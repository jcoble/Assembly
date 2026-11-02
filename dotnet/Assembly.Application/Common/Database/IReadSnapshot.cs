namespace Assembly.Application.Common.Database;

/// <summary>A read-only transaction. Its first read fixes the view until disposal.</summary>
public interface IReadSnapshot : IReadScope, IDisposable;
