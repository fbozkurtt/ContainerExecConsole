using Docker.DotNet;
using Docker.DotNet.Models;
using ProtoBuf;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Docker.DotNet.MultiplexedStream;

namespace ContainerExecConsole
{
    public class Program
    {
        private static readonly IDockerClient _dockerClient;
        private static MultiplexedStream _stream;
        private static readonly byte[] ExecutionTimedOut = Encoding.UTF8.GetBytes("\n(Execution timed out)");
        private static readonly byte[] StartOfOutputNotFound = Encoding.UTF8.GetBytes("\n(Could not find start of output)");
        private static readonly byte[] UnexpectedEndOfOutput = Encoding.UTF8.GetBytes("\n(Unexpected end of output)");

        static Program()
        {
            _dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).
                CreateClient();
        }
        static void Main(string[] args)
        {
            //if (args.Length == 0)
            //    throw new ArgumentNullException("containerId");

            var containerId = args?.Length >= 1 ? args[0] : "busybox";

            var execId = Task.Run(async ()
                => await _dockerClient.Exec.ExecCreateContainerAsync(
                    containerId,
                    new ContainerExecCreateParameters()
                    {
                        AttachStderr = true,
                        AttachStdin = true,
                        AttachStdout = true,
                        Tty = true,
                        Cmd = new List<string>() { "sh" }
                    }
                    )).Result.ID;

            _stream = Task.Run(async ()
                => await _dockerClient.Exec.StartAndAttachContainerExecAsync(execId, true)).Result;

            byte[]? bodyBytes = null;
            byte[]? outputBuffer = null;
            var input = string.Empty;
            while (true)
            {
                while (true)
                {
                    var c = Console.ReadKey();
                    if (c.Key == ConsoleKey.Enter)
                    {
                        if (input.Length == 0)
                            continue;

                        bodyBytes = ArrayPool<byte>.Shared.Rent(input.Length + 1);
                        outputBuffer = ArrayPool<byte>.Shared.Rent(1024);

                        var contentBytes = Encoding.UTF8.GetBytes(input + '\n');
                        var contentStream = new MemoryStream(contentBytes);
                        var memoryStream = new MemoryStream(bodyBytes);

                        var outputStartMarker = Guid.NewGuid();
                        var outputEndMarker = Guid.NewGuid();

                        Task.Run(async () => await contentStream.CopyToAsync(memoryStream));
                        Task.Run(async () =>
                        {
                            var s = new MemoryStream();
                            Serializer.SerializeWithLengthPrefix(s, new ExecuteCommand(bodyBytes, outputStartMarker, outputEndMarker), PrefixStyle.Base128);
                            s.Seek(0, SeekOrigin.Begin);
                            await _stream.CopyFromAsync(s, CancellationToken.None);
                            //_stream.CloseWrite();
                        }).Wait();

                        Task.Run(async () =>
                        {
                            const int OutputMarkerLength = 36; // length of guid
                            byte[]? outputStartMarkerBytes = null;
                            byte[]? outputEndMarkerBytes = null;

                            outputStartMarkerBytes = ArrayPool<byte>.Shared.Rent(OutputMarkerLength);
                            outputEndMarkerBytes = ArrayPool<byte>.Shared.Rent(OutputMarkerLength);

                            Utf8Formatter.TryFormat(outputStartMarker, outputStartMarkerBytes, out _);
                            Utf8Formatter.TryFormat(outputEndMarker, outputEndMarkerBytes, out _);

                            var result = await ReadOutputAsync(
                                outputBuffer,
                                outputStartMarkerBytes.AsMemory(0, OutputMarkerLength),
                                outputEndMarkerBytes.AsMemory(0, OutputMarkerLength),
                                CancellationToken.None
                            );

                            if (outputStartMarkerBytes != null)
                                ArrayPool<byte>.Shared.Return(outputStartMarkerBytes);
                            if (outputEndMarkerBytes != null)
                                ArrayPool<byte>.Shared.Return(outputEndMarkerBytes);
                        }).Wait();

                        if (bodyBytes != null)
                            ArrayPool<byte>.Shared.Return(bodyBytes);
                        if (outputBuffer != null)
                            ArrayPool<byte>.Shared.Return(outputBuffer);
                    }
                    else
                    {
                        input += c.KeyChar;
                    }
                }
            }
        }

