namespace Assembly.Application.Common.Calls;

/// <summary>A dispatched Git write has no confirmed reply; read its existing operation, never replay it.</summary>
public sealed class CallUncertain(string machine, string message = "This may have completed. Refresh before trying again.") : Exception(message)
{
    public string Machine { get; } = machine;
}
