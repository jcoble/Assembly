namespace Assembly.Application.Common.GitHub;

/// <summary>A completed hosted write supplied no usable confirmation; refresh instead of replaying it.</summary>
public sealed class GhUnconfirmed(string message) : Exception(message);
