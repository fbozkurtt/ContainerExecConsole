using ProtoBuf;

namespace ContainerExecConsole
{
    [ProtoContract]
    [ProtoInclude(10, typeof(ExecuteCommand))]
    public abstract class StdinCommand
    {
    }
}