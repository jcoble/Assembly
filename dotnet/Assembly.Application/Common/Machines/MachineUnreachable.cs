namespace Assembly.Application.Common.Machines;

/// <summary>
/// A remote machine didn't answer, or answered in a way this one can't use. The routes reply 502 with the message, and the
/// socket answers <c>unreachable</c> with the machine's name, so the window can mark that machine.
/// </summary>
public sealed class MachineUnreachable(string machine, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Machine { get; } = machine;
}
