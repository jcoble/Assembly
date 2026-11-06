using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Infrastructure.Git;

/// <summary>The normal Git writer; New Project preparation consumes these same initialization and clone methods.</summary>
public sealed partial class GitWriter(IGit git)
{
    public async Task<GitWriteResult> InitAsync(string root, CancellationToken ct)
    {
        var entry = new FileInfo(Path.Combine(root, ".git"));
        if (entry.Exists || entry.LinkTarget is not null || Directory.Exists(entry.FullName))
            throw new CallFailed("This folder already has a git repository.");
        var run = await git.RunAsync(root, ["init"], null, null, ct);
        if (run.ExitCode != 0) throw GitErrors.From("init", run);
        return new("Initialized repository", await new GitStatusReader(git).ReadAsync(root, ct));
    }

    public async Task<GitWriteResult> CloneAsync(string source, string destination, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Contains('\0')) throw new CallFailed("A repository URL or path is required.");
        var parent = Path.GetDirectoryName(destination) ?? throw new CallFailed("Folder path must be absolute");
        var run = await git.RunAsync(parent, ["clone", "--", source, destination], null, TimeSpan.FromSeconds(120), ct);
        if (run.ExitCode != 0)
        {
            var safe = run with { Output = WithoutSource(run.Output, source), Error = WithoutSource(run.Error, source) };
            throw GitErrors.From("clone", safe);
        }
        return new("Copied repository", await new GitStatusReader(git).ReadAsync(destination, ct));
    }
    private static string WithoutSource(string text, string source)
    {
        text = text.Replace(source, "[repository]", StringComparison.Ordinal);
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
        {
            foreach (var value in uri.UserInfo.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                text = text.Replace(value, "[private]", StringComparison.Ordinal);
                text = text.Replace(Uri.UnescapeDataString(value), "[private]", StringComparison.Ordinal);
            }
        }
        return text;
    }
}
