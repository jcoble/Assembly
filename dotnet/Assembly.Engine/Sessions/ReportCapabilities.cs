namespace Assembly.Engine.Sessions;

/// <summary>Capabilities observed from the owning adapter, never a user setting.</summary>
public sealed record ReportCapabilities(bool CanSteer) : SessionCommand;
