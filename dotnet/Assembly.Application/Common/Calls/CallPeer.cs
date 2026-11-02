namespace Assembly.Application.Common.Calls;

/// <summary>Authenticated server-owned connection identity, never deserialized from call arguments.</summary>
public abstract record CallPeer;
public sealed record OwnerPeer : CallPeer;
public sealed record PhonePeer(string DeviceId) : CallPeer;
