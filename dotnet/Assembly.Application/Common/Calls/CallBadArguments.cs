namespace Assembly.Application.Common.Calls;

/// <summary>A validated call whose arguments cannot describe the requested operation.</summary>
public sealed class CallBadArguments(string message) : Exception(message);
