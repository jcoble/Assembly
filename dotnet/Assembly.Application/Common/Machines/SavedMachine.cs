namespace Assembly.Application.Common.Machines;

/// <summary>Saved connection settings; the bearer token is read over SSH and never stored here.</summary>
public sealed record SavedMachine(string Name, string Ssh, int Port = 9797, string TokenFile = SavedMachine.InstalledTokenFile)
{
    /// <summary>The one token file an install has. A machine saved with another keeps connecting with it, and can't be installed on.</summary>
    public const string InstalledTokenFile = ".config/assembly-host/env";

    /// <summary>Why this app won't install or update anything on this machine, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CantUpdate => TokenFile == InstalledTokenFile ? null :
        $"Saved with the token file {TokenFile}, not an install's own, so this app can't install or update here. Clear the token file in Settings to install.";
}
