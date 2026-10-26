namespace Assembly.Infrastructure.Http;

/// <summary>A call that can't be done as asked; it answers 400 with the message as its error.</summary>
public sealed class CallFailed(string message) : Exception(message);
