using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Infrastructure.Engine.Workflows;

internal static class WorkflowPaths
{
    internal static string Canonical(string path, string field = "path")
    {
        try
        {
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)!.FullName;
            }
            return Path.GetFullPath(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new WorkflowFieldException(field, ex.Message); }
    }

    internal static string Contained(string root, string relative, string field)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new WorkflowFieldException(field, "path must be relative without traversal");
        var canonical = Canonical(Path.Combine(root, relative), field);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new WorkflowFieldException(field, "path escapes its directory");
        return canonical;
    }

    internal static string Read(string path, string field, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(path, new System.Text.UTF8Encoding(false, true), false);
            var text = new System.Text.StringBuilder();
            var buffer = new char[8192];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = reader.Read(buffer, 0, buffer.Length);
                if (count == 0) return text.ToString();
                text.Append(buffer, 0, count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { throw new WorkflowFieldException(field, ex.Message); }
    }
}
