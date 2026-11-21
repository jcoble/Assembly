using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

/// <summary>Checks git's streamed output and failures in temporary folders.</summary>
public sealed class GitCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assembly-git-" + Guid.NewGuid());

    // Pins: scm#16
    [Fact]
    public async Task Large_record_is_yielded_in_one_mebibyte_pieces()
    {
        Directory.CreateDirectory(_root);
        await Read("init", "-b", "main");
        var content = new string('x', 1536 * 1024);
        await File.WriteAllTextAsync(Path.Combine(_root, "large"), content);
        var hash = Assert.Single(await Read("hash-object", "-w", "large")).Trim();
        var records = await Read("cat-file", "-p", hash);
        Assert.Equal(2, records.Count);
        Assert.Equal(1024 * 1024, records[0].Length);
        Assert.Equal(content.Length, records.Sum(record => record.Length));
        Assert.Equal(content, string.Concat(records));
    }

    private async Task<List<string>> Read(params string[] arguments)
    {
        var records = new List<string>();
        await foreach (var record in new GitCli().ReadAsync(_root, arguments, null, CancellationToken.None))
            records.Add(record);
        return records;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
