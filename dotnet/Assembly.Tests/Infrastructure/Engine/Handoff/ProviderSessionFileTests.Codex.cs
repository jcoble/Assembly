using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Infrastructure.Engine.Handoff;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

/// <summary>SOURCE-DERIVED rust-v0.166.0 JSON/SQLite fixtures, not captured provider sessions or witness proof.</summary>
public sealed partial class ProviderSessionFileTests
{
    // Canonical PORT labels in SOURCE-DERIVED inputs; not observed native SQLite values or a producer witness.
    private const string CodexHistoryMode = "single_file_jsonl";
    private const string CodexCompression = "none";
    private const string CodexThread = "0195f111-2222-7333-8444-555555555555";
    private const string CodexRollout = "0195f666-7777-7888-8999-aaaaaaaaaaaa";
    private const string CodexNormal = "sessions/2026/11/03/rollout-2026-11-03T01-02-03-" + CodexThread + ".jsonl";
    private const string CodexRevert = "sessions/2026/11/03/rollout-2026-11-03T04-05-06-" + CodexThread + "_" + CodexRollout + ".jsonl";

    [Fact]
    public void Codex_mapping_changes_only_the_allowed_JSON_string_token_and_preserves_every_other_byte()
    {
        const string line = """{ "type" : "session_meta", "payload" : {"id":"0195f111-2222-7333-8444-555555555555", "cwd" : "/a/repo", "session_id":"not-thread-authority", "prompt":"/a/repo", "tool":{"cwd":"/a/repo"}}, "cwd":"/a/repo" }""";
        const string expected = """{ "type" : "session_meta", "payload" : {"id":"0195f111-2222-7333-8444-555555555555", "cwd" : "/b/repo", "session_id":"not-thread-authority", "prompt":"/a/repo", "tool":{"cwd":"/a/repo"}}, "cwd":"/a/repo" }""";
        Assert.Equal(expected, new CodexSessionFile().MapLine(line, "/a/repo", "/b/repo"));
        const string unrelated = """{"type":"response_item","payload":{"cwd":"/a/repo","text":"/a/repo"}}""";
        Assert.Equal(unrelated, new CodexSessionFile().MapLine(unrelated, "/a/repo", "/b/repo"));
    }

