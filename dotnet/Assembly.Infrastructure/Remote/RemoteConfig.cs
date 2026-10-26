namespace Assembly.Infrastructure.Remote;

/// <summary>A remote machine the Mac shows threads of: its name, where its API answers here, and its token.</summary>
public sealed record RemoteConfig(string Name, Uri Address, string Token);
