using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

internal sealed class GitHistoryTestRepository : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "assembly-history-" + Guid.NewGuid());
    public GitCli Git { get; } = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        await Run("init", "-b", "main");
        await Run("config", "user.name", "History Test");
        await Run("config", "user.email", "history@example.invalid");
        await Run("config", "commit.gpgsign", "false");
    }

    public async Task Run(params string[] args)
    {
        await foreach (var _ in Git.ReadAsync(Root, args, null, CancellationToken.None)) { }
    }

    public Task Commit(string subject) => Run("-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", subject);

    public async Task<string> Head()
    {
        var output = new List<string>();
        await foreach (var item in Git.ReadAsync(Root, ["rev-parse", "HEAD"], null, CancellationToken.None)) output.Add(item);
        return string.Concat(output).Trim();
    }

    public async Task Write(string path, string content)
    {
        var absolute = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content);
        await Run("--literal-pathspecs", "add", "--", path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
