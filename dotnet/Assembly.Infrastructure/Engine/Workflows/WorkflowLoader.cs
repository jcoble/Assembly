using System.Collections.Immutable;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Infrastructure.Engine.Workflows;

public static class WorkflowLoader
{
    public static WorkflowSnapshot Load(string projectRoot, string relativePath) => Load(projectRoot, relativePath, CancellationToken.None);

    public static WorkflowSnapshot Load(string projectRoot, string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetDirectoryName(relativePath)?.Replace('\\', '/') != ".assembly/workflows" || Path.GetExtension(relativePath) != ".yml")
            throw new WorkflowFieldException("path", "workflow must be .assembly/workflows/*.yml");
        var root = WorkflowPaths.Canonical(projectRoot);
        var workflowRoot = WorkflowPaths.Contained(root, ".assembly/workflows", "path");
        var path = WorkflowPaths.Contained(workflowRoot, Path.GetFileName(relativePath), "path");
        var yaml = WorkflowPaths.Read(path, "yaml", cancellationToken);
        var prompts = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var reference in WorkflowParser.PromptPaths(yaml, cancellationToken))
        {
            if (prompts.ContainsKey(reference.Path)) continue;
            var promptPath = WorkflowPaths.Contained(Path.GetDirectoryName(path)!, reference.Path, reference.Field);
            prompts.Add(reference.Path, WorkflowPaths.Read(promptPath, reference.Field, cancellationToken));
        }
        return WorkflowParser.Parse(new(yaml, prompts.ToImmutable()), cancellationToken);
    }
}
