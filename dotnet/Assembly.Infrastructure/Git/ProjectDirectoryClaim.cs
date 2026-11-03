using System.ComponentModel;
using System.Runtime.InteropServices;
using Assembly.Application.Common.Calls;
using Microsoft.Win32.SafeHandles;

namespace Assembly.Infrastructure.Git;

/// <summary>Exclusive New Project reservation and retained directory identities; never removes user files.</summary>
internal sealed class ProjectDirectoryClaim : IDisposable
{
    private readonly Lock _gate = new();
    private readonly DirectoryHandle _parent, _destination;
    private readonly Identity _parentIdentity, _destinationIdentity;
    private bool _disposed;
    internal string Parent { get; }
    internal string Destination { get; }

    private ProjectDirectoryClaim(string parent, string destination, DirectoryHandle parentHandle,
        Identity parentIdentity, DirectoryHandle destinationHandle, Identity destinationIdentity)
    {
        Parent = parent; Destination = destination;
        _parent = parentHandle; _parentIdentity = parentIdentity;
        _destination = destinationHandle; _destinationIdentity = destinationIdentity;
    }

    internal static ProjectDirectoryClaim Reserve(string canonicalParent, string name)
    {
        if (!Path.IsPathFullyQualified(canonicalParent) || canonicalParent.Contains('\0'))
            throw new CallFailed("Folder path must be absolute.");
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new CallFailed("Choose one folder name, without path separators.");
        var parent = Open(canonicalParent);
        DirectoryHandle? destination = null;
        try
        {
            var parentIdentity = Read(parent);
            RequirePath(canonicalParent, parentIdentity);
            var path = Path.Combine(canonicalParent, name);
            if (mkdir(path, 0x1ff) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new CallFailed(error == 17 ? "That destination already exists. Choose another folder name."
                    : "The folder could not be created: " + new Win32Exception(error).Message);
            }
            // mkdir is exclusive, but returns no descriptor. Claims begin at this captured identity.
            destination = Open(path);
            var identity = Read(destination);
            RequirePath(canonicalParent, parentIdentity); RequirePath(path, identity);
            return new(canonicalParent, path, parent, parentIdentity, destination, identity);
        }
        catch
        {
            destination?.Dispose(); parent.Dispose();
            throw;
        }
    }

    internal void RequireCurrent()
    {
        lock (_gate)
        {
            if (_disposed) throw Changed();
            if (Read(_parent) != _parentIdentity || Read(_destination) != _destinationIdentity) throw Changed();
            RequirePath(Parent, _parentIdentity); RequirePath(Destination, _destinationIdentity);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _destination.Dispose(); _parent.Dispose();
        }
    }

    private readonly record struct Identity(ulong Device, ulong Inode);
    private static DirectoryHandle Open(string path)
    {
        var handle = opendir(path);
        if (handle == IntPtr.Zero) throw Changed();
        return new(handle);
    }
    private static Identity Read(DirectoryHandle directory) => ReadStat(null, dirfd(directory));
    private static void RequirePath(string path, Identity expected)
    {
        if (ReadStat(path, -1) != expected) throw Changed();
    }
    private static Identity ReadStat(string? path, int fd)
    {
        // Native stat layouts on our 64-bit Mac/Linux targets; never interprets an unsupported layout.
        var mac = OperatingSystem.IsMacOS();
        var arch = RuntimeInformation.ProcessArchitecture;
        if ((!mac && !OperatingSystem.IsLinux()) || arch is not (Architecture.X64 or Architecture.Arm64))
            throw new CallFailed("Directory identity checks are unavailable on this platform.");
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var result = mac && arch == Architecture.X64
                ? path is null ? fstat64(fd, buffer) : lstat64(path, buffer)
                : path is null ? fstat(fd, buffer) : lstat(path, buffer);
            if (result != 0) throw Changed();
            var mode = mac ? (uint)(ushort)Marshal.ReadInt16(buffer, 4)
                : (uint)Marshal.ReadInt32(buffer, arch == Architecture.X64 ? 24 : 16);
            if ((mode & 0xf000) != 0x4000) throw Changed(); // A symlink is never the captured directory.
            var device = mac ? (uint)Marshal.ReadInt32(buffer) : (ulong)Marshal.ReadInt64(buffer);
            return new(device, (ulong)Marshal.ReadInt64(buffer, 8));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static CallFailed Changed() => new("The project folder or its parent changed. It was not confirmed for registration; inspect the folder before adding it.");
    private sealed class DirectoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DirectoryHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => closedir(handle) == 0;
    }
    [DllImport("libc", SetLastError = true)] private static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr opendir([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc")] private static extern int closedir(IntPtr directory);
    [DllImport("libc")] private static extern int dirfd(DirectoryHandle directory);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(int fd, IntPtr stat);
    [DllImport("libc", SetLastError = true)] private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int fstat64(int fd, IntPtr stat);
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)] private static extern int lstat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr stat);
}
