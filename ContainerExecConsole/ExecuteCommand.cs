using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ContainerExecConsole
{
    [ProtoContract(SkipConstructor = true)]
    public class ExecuteCommand : StdinCommand
    {
        public ExecuteCommand(byte[] assemblyBytes, Guid outputStartMarker, Guid outputEndMarker)
        {
            AssemblyBytes = assemblyBytes;
            OutputStartMarker = outputStartMarker;
            OutputEndMarker = outputEndMarker;
        }

        [ProtoMember(1, Options = MemberSerializationOptions.OverwriteList | MemberSerializationOptions.Packed)]
        public byte[] AssemblyBytes { get; }
        [ProtoMember(2)]
        public Guid OutputStartMarker { get; }
        [ProtoMember(3)]
        public Guid OutputEndMarker { get; }
    }
}
