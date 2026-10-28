namespace Assembly.Application.Common.Machines;

/// <summary>Saved connection settings; the bearer token is read over SSH and never stored here.</summary>
public sealed record SavedMachine(string Name, string Ssh, int Port = 9797, string TokenFile = ".config/assembly-dotnet/env");
