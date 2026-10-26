namespace Assembly.Application.Common.Database;

/// <summary>One SQL parameter: a string, long, double, byte[] or null.</summary>
public readonly record struct SqlArg(string Name, object? Value);
