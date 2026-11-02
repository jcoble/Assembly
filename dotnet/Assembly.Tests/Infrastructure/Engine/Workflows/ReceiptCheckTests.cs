using System.Text.Json;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Infrastructure.Engine.Workflows;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Infrastructure.Engine.Workflows;

public sealed class ReceiptCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("workflow-receipts-").FullName;
    private readonly GitCli _git = new();
    private string Repo => Path.Combine(_root, "checkout");
    private string Proof => Path.Combine(_root, "proof");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_size_dirty_or_untracked_rewrite_changes_nonbuild_fingerprint(bool untracked)
    {
        await Init();
        var file = Path.Combine(Repo, untracked ? "untracked.txt" : "src.txt");
        await File.WriteAllTextAsync(file, "aaa");
        var start = await Context();
        await File.WriteAllTextAsync(file, "bbb");
        var now = await new ReceiptCheckout().Capture(Repo, default);
        Assert.Equal(start.FrozenHead, now.Head);
        Assert.NotEqual(start.StartFingerprint, now.Fingerprint);
        var result = await Check(start with { ReportFingerprint = now.Fingerprint }, "recon", Recon);
        Assert.Equal("changed_code", result.Error);
    }

    [Fact]
    public async Task Mutation_after_report_is_rejected_even_for_build()
    {
        await Init(); var context = await Context();
        await File.WriteAllTextAsync(Path.Combine(Repo, "src.txt"), "changed after report");
        Assert.Equal("changed_code", (await Check(context, "build", """{"commits":[],"tests_added":[],"summary":"No change"}""")).Error);
    }

    [Fact]
    public async Task Build_checks_actual_new_commits_and_requires_complete_list()
    {
        await Init(); var before = await Context();
        await File.WriteAllTextAsync(Path.Combine(Repo, "tests.cs"), "fixture test");
        await Git("add", "."); await Git("commit", "-m", "change");
        var current = await Context(); current = current with { AttemptBaseHead = before.AttemptBaseHead, StartFingerprint = before.StartFingerprint };
        var missing = await Check(current, "build", """{"commits":[],"tests_added":[],"summary":"omitted"}""");
        Assert.Equal("commits", missing.Error);
        var valid = JsonSerializer.Serialize(new { commits = new[] { current.FrozenHead }, tests_added = new[] { "tests.cs" }, summary = "change" });
        Assert.Null((await Check(current, "build", valid)).Error);
        var old = JsonSerializer.Serialize(new { commits = new[] { before.FrozenHead }, tests_added = new[] { "tests.cs" }, summary = "wrong old commit" });
        Assert.Equal("commits", (await Check(current, "build", old)).Error);
    }

    [Fact]
    public async Task Recon_validates_actual_lines_and_not_only_path_presence()
    {
        await Init(); var context = await Context();
        Assert.Null((await Check(context, "recon", Recon)).Error);
        Assert.Equal("existing", (await Check(context, "recon", Recon.Replace("src.txt:2", "src.txt:9"))).Error);
        Assert.Equal("existing", (await Check(context, "recon", Recon.Replace("src.txt:2", "../outside:2"))).Error);
    }

    [Fact]
    public async Task Review_checks_actual_prior_findings_and_frozen_head_independently_of_verdict()
    {
        await Init(); var context = await Context();
        var body = Review(context.FrozenHead, "earlier");
        Assert.Equal("grades_of_earlier", (await Check(context, "review", body)).Error);
        Assert.Null((await Check(context with { EarlierFindingTexts = ["earlier"] }, "review", body)).Error);
        Assert.Equal("head", (await Check(context with { EarlierFindingTexts = ["earlier"] }, "review", Review(new string('f', 40), "earlier"))).Error);
    }

    [Fact]
    public async Task Proof_head_and_managed_file_failures_name_the_exact_reason()
    {
        await Init(); var context = await Context();
        await File.WriteAllTextAsync(Path.Combine(Proof, "shot.png"), "proof bytes");
        Assert.Null((await Check(context, "proof", ProofBody(context.FrozenHead, "shot.png"))).Error);
        Assert.Equal("proof_head_mismatch", (await Check(context, "proof", ProofBody(new string('f', 40), "shot.png"))).Error);
        Assert.Equal("proof_file_missing", (await Check(context, "proof", ProofBody(context.FrozenHead, "absent.png"))).Error);
    }

    [Fact]
    public async Task Expected_files_expand_workspace_and_managed_proof_with_deterministic_hashes()
    {
        await Init();
        await File.WriteAllTextAsync(Path.Combine(Proof, "proof2.png"), "two");
        await File.WriteAllTextAsync(Path.Combine(Proof, "proof1.png"), "one");
        var files = await ReceiptFiles.Expand(Repo, Proof, ["src.?xt", Path.Combine(Proof, "proof[12].png")], default);
        Assert.Equal(3, files.Count);
        Assert.Equal(new[] { "proof1.png", "proof2.png", "src.txt" }, files.Select(f => f.Path));
        Assert.All(files, f => { Assert.Equal(64, f.Sha256.Length); Assert.True(f.Bytes > 0); });
        Assert.Equal(files, await ReceiptFiles.Expand(Repo, Proof, [Path.Combine(Proof, "proof[12].png"), "src.?xt"], default));
    }

    [Theory]
    [InlineData("missing*.txt")]
    [InlineData("../outside")]
    public async Task Missing_or_escaping_expected_pattern_is_a_visible_failure(string pattern)
    {
        await Init(); var context = await Context();
        Assert.Equal("expect_files", (await Check(context with { ExpectedFiles = [pattern] }, "recon", Recon)).Error);
    }

    [Fact]
    public async Task Expected_file_symlink_cannot_escape_its_root()
    {
        await Init();
        var outside = Path.Combine(_root, "outside"); await File.WriteAllTextAsync(outside, "private");
        File.CreateSymbolicLink(Path.Combine(Proof, "escape.txt"), outside);
        await Assert.ThrowsAsync<WorkflowFieldException>(() => ReceiptFiles.Expand(Repo, Proof, [Path.Combine(Proof, "*.txt")], default));
    }

    [Fact]
    public async Task Cancellation_is_preserved()
    {
        await Init(); var context = await Context(); using var stop = new CancellationTokenSource(); stop.Cancel();
        using var body = JsonDocument.Parse(Recon);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReceiptChecks().CheckLocalAsync(context, "recon", body.RootElement, stop.Token));
    }

    private const string Recon = """{"summary":"Inspected","existing":["src.txt:2"],"touches":[]}""";
    private static string Review(string head, string earlier) => JsonSerializer.Serialize(new
    {
        verdict = "fail",
        findings = new[] { new { severity = "blocking", late = false, path_line = "src.txt:2", text = "Issue", fix = "Fix it", grades_of_earlier = new[] { new { text = earlier, grade = "fixed" } } } },
        head
    });
    private static string ProofBody(string head, string file) => JsonSerializer.Serialize(new
    {
        summary = "Verified",
        files = new[] { file },
        proofUrls = new[] { "https://evidence.example.invalid/proof" },
        head,
        checks = new[] { new { name = "native", result = "passed", receiptUrl = "https://evidence.example.invalid/check" } }
    });
    private static async Task<ReceiptObservation> Check(ReceiptCheckContext context, string kind, string json)
    { using var body = JsonDocument.Parse(json); return await new ReceiptChecks().CheckLocalAsync(context, kind, body.RootElement, default); }
    private async Task Init()
    {
        Directory.CreateDirectory(Repo); Directory.CreateDirectory(Proof);
        await Git("init"); await Git("config", "user.name", "Receipt Fixture"); await Git("config", "user.email", "receipt@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Repo, "src.txt"), "first\nsecond\nthird\n");
        await Git("add", "."); await Git("commit", "-m", "base");
    }
    private async Task<ReceiptCheckContext> Context()
    {
        var state = await new ReceiptCheckout().Capture(Repo, default);
        return new(Repo, Proof, state.Head, state.Head, state.Fingerprint, state.Fingerprint, [], []);
    }
    private async Task<string> Git(params string[] args)
    {
        List<string> values = []; await foreach (var value in _git.ReadAsync(Repo, args, null, default)) values.Add(value);
        return string.Join('\0', values).TrimEnd('\r', '\n');
    }
    public void Dispose() => Directory.Delete(_root, true);
}
