using System.Runtime.InteropServices;
using Assembly.Application.Common.Security;
using Assembly.Application.Features.Notion;

namespace Assembly.Infrastructure.Security;

/// <summary>Only exact service/account operations; never enumerates the owner's Keychain.</summary>
public sealed class MacKeychain : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly Lazy<nint> SecurityLibrary = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CoreLibrary = new(() => NativeLibrary.Load(Core));
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public Task<byte[]?> GetAsync(string service, string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireMac();
        using var query = Query(service, account);
        CFDictionarySetValue(query.Value, Key("kSecReturnData"), Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreLibrary.Value, "kCFBooleanTrue")));
        var status = SecItemCopyMatching(query.Value, out var result);
        if (status == -25300) return Task.FromResult<byte[]?>(null);
        Check(status);
        using var value = new Owned(result);
        var bytes = new byte[checked((int)CFDataGetLength(result))];
        Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length);
        return Task.FromResult<byte[]?>(bytes);
    }
    public Task PutAsync(string service, string account, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireMac();
        using var query = Query(service, account);
        var bytes = value.ToArray();
        try
        {
            using var data = new Owned(CFDataCreate(0, bytes, bytes.Length));
            using var changes = new Owned(CFDictionaryCreateMutable(0, 0, 0, 0));
            CFDictionarySetValue(changes.Value, Key("kSecValueData"), data.Value);
            var status = SecItemUpdate(query.Value, changes.Value);
            if (status == -25300)
            {
                CFDictionarySetValue(query.Value, Key("kSecValueData"), data.Value);
                status = SecItemAdd(query.Value, 0);
            }
            Check(status);
            return Task.CompletedTask;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    public Task DeleteAsync(string service, string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireMac();
        using var query = Query(service, account);
        var status = SecItemDelete(query.Value);
        if (status != -25300) Check(status);
        return Task.CompletedTask;
    }
    private static Owned Query(string service, string account)
    {
        var query = new Owned(CFDictionaryCreateMutable(0, 0, 0, 0));
        // The dictionary does not retain values; Owned keeps these strings alive until query disposal.
        query.Service = CFStringCreateWithCString(0, service, 0x08000100);
        query.Account = CFStringCreateWithCString(0, account, 0x08000100);
        CFDictionarySetValue(query.Value, Key("kSecClass"), Key("kSecClassGenericPassword"));
        CFDictionarySetValue(query.Value, Key("kSecAttrService"), query.Service);
        CFDictionarySetValue(query.Value, Key("kSecAttrAccount"), query.Account);
        return query;
    }
    private static nint Key(string key) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary.Value, key));
    private static void RequireMac() { if (!OperatingSystem.IsMacOS()) throw new NotionException("unsupported_platform"); }
    private static void Check(int status) { if (status != 0) throw new NotionException("keychain_unavailable"); }
    private sealed class Owned(nint value) : IDisposable
    {
        public nint Value { get; } = value;
        public nint Service, Account;
        public void Dispose() { CFRelease(Value); if (Service != 0) CFRelease(Service); if (Account != 0) CFRelease(Account); }
    }
    [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(Security)] private static extern int SecItemAdd(nint query, nint result);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint changes);
    [DllImport(Security)] private static extern int SecItemDelete(nint query);
    [DllImport(Core)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(Core)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(Core)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(Core)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(Core)] private static extern nint CFDataGetLength(nint data);
    [DllImport(Core)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(Core)] private static extern void CFRelease(nint value);
}
