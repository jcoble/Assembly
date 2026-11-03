using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Infrastructure.Engine.Handoff;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

/// <summary>SOURCE-DERIVED Claude 2.1.316 vectors; these are not captured provider sessions.</summary>
public sealed partial class ProviderSessionFileTests
{
    private const string ClaudeNative = "b1234567-1111-4111-8111-111111111111";
    private const string ClaudePublic = "a1234567-1111-4111-8111-111111111111";

    [Fact]
    public void Claude_project_key_uses_original_utf16_and_signed_int32()
    {
        Assert.Equal("-a-b-C-9", ClaudeSessionFile.ProjectKey("/a_b.C-9"));
        Assert.Equal("-" + new string('a', 199), ClaudeSessionFile.ProjectKey("/" + new string('a', 199)));
        Assert.Equal("-" + new string('a', 199) + "-b6ymvl", ClaudeSessionFile.ProjectKey("/" + new string('a', 200)));
        Assert.Equal(new string('-', 200) + "-s4uqmp", ClaudeSessionFile.ProjectKey("/" + new string('é', 200)));
        Assert.Equal(new string('-', 200) + "-5s12ud", ClaudeSessionFile.ProjectKey("/" + string.Concat(Enumerable.Repeat("😀", 100))));
        Assert.Equal("-" + new string('z', 199) + "-f94rhr", ClaudeSessionFile.ProjectKey("/" + new string('z', 220)));
        // Original-folder JS hash is int.MinValue; abs must widen before negating.
        Assert.Equal("-" + new string('a', 199) + "-zik0zk",
            ClaudeSessionFile.ProjectKey("/" + new string('a', 200) + "ai|apfr"));
    }

    [Theory]
    [InlineData("/a/repo", "/target", "/a/repo", "/target")]
    [InlineData("/a/repo/", "/target/", "/a/repo/sub", "/target/sub")]
    [InlineData("/a/repo", "/target", "/a/repo2", "/a/repo2")]
    [InlineData("/", "/target/", "/sub", "/target/sub")]
    [InlineData("/a", "/", "/a/sub", "/sub")]
    [InlineData("C:\\repo\\", "D:\\target\\", "C:\\repo\\sub", "D:\\target\\sub")]
    public void Claude_mapping_changes_only_top_level_cwd(string oldFolder, string newFolder, string cwd, string expected)
    {
        var suffix = ", \"prompt\":\"/a/repo\", \"tool\":{\"cwd\":\"/a/repo\"}, \"sessionId\":\"same\" }\r\n";
        var raw = "{ \"cwd\": " + JsonSerializer.Serialize(cwd) + suffix;
        var mapped = new ClaudeSessionFile().MapLine(raw, oldFolder, newFolder);
        Assert.Equal("{ \"cwd\": " + JsonSerializer.Serialize(expected) + suffix, mapped);
        if (cwd != expected)
            Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), SHA256.HashData(Encoding.UTF8.GetBytes(mapped)));
        Assert.DoesNotContain('\uFEFF', mapped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"cwd\":null}")]
    [InlineData("{\"cwd\":\"/a\",\"cwd\":\"/b\"}")]
    public void Claude_malformed_required_json_refuses(string line) =>
        Assert.Throws<HandoffRefused>(() => new ClaudeSessionFile().MapLine(line, "/a", "/b"));

    [Fact]
    public void Claude_alias_uses_selected_current_native_and_never_old_public_path()
    {
        var root = Directory.CreateTempSubdirectory("assembly-claude-source-derived-").FullName;
        try
        {
            var capture = ClaudeCapture(root);
            var path = Path.Combine(root, capture.Current.SelectedRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"type\":\"file-history-snapshot\",\"snapshot\":{}}\n" +
                "{\"type\":\"user\",\"sessionId\":\"" + ClaudeNative + "\",\"cwd\":\"/source\"}\n");
            var helper = new ClaudeSessionFile();
            var file = Assert.IsType<NativeFile>(helper.Locate(capture.Storage, capture.Current));
            Assert.Equal(ClaudeNative, file.NativeId);
            Assert.Equal(capture.Current.SelectedRelativePath, file.RelativePath);
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage,
                capture.Current with { SelectedRelativePath = "projects/-source/" + ClaudePublic + ".jsonl" }));
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage,
                capture.Current with { AccountContext = "another-account" }));
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage,
                capture.Current with { LaunchBindingId = "another-launch" }));
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage,
                capture.Current with { HistoryMode = "unknown" }));
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage,
                capture.Current with { Compression = "zstd" }));
            var target = capture.Storage with { ProviderHome = Path.Combine(root, "target"), MachineId = "target" };
            Assert.Equal(Path.Combine(target.ProviderHome, "projects", "-new", ClaudeNative + ".jsonl"),
                helper.TargetPath(target, "/new", capture, file));
            File.WriteAllText(path, "{\"type\":\"user\",\"sessionId\":\"" + ClaudePublic + "\"}\n");
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage, capture.Current));
            File.WriteAllText(path, "{not json}\n");
            Assert.Throws<HandoffRefused>(() => helper.Locate(capture.Storage, capture.Current));
        }
        finally { Directory.Delete(root, true); }
    }

    private static NativeSessionCapture ClaudeCapture(string home) => new(
        new ProviderFileContext("source", "configured-claude", "claude", "", "launch", home, null),
        new NativeSessionWitness("assembly-session", 1, "source", "configured-claude", "", "launch",
            ClaudePublic, ClaudeNative, "/source", "projects/-source/" + ClaudeNative + ".jsonl", null,
            "single_file_jsonl", "none"));
}
