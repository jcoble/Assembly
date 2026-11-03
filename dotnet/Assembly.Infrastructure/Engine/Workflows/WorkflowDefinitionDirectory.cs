using System.Text;
using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>An explicit directory read using the sole workflow loader and its containment rules.</summary>
public static class WorkflowDefinitionDirectory
{
    private const string Prefix = ".assembly/workflows/";
    private const int ItemBudget = 32 * 1024;
    private const int PageBudget = 1024 * 1024;

    public static WorkflowDefinitionPage List(string projectRoot, string? after = null, int limit = 50) =>
        List(projectRoot, after, limit, CancellationToken.None);

    public static WorkflowDefinitionPage List(string projectRoot, string? after, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 200) throw new ArgumentException("limit must be between 1 and 200", nameof(limit));
        if (after is not null && (!after.StartsWith(Prefix, StringComparison.Ordinal) ||
            after[Prefix.Length..].IndexOfAny(['/', '\\']) >= 0 || Path.GetExtension(after) != ".yml"))
            throw new ArgumentException("invalid_cursor", nameof(after));
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot))
            throw new WorkflowFieldException("project", "an explicit absolute project root is required");
        var root = WorkflowPaths.Canonical(projectRoot, "project");
        if (!Directory.Exists(root)) throw new WorkflowFieldException("project", "project root is not an available directory");
        var directory = WorkflowPaths.Contained(root, ".assembly/workflows", "path");
        if (File.Exists(directory)) throw new WorkflowFieldException("path", "workflow directory is a file");
        if (!Directory.Exists(directory)) return new([], null);

        // Enumeration order is not stable. Retain only the next bounded ordinal path window.
        SortedSet<string> paths = new(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetExtension(file) != ".yml") continue;
                var path = Prefix + Path.GetFileName(file);
                if (after is not null && StringComparer.Ordinal.Compare(path, after) <= 0) continue;
                paths.Add(path);
                if (paths.Count > limit + 1) paths.Remove(paths.Max!);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new WorkflowFieldException("path", ErrorText(error)); }

        List<WorkflowDefinitionDisplay> items = []; var bytes = 4096;
        foreach (var path in paths.Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Read(root, path, cancellationToken);
            var size = JsonSerializer.SerializeToUtf8Bytes(item, WorkflowDefinitionReadJson.Default.WorkflowDefinitionDisplay).Length;
            if (bytes + size + 1 > PageBudget) break;
            items.Add(item); bytes += size + 1;
        }
        return new(items, items.Count < paths.Count && items.Count > 0 ? items[^1].Path : null);
    }

    private static WorkflowDefinitionDisplay Read(string root, string path, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = WorkflowLoader.Load(root, path, cancellationToken);
            var item = new WorkflowDefinitionDisplay(path, snapshot.Definition.Name, snapshot.Hash, []);
            if (Encoding.UTF8.GetByteCount(item.Name!) > ItemBudget ||
                JsonSerializer.SerializeToUtf8Bytes(item, WorkflowDefinitionReadJson.Default.WorkflowDefinitionDisplay).Length > ItemBudget)
                return item with { Name = null, Errors = ["name: display exceeds the 32 KiB UTF-8 budget"] };
            return item;
        }
        catch (WorkflowFieldException error) { return new(path, null, null, [ErrorText(error)]); }
    }

    private static string ErrorText(Exception error)
    {
        const int maximum = 2048;
        if (Encoding.UTF8.GetByteCount(error.Message) <= maximum) return error.Message;
        var text = new StringBuilder(); var bytes = 0;
        foreach (var rune in error.Message.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximum - 3) break;
            text.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return text.Append('…').ToString();
    }
}
