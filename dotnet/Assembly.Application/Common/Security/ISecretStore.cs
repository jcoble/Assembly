namespace Assembly.Application.Common.Security;

public interface ISecretStore
{
    Task<byte[]?> GetAsync(string service, string account, CancellationToken ct);
    Task PutAsync(string service, string account, ReadOnlyMemory<byte> value, CancellationToken ct);
    Task DeleteAsync(string service, string account, CancellationToken ct);
}
