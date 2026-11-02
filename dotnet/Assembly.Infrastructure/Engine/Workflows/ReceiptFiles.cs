using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Infrastructure.Engine.Workflows;

internal sealed record CheckedReceiptFile(string Path, string RootKind, string Sha256, long Bytes);

/// <summary>File references are data; no pattern or path is evaluated by a shell.</summary>
internal static class ReceiptFiles
{
    internal static bool Inside(string root, string path) => path == root || path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string Contained(string root, string path, string field)
    {
        root = WorkflowPaths.Canonical(root, field);
        if (path.Split('/', '\\').Any(p => p == "..")) throw new WorkflowFieldException(field, "path traversal");
        var full = WorkflowPaths.Canonical(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path), field);
        if (!Inside(root, full) || !File.Exists(full)) throw new WorkflowFieldException(field, "file missing or outside its root");
        return full;
    }

    internal static async Task<CheckedReceiptFile> Observe(string root, string path, string rootKind, string field, CancellationToken ct)
    {
        var full = Contained(root, path, field);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = stream.Length;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (stream.Length != bytes) throw new IOException("receipt file changed while reading");
        return new(Path.GetRelativePath(root, full).Replace('\\', '/'), rootKind, hash, bytes);
    }

    internal static async Task<IReadOnlyList<CheckedReceiptFile>> Expand(string workspace, string proofDir,
        IReadOnlyList<string> patterns, CancellationToken ct)
    {
        workspace = WorkflowPaths.Canonical(workspace); proofDir = WorkflowPaths.Canonical(proofDir);
        var found = new Dictionary<(string Root, string Path), CheckedReceiptFile>();
        foreach (var pattern in patterns)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Split('/', '\\').Any(p => p == ".."))
                throw new WorkflowFieldException("expect_files", "invalid pattern");
            var absolute = Path.IsPathFullyQualified(pattern);
            var root = absolute ? proofDir : workspace;
            var relative = absolute ? Path.GetRelativePath(proofDir, pattern) : pattern;
            if (relative.Split('/', '\\').Any(p => p == "..")) throw new WorkflowFieldException("expect_files", "pattern escapes root");
            var parts = relative.Replace('\\', '/').Split('/');
            var matches = 0;
            foreach (var path in Match(root, root, parts, 0, ct).Order(StringComparer.Ordinal))
            {
                matches++;
                var observed = await Observe(root, path, absolute ? "proof" : "workspace", "expect_files", ct);
                found[(observed.RootKind, observed.Path)] = observed;
            }
            if (matches == 0) throw new WorkflowFieldException("expect_files", "pattern has no existing regular file: " + pattern);
        }
        return found.Values.OrderBy(f => f.RootKind, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> Match(string root, string current, string[] parts, int index, CancellationToken ct)
    {
        if (index == parts.Length) { if (File.Exists(current)) yield return current; yield break; }
        if (!Directory.Exists(current)) yield break;
        var expression = Glob(parts[index]);
        foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!expression.IsMatch(Path.GetFileName(entry))) continue;
            var canonical = WorkflowPaths.Canonical(entry, "expect_files");
            if (!Inside(root, canonical)) throw new WorkflowFieldException("expect_files", "symlink escapes root");
            foreach (var match in Match(root, canonical, parts, index + 1, ct)) yield return match;
        }
    }

    private static Regex Glob(string component)
    {
        var pattern = new StringBuilder("\\A");
        for (var i = 0; i < component.Length; i++)
        {
            var c = component[i];
            if (c == '*') pattern.Append(".*");
            else if (c == '?') pattern.Append('.');
            else if (c == '[')
            {
                var end = component.IndexOf(']', i + 1);
                if (end < 0) throw new WorkflowFieldException("expect_files", "unclosed character class");
                var body = component[(i + 1)..end];
                if (body.Length == 0 || body.Contains('\\') || body.Contains('['))
                    throw new WorkflowFieldException("expect_files", "invalid character class");
                pattern.Append('[');
                if (body[0] == '!') pattern.Append('^').Append(body.AsSpan(1));
                else pattern.Append(body);
                pattern.Append(']'); i = end;
            }
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        pattern.Append("\\z");
        try { return new(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { throw new WorkflowFieldException("expect_files", ex.Message); }
    }

    internal static void HashText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)bytes.LongLength);
        hash.AppendData(length); hash.AppendData(bytes);
    }
}