    [Theory]
    [InlineData("/a/repo/", "/b/repo/", "/a/repo/child", "/b/repo/child")]
    [InlineData("/a/repo", "/b/repo", "/a/repo2", "/a/repo2")]
    [InlineData("/a/repo", "/b/repo", "/A/repo", "/A/repo")]
    [InlineData("/", "/target/", "/nested/file", "/target/nested/file")]
    [InlineData("/a/repo", "/", "/a/repo/child", "/child")]
    [InlineData("C:\\repo\\", "D:\\target\\", "C:\\repo\\child", "D:\\target\\child")]
    public void Codex_mapping_respects_exact_directory_boundaries_and_root_separators(string oldFolder, string newFolder, string cwd, string expected)
    {
        var line = JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd, untouched = "keep" } });
        using var mapped = JsonDocument.Parse(new CodexSessionFile().MapLine(line, oldFolder, newFolder));
        Assert.Equal(expected, mapped.RootElement.GetProperty("payload").GetProperty("cwd").GetString());
        Assert.Equal("keep", mapped.RootElement.GetProperty("payload").GetProperty("untouched").GetString());
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"/a\"}}")]
    [InlineData("{\"type\":\"turn_context\",\"payload\":{\"cwd\":3}}")]
    [InlineData("{\"type\":\"turn_context\",\"payload\":{\"cwd\":\"/a\",\"cwd\":\"/a\"}}")]
    public void Codex_mapping_refuses_malformed_required_metadata(string line) =>
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().MapLine(line, "/a", "/b")).Message);
    [Fact]
    public void Codex_current_SQLite_pointer_preserves_reverted_rollout_instead_of_the_newest_or_old_normal_file()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Write(CodexNormal);
        fixture.Write(CodexRevert);
        fixture.Select(CodexRevert);
        // An old normal rollout is newer by mtime: it is not the selected native conversation.
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Home, CodexNormal), DateTime.UtcNow.AddDays(1));
        var capture = fixture.Capture(CodexRevert, CodexRollout);
        Assert.Equal(new NativeFile(CodexRevert, CodexThread, CodexRollout), new CodexSessionFile().Locate(capture.Storage, capture.Current));
        var target = capture.Storage with { ProviderHome = fixture.Target, CodexSqliteHome = fixture.Target, MachineId = "target-engine" };
        Assert.Equal(Path.Combine(fixture.Target, CodexRevert), new CodexSessionFile().TargetPath(target, "/target/repo", capture,
            new(CodexRevert, CodexThread, CodexRollout)));
    }

    [Fact]
    public void Codex_bad_current_pointer_never_falls_back_to_a_valid_older_transcript()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Write(CodexNormal);
        fixture.Write(CodexRevert, metadataId: "0195f000-0000-7000-8000-000000000000");
        fixture.Select(CodexRevert);
        var capture = fixture.Capture(CodexRevert, CodexRollout);
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage, capture.Current)).Message);
        fixture.Select(CodexNormal);
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage, capture.Current)).Message);
    }

    [Fact]
    public void Codex_legacy_fallback_requires_exactly_one_metadata_valid_thread_candidate_and_the_same_witness_path()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Write(CodexNormal);
        var capture = fixture.Capture(CodexNormal, null);
        Assert.Equal(new NativeFile(CodexNormal, CodexThread, null), new CodexSessionFile().Locate(capture.Storage, capture.Current));
        fixture.Write(CodexRevert);
        Assert.Equal("ambiguous_native_session", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage, capture.Current)).Message);
    }

    [Fact]
    public void Codex_metadata_id_is_thread_authority_and_session_id_cannot_substitute_for_it()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Write(CodexNormal, metadataId: "0195f000-0000-7000-8000-000000000000");
        fixture.Select(CodexNormal);
        var capture = fixture.Capture(CodexNormal, null);
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage, capture.Current)).Message);
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage,
            capture.Current with { AccountContext = "another-account" })).Message);
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage,
            capture.Current with { LaunchBindingId = "another-launch" })).Message);
    }

    [Fact]
    public void Codex_archived_compressed_paginated_and_unknown_witnesses_refuse_without_falling_back()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Write(CodexNormal);
        fixture.Select(CodexNormal, archived: true);
        var capture = fixture.Capture(CodexNormal, null);
        Assert.Equal("unavailable", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage, capture.Current)).Message);
        fixture.Select(CodexNormal);
        Assert.Equal("unsupported", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage,
            capture.Current with { Compression = "zstd" })).Message);
        Assert.Equal("unsupported", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage,
            capture.Current with { HistoryMode = "paginated" })).Message);
        Assert.Equal("unsupported", Assert.Throws<HandoffRefused>(() => new CodexSessionFile().Locate(capture.Storage,
            capture.Current with { HistoryMode = "unknown" })).Message);
    }

    [Fact]
    public void Codex_target_cannot_repoint_an_existing_thread_to_a_different_rollout_or_open_source_roots()
    {
        using var fixture = new CodexDerivedFiles();
        fixture.Select(CodexNormal, target: true);
        var capture = fixture.Capture(CodexRevert, CodexRollout);
        var target = capture.Storage with { MachineId = "target-engine", ProviderHome = fixture.Target, CodexSqliteHome = fixture.Target };
        var handler = new CodexSessionFile();
        Assert.Equal("bad_args", Assert.Throws<HandoffRefused>(() => handler.TargetPath(target, "/target/repo", capture,
            new(CodexRevert, CodexThread, CodexRollout))).Message);
        fixture.Select(CodexRevert, target: true);
        // The source directory does not exist: target resolution must not read a source root.
        Directory.Delete(fixture.Home);
        Assert.Equal(Path.Combine(fixture.Target, CodexRevert), handler.TargetPath(target, "/target/repo", capture,
            new(CodexRevert, CodexThread, CodexRollout)));
    }

    private sealed class CodexDerivedFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "derived-codex-session-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(_root, "source");
        public string Target => Path.Combine(_root, "target");
        public CodexDerivedFiles() { Directory.CreateDirectory(Home); Directory.CreateDirectory(Target); }
        public NativeSessionCapture Capture(string relative, string? rollout) => new(
            new("source-engine", "configured-codex", "codex", "", "derived-launch-binding", Home, Home),
            new("assembly-session", 3, "source-engine", "configured-codex", "", "derived-launch-binding",
                CodexThread, CodexThread, "/source/repo", relative, rollout, CodexHistoryMode, CodexCompression));
        public void Write(string relative, string metadataId = CodexThread)
        {
            var path = Path.Combine(Home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // SOURCE-DERIVED. session_id is deliberately not a valid thread id and is never used as one.
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { id = metadataId, session_id = "not-thread-authority", cwd = "/source/repo" }
            }) + "\n");
        }
        public void Select(string relative, bool archived = false, bool target = false)
        {
            var home = target ? Target : Home;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(home, "state_5.sqlite"), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS threads(id TEXT PRIMARY KEY,rollout_path TEXT,history_mode TEXT,archived_at INTEGER);
                INSERT INTO threads(id,rollout_path,history_mode,archived_at) VALUES($id,$path,$mode,$archived)
                ON CONFLICT(id) DO UPDATE SET rollout_path=excluded.rollout_path,history_mode=excluded.history_mode,archived_at=excluded.archived_at;
                """;
            command.Parameters.AddWithValue("$id", CodexThread);
            command.Parameters.AddWithValue("$path", Path.Combine(home, relative));
            // Deliberately distinct from the canonical port label: this opaque DERIVED value is not a native capture.
            command.Parameters.AddWithValue("$mode", "source-derived-native-history-mode");
            command.Parameters.AddWithValue("$archived", archived ? 1000L : DBNull.Value);
            command.ExecuteNonQuery();
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

}
