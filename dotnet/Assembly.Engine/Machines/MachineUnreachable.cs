namespace Assembly.Engine.Machines;

/// <summary>A remote machine didn't answer, or answered in a way this one can't use. The routes reply 502 with the message.</summary>
public sealed class MachineUnreachable(string message, Exception? inner = null) : Exception(message, inner);