        private static async Task<ExecutionOutputResult> ReadOutputAsync(
            byte[] outputBuffer,
            ReadOnlyMemory<byte> outputStartMarker,
            ReadOnlyMemory<byte> outputEndMarker,
            CancellationToken cancellationToken
        )
        {
            var currentIndex = 0;
            var outputStartIndex = -1;
            var outputEndIndex = -1;
            var nextStartMarkerIndexToCompare = 0;
            var nextEndMarkerIndexToCompare = 0;
            var cancelled = false;

            while (outputEndIndex < 0)
            {
                var (read, readCancelled) = await ReadWithCancellationAsync(outputBuffer, currentIndex, outputBuffer.Length - currentIndex, cancellationToken);
                if (readCancelled)
                {
                    cancelled = true;
                    break;
                }

                if (read.EOF)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }

                if (outputStartIndex == -1)
                {
                    var startMarkerEndIndex = GetMarkerEndIndex(
                        outputBuffer, currentIndex, read.Count,
                        outputStartMarker, ref nextStartMarkerIndexToCompare
                    );
                    if (startMarkerEndIndex != -1)
                        outputStartIndex = startMarkerEndIndex;
                }

                // cannot be else if -- it might have changed inside previous if
                if (outputStartIndex != -1)
                {
                    var endMarkerEndIndex = GetMarkerEndIndex(
                        outputBuffer, currentIndex, read.Count,
                        outputEndMarker, ref nextEndMarkerIndexToCompare
                    );
                    if (endMarkerEndIndex != -1)
                        outputEndIndex = endMarkerEndIndex - outputEndMarker.Length;
                }

                currentIndex += read.Count;
                if (currentIndex >= outputBuffer.Length)
                    break;
            }

            if (outputStartIndex < 0)
                return new(outputBuffer.AsMemory(0, currentIndex), StartOfOutputNotFound);
            if (cancelled)
                return new(outputBuffer.AsMemory(outputStartIndex, currentIndex - outputStartIndex), ExecutionTimedOut);
            if (outputEndIndex < 0)
                return new(outputBuffer.AsMemory(outputStartIndex, currentIndex - outputStartIndex), UnexpectedEndOfOutput);

            return new(outputBuffer.AsMemory(outputStartIndex, outputEndIndex - outputStartIndex));
        }

        private static async Task<(ReadResult result, bool cancelled)> ReadWithCancellationAsync(byte[] buffer, int index, int count, CancellationToken cancellationToken)
        {
            var cancellationTaskSource = new TaskCompletionSource<object?>();
            using var _ = cancellationToken.Register(() => cancellationTaskSource.SetResult(null));

            var result = await Task.WhenAny(
                _stream.ReadOutputAsync(buffer, index, count, cancellationToken),
                cancellationTaskSource.Task
            );
            if (result == cancellationTaskSource.Task)
                return (default, true);

            var output = Encoding.UTF8.GetString(buffer);
            Console.WriteLine(output);
            return (await (Task<ReadResult>)result, false);
        }

        private static int GetMarkerEndIndex(byte[] outputBuffer, int currentIndex, int length, ReadOnlyMemory<byte> marker, ref int nextMarkerIndexToCompare)
        {
            var markerEndIndex = -1;
            var searchEndIndex = currentIndex + length;
            for (var i = currentIndex; i < searchEndIndex; i++)
            {
                if (outputBuffer[i] != marker.Span[nextMarkerIndexToCompare])
                {
                    nextMarkerIndexToCompare = outputBuffer[i] == marker.Span[0] ? 1 : 0;
                    continue;
                }

                nextMarkerIndexToCompare += 1;
                if (nextMarkerIndexToCompare == marker.Length)
                {
                    markerEndIndex = i + 1;
                    break;
                }
            }
            return markerEndIndex;
        }

        public readonly struct ExecutionOutputResult
        {
            public ExecutionOutputResult(ReadOnlyMemory<byte> output)
            {
                Output = output;
                OutputReadFailureMessage = default;
            }

            public ExecutionOutputResult(ReadOnlyMemory<byte> output, ReadOnlyMemory<byte> outputReadFailureMessage)
            {
                Output = output;
                OutputReadFailureMessage = outputReadFailureMessage;
            }

            public ReadOnlyMemory<byte> Output { get; }
            public ReadOnlyMemory<byte> OutputReadFailureMessage { get; }
            public bool IsOutputReadSuccess => OutputReadFailureMessage.IsEmpty;
        }
    }
}
