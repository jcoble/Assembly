using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class TerminalDetailTests(Xunit.Abstractions.ITestOutputHelper proof)
{
    private static void Apply(RowStore store, UpdateReader reader, RowConverter converter, string json, string provider = "P")
    {
        foreach (var e in reader.Read(new SessionNotification(provider, JsonDocument.Parse(json).RootElement.Clone()), 1))
            store.Commit("S", converter.Apply(e));
    }

    private static string Chunk(string data) => "{\"sessionUpdate\":\"terminal_output_chunk\",\"terminalId\":\"T\",\"data\":\"" + data + "\"}";
    private const string Reference = """{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"in_progress","content":[{"type":"terminal","terminalId":"T"}]}""";

    [Fact]
    public void Provider_selector_survives_content_clear_reopen_same_identity_resume_and_late_old_terminal()
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            foreach (var (provider, text, code) in new[] { ("P1", "old", 1), ("P2", "newer", 0) })
            {
                var reader = new UpdateReader(); var converter = new RowConverter();
                Apply(store, reader, converter, Reference, provider);
                Apply(store, reader, converter, Chunk(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))), provider);
                Apply(store, reader, converter, "{\"sessionUpdate\":\"terminal_update\",\"terminalId\":\"T\",\"exitStatus\":{\"exitCode\":" + code + "}}", provider);
                Apply(store, reader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"completed","content":[]}""", provider);
            }
            Assert.Equal("newer", store.ToolCall("S", "C")!.Output);
        }
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            Assert.Equal("P2", db.QueryFirst("SELECT acp_session_id FROM tool_details WHERE session_id='S' AND item_id='C'", r => r.Text(0), default));
            var current = store.ToolCall("S", "C")!;
            Assert.Equal("newer", current.Output);
            var json = JsonSerializer.SerializeToElement(current);
            Assert.Equal(5, json.GetProperty("OutputBytes").GetInt64());
            Assert.False(json.GetProperty("OutputTruncated").GetBoolean());
            Assert.Equal("P2", json.GetProperty("Terminals")[0].GetProperty("AcpSessionId").GetString());
            Assert.Equal(0, json.GetProperty("Terminals")[0].GetProperty("ExitStatus").GetProperty("ExitCode").GetInt64());
            Apply(store, new UpdateReader(), new RowConverter(), Chunk("IQ=="), "P2");
            Apply(store, new UpdateReader(), new RowConverter(), Chunk("YmFk"), "P1");
        }
        using var reopened = TestDatabase.Open(file.Path);
        var final = RowStore.Open(reopened).ToolCall("S", "C")!;
        Assert.Equal("newer!", final.Output);
        Assert.Equal(6, JsonSerializer.SerializeToElement(final).GetProperty("OutputBytes").GetInt64());
        proof.WriteLine($"Reopened active identity=P2; output={final.Output}; source_bytes={final.OutputBytes}; truncated={final.OutputTruncated}; terminal_count={final.Terminals!.Count}; old P1 late bytes excluded.");
    }

    [Theory]
    [InlineData("codex-terminal-background.jsonl", "finished\n")]
    [InlineData("codex-terminal-chunks.jsonl", null)]
    [InlineData("claude-terminal-snapshots.jsonl", null)]
    public void Genuine_terminal_output_is_saved_once_and_remains_available_after_tool_completion(string fixture, string? expected)
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
        // Independent delivered-byte oracle from selected original wire lines, with no invented stdout.
        var outputs = new Dictionary<string, List<byte>>();
        var refs = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "acp-v2", fixture)))
        {
            using var doc = JsonDocument.Parse(line);
            var p = doc.RootElement.GetProperty("line").GetProperty("params"); var update = p.GetProperty("update");
            var kind = update.GetProperty("sessionUpdate").GetString();
            if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var item in content.EnumerateArray())
                    if (item.GetProperty("type").GetString() == "terminal")
                        refs[update.GetProperty("toolCallId").GetString()!] = item.GetProperty("terminalId").GetString()!;
            if (kind is "terminal_update" or "terminal_output_chunk")
            {
                var id = update.GetProperty("terminalId").GetString()!;
                if (!outputs.TryGetValue(id, out var bytes)) outputs[id] = bytes = [];
                if (kind == "terminal_output_chunk") bytes.AddRange(Convert.FromBase64String(update.GetProperty("data").GetString()!));
                else if (update.TryGetProperty("output", out var output))
                {
                    bytes.Clear(); if (output.ValueKind == JsonValueKind.Object) bytes.AddRange(Convert.FromBase64String(output.GetProperty("data").GetString()!));
                }
            }
            Apply(store, reader, converter, update.GetRawText(), p.GetProperty("sessionId").GetString()!);
        }
        Assert.NotEmpty(refs);
        foreach (var (tool, terminal) in refs)
        {
            var result = store.ToolCall("S", tool)!;
            Assert.Equal(expected ?? Encoding.UTF8.GetString(outputs[terminal].ToArray()), result.Output);
            Assert.DoesNotContain(store.RowsAfter("S", 0), row => row.Payload.GetRawText().Contains("finished\\n", StringComparison.Ordinal));
        }
        using var replayFile = new TempDatabase(); using var replayDb = TestDatabase.Open(replayFile.Path);
        var replayStore = RowStore.Open(replayDb); var replayConverter = new RowConverter();
        foreach (var e in Assembly.Tests.Fixtures.FixtureReplay.Events("acp-v2/" + fixture))
            replayStore.Commit("S", replayConverter.Apply(e));
        foreach (var tool in refs.Keys)
        {
            var direct = store.ToolCall("S", tool)!; var replay = replayStore.ToolCall("S", tool)!;
            Assert.Equal((direct.Output, direct.OutputBytes, direct.OutputTruncated), (replay.Output, replay.OutputBytes, replay.OutputTruncated));
            Assert.Equal(direct.Terminals, replay.Terminals);
        }
        Assert.Equal(0, db.QueryFirst("""
            SELECT count(*) FROM tool_details d WHERE output IS NOT NULL AND EXISTS
              (SELECT 1 FROM tool_terminal_refs r WHERE r.session_id=d.session_id AND r.item_id=d.item_id AND r.acp_session_id=d.acp_session_id)
            """, r => r.Long(0), default));
    }

    [Fact]
    public void Byte_chunks_preserve_split_utf8_and_snapshots_replace_including_unknown_and_known_empty()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
        void Send(string json) => Apply(store, reader, converter, json);
        Send(Chunk("4g==")); // First byte of the euro sign precedes its reference.
        Send(Reference); Send(Chunk("gqw="));
        Assert.Equal("€", store.ToolCall("S", "C")!.Output);
        Send("""{"sessionUpdate":"terminal_update","terminalId":"T","output":{"data":"eA=="},"exitStatus":{}}""");
        Send("""{"sessionUpdate":"terminal_update","terminalId":"T","output":{"data":"eA=="}}""");
        Assert.Equal("x", store.ToolCall("S", "C")!.Output);
        Send(Chunk("!invalid!"));
        Assert.Equal("x", store.ToolCall("S", "C")!.Output);
        Send("""{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"cancelled","content":[]}""");
        Send(Chunk("eQ==")); Assert.Equal("xy", store.ToolCall("S", "C")!.Output);
        Send("""{"sessionUpdate":"terminal_update","terminalId":"T","output":null,"exitStatus":null}""");
        Assert.Null(store.ToolCall("S", "C")!.Output);
        Send(Chunk("")); Assert.Equal("", store.ToolCall("S", "C")!.Output);
    }

    [Fact]
    public void Bounded_previews_count_original_bytes_once_and_cap_combined_output_at_whole_utf8_characters()
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
            Apply(store, reader, converter, Reference);
            var block = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 65536)));
            for (var i = 0; i < 16; i++) Apply(store, reader, converter, Chunk(block));
            Apply(store, reader, converter, Chunk("4oKs")); // A complete code point beyond the retained prefix.
            Apply(store, reader, converter, """{"sessionUpdate":"tool_call_content_chunk","toolCallId":"C","content":{"type":"terminal","terminalId":"T"}}""");
            Apply(store, reader, converter, """{"sessionUpdate":"tool_call_content_chunk","toolCallId":"C","content":{"type":"terminal","terminalId":"other"}}""");
            Apply(store, reader, converter, """{"sessionUpdate":"terminal_output_chunk","terminalId":"other","data":"IQ=="}""");
            Assert.Equal(1048576, db.QueryFirst("SELECT length(output) FROM acp_terminal_details WHERE terminal_id='T'", r => r.Long(0), default));
            Assert.Equal(1048579, db.QueryFirst("SELECT output_bytes FROM acp_terminal_details WHERE terminal_id='T'", r => r.Long(0), default));
            Assert.Equal(2, db.QueryFirst("SELECT count(*) FROM tool_terminal_refs", r => r.Long(0), default));
            proof.WriteLine($"Isolated SQLite T: preview_bytes={db.QueryFirst("SELECT length(output) FROM acp_terminal_details WHERE terminal_id='T'", r => r.Long(0), default)}; observed_bytes={db.QueryFirst("SELECT output_bytes FROM acp_terminal_details WHERE terminal_id='T'", r => r.Long(0), default)}; terminal_records={db.QueryFirst("SELECT count(*) FROM acp_terminal_details", r => r.Long(0), default)}; duplicated_tool_output_rows={db.QueryFirst("SELECT count(*) FROM tool_details WHERE output IS NOT NULL", r => r.Long(0), default)}.");
        }
        using var reopened = TestDatabase.Open(file.Path);
        var result = RowStore.Open(reopened).ToolCall("S", "C")!;
        Assert.Equal(1048580, result.OutputBytes);
        Assert.True(result.OutputTruncated);
        Assert.Equal(1048576, Encoding.UTF8.GetByteCount(result.Output!));

        // A replacement ends at a character boundary in the source but crosses the storage cap.
        var store2 = RowStore.Open(reopened); var reader2 = new UpdateReader(); var converter2 = new RowConverter();
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 1048575) + "€"));
        Apply(store2, reader2, converter2, "{\"sessionUpdate\":\"terminal_update\",\"terminalId\":\"T\",\"output\":{\"data\":\"" + data + "\"}}");
        var split = store2.ToolCall("S", "C")!;
        Assert.Equal(1048579, split.OutputBytes);
        Assert.Equal(1048575, Encoding.UTF8.GetByteCount(split.Output!));
        Assert.DoesNotContain('�', split.Output!);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(17, 2)]
    public void Additive_migration_preserves_legacy_details_null_identity_and_never_lowers_version(int version, int existingColumns)
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
            db.Writer.Write("test.legacy", scope => scope.Execute($$"""
                CREATE TABLE tool_details(session_id TEXT NOT NULL,item_id TEXT NOT NULL,input TEXT,output TEXT,diff TEXT,path TEXT,
                  PRIMARY KEY(session_id,item_id));
                INSERT INTO tool_details VALUES('S','C','{"x":1}','legacy €','diff','file');
                PRAGMA user_version={{version}};
                """ + (existingColumns >= 1 ? "ALTER TABLE tool_details ADD COLUMN output_source TEXT;" : "")
                + (existingColumns >= 2 ? "ALTER TABLE tool_details ADD COLUMN acp_session_id TEXT;" : "")));
        for (var i = 0; i < 2; i++)
        {
            using var db = TestDatabase.Open(file.Path); var store = RowStore.Open(db);
            var result = store.ToolCall("S", "C")!;
            Assert.Equal(("{\"x\":1}", "legacy €", "diff", "file"), (result.Input, result.Output, result.Diff, result.Path));
            Assert.Null(result.OutputBytes); Assert.Null(result.Terminals);
            Assert.Equal(version, store.StoredFormatVersion);
            Assert.Equal(2, db.QueryFirst("SELECT count(*) FROM pragma_table_info('tool_details') WHERE name IN ('output_source','acp_session_id')", r => r.Long(0), default));
            Assert.Equal(1, db.QueryFirst("SELECT acp_session_id IS NULL AND output_source IS NULL FROM tool_details", r => r.Long(0), default));
        }
    }

    [Fact]
    public void Failed_related_row_write_rolls_back_terminal_refs_selector_counts_and_sequence_without_publication()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db);
        var reader = new UpdateReader(); var converter = new RowConverter();
        Apply(store, reader, converter, Reference, "P1");
        Apply(store, reader, converter, Chunk("b2xk"), "P1");
        var before = store.LastSeq("S");
        using var hub = new LiveHub(store); using var live = hub.Subscribe("S", before);
        db.Writer.Write("test.failure", scope => scope.Execute("""
            CREATE TRIGGER reject_rows BEFORE INSERT ON rows BEGIN SELECT RAISE(ABORT,'injected row failure'); END;
            """));
        var batch = new RowBatch();
        batch.Terminals.Add(new TerminalChanged(1, "T", "eA==", AcpSessionId: "P"));
        batch.Details.Add(new ToolDetail("C", Replace: true, TerminalIds: ["T"], AcpSessionId: "P"));
        batch.Rows.Add(new NewRow("run", "C", "tool", JsonDocument.Parse("{}").RootElement.Clone(), 1));
        Assert.Throws<SqliteException>(() => hub.Publish("S", batch));
        Assert.False(live.Rows.TryRead(out _)); Assert.Equal(before, store.LastSeq("S"));
        Assert.Equal("old", store.ToolCall("S", "C")!.Output);
        Assert.Equal("P1", db.QueryFirst("SELECT acp_session_id FROM tool_details", r => r.Text(0), default));
        Assert.Equal(3, db.QueryFirst("SELECT output_bytes FROM acp_terminal_details", r => r.Long(0), default));
        foreach (var table in new[] { "acp_terminal_details", "tool_terminal_refs", "tool_details", "rows" })
            Assert.Equal(1, db.QueryFirst($"SELECT count(*) FROM {table}", r => r.Long(0), default));
    }

    [Fact]
    public void Session_loss_releases_retained_open_tool_and_preserves_late_terminal_detail()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
        store.Commit("S", converter.Apply(new TurnStarted(0, "original", "start")));
        Apply(store, reader, converter, Reference);
        Apply(store, reader, converter, """{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""");
        Assert.True(converter.HasOpenTools);
        store.Commit("S", converter.Apply(new TaskChanged(2, "C", "", "stopped", false, "C")));
        Assert.False(converter.HasOpenTools);
        Apply(store, reader, converter, Chunk("eA=="));
        Assert.Equal("x", store.ToolCall("S", "C")!.Output);
        Assert.False(converter.HasOpenTools);
    }

    [Fact]
    public void Multiple_terminal_refs_read_only_one_combined_cap_of_blob_bytes_from_sql()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var measured = new MeasuredDatabase(db); var store = RowStore.Open(measured);
        var reader = new UpdateReader(); var converter = new RowConverter();
        Apply(store, reader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","content":[{"type":"terminal","terminalId":"T"},{"type":"terminal","terminalId":"U"}]}""");
        var data = Convert.ToBase64String(new byte[1048576]);
        foreach (var id in new[] { "T", "U" })
            Apply(store, reader, converter, "{\"sessionUpdate\":\"terminal_output_chunk\",\"terminalId\":\"" + id + "\",\"data\":\"" + data + "\"}");
        var result = store.ToolCall("S", "C")!;
        Assert.Equal(2097152, result.OutputBytes);
        Assert.Equal(1048576, measured.BlobBytesRead);
        Assert.Equal(2, result.Terminals!.Count);
        proof.WriteLine($"Combined SQLite BLOB read={measured.BlobBytesRead}; delivered source bytes={result.OutputBytes}; metadata terminals={result.Terminals.Count}; truncated={result.OutputTruncated}.");
    }

    [Fact]
    public void Malformed_utf8_replacement_is_displayed_with_source_count_and_reports_a_cut_expanded_preview()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
        Apply(store, reader, converter, Reference);
        Apply(store, reader, converter, Chunk(Convert.ToBase64String(Enumerable.Repeat((byte)0x80, 350000).ToArray())));
        var result = store.ToolCall("S", "C")!;
        Assert.Equal(350000, result.OutputBytes);
        Assert.Equal(1048575, Encoding.UTF8.GetByteCount(result.Output!));
        Assert.True(result.OutputTruncated);
        Assert.All(result.Output!, character => Assert.Equal('�', character));
    }

    [Fact]
    public void Completed_resolved_snapshots_keep_late_raw_precedence_and_deduplicate_without_breaking_partial_producers()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
        Apply(store, reader, converter, Reference);
        Apply(store, reader, converter, Chunk("eA=="));
        Apply(store, reader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"completed","rawOutput":"raw","content":[{"type":"content","content":{"type":"text","text":"fallback"}}]}""");
        Assert.Equal("raw", store.ToolCall("S", "C")!.Output);
        var duplicate = Assert.Single(reader.Read(new SessionNotification("P", JsonDocument.Parse("""{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"completed"}""").RootElement.Clone())));
        Assert.True(converter.Apply(duplicate).IsEmpty);
        Apply(store, reader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","rawOutput":null}""");
        Assert.Equal("x", store.ToolCall("S", "C")!.Output);
        Apply(store, reader, converter, """{"sessionUpdate":"terminal_update","terminalId":"T","output":null}""");
        Assert.Equal("fallback", store.ToolCall("S", "C")!.Output);
        Apply(store, reader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","content":[]}""");
        Assert.Null(store.ToolCall("S", "C")!.Output);

        var partial = new RowConverter();
        store.Commit("legacy", partial.Apply(new ToolCallChanged(1, "legacy", true, new ToolCallFields(Name: "Read", Output: "kept"))));
        store.Commit("legacy", partial.Apply(new ToolCallChanged(2, "legacy", false, new ToolCallFields(Status: "completed"))));
        store.Commit("legacy", partial.Apply(new ToolCallChanged(3, "legacy", false, new ToolCallFields(Title: "late"))));
        Assert.Equal("kept", store.ToolCall("legacy", "legacy")!.Output);
    }

    [Fact]
    public void New_provider_reused_tool_starts_immediately_after_prior_session_stopped_cleanup()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db); var converter = new RowConverter(); var oldReader = new UpdateReader();
        store.Commit("S", converter.Apply(new TurnStarted(0, "old-run", "old")));
        Apply(store, oldReader, converter, Reference, "P1");
        Apply(store, oldReader, converter, Chunk("b2xk"), "P1");
        Apply(store, oldReader, converter, """{"sessionUpdate":"state_update","state":"idle","stopReason":"end_turn"}""", "P1");
        store.Commit("S", converter.Apply(new TaskChanged(2, "C", "", "stopped", false, "C")));
        Assert.False(converter.HasOpenTools);

        // SessionHost owns one converter across actual provider-session replacement.
        store.Commit("S", converter.Apply(new TurnStarted(3, "new-run", "new")));
        var newReader = new UpdateReader();
        Apply(store, newReader, converter, Reference, "P2");
        Assert.True(converter.HasOpenTools);
        Assert.Equal("P2", db.QueryFirst("SELECT acp_session_id FROM tool_details", r => r.Text(0), default));
        Assert.Null(store.ToolCall("S", "C")!.Output); // P1 data cannot show while P2 is still running.
        Apply(store, newReader, converter, Chunk("bmV3ZXI="), "P2");
        Assert.Equal("newer", store.ToolCall("S", "C")!.Output);
        var current = store.PageBefore("S", long.MaxValue, 100).Rows.Single(row => row.Kind == "tool");
        Assert.Equal(("new-run", "started"), (current.RunId, current.Payload.GetProperty("state").GetString()));
        Apply(store, newReader, converter, """{"sessionUpdate":"tool_call_update","toolCallId":"C","status":"completed","content":[]}""", "P2");
        Assert.False(converter.HasOpenTools);
        Assert.Equal("newer", store.ToolCall("S", "C")!.Output);
    }

    [Theory]
    [InlineData("P1", "P1")]
    [InlineData(null, "P2")]
    public void Same_or_unknown_prior_provider_keeps_ended_tool_closed_until_its_explicit_terminal_update(string? prior, string current)
    {
        var converter = new RowConverter();
        converter.Apply(new TurnStarted(0, "old-run", "old"));
        converter.Apply(new ToolCallChanged(1, "C", true, new ToolCallFields(Name: "original", Status: "pending", AcpSessionId: prior)));
        converter.Apply(new TurnEnded(2, "end_turn", null));
        converter.Apply(new TurnStarted(3, "new-run", "new"));
        var ignored = converter.Apply(new ToolCallChanged(4, "C", false, new ToolCallFields(Status: "in_progress", AcpSessionId: current)));
        Assert.True(ignored.IsEmpty); Assert.False(converter.HasOpenTools);
        var late = converter.Apply(new ToolCallChanged(5, "C", false, new ToolCallFields(Status: "completed", Output: "late", AcpSessionId: current)));
        Assert.Equal("old-run", Assert.Single(late.Rows).RunId);
        Assert.Equal("late", Assert.Single(late.Details).Output);
        Assert.False(converter.HasOpenTools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Historical_terminal_order_and_bounded_prefix_survive_replacement_clear_reopen_and_late_bytes(bool bounded)
    {
        using var file = new TempDatabase();
        var z = bounded ? new string('z', 1048575) + "€" : "z";
        var firstOutput = bounded ? new string('z', 1048575) : "za";
        string References(params string[] ids) => JsonSerializer.Serialize(new
        {
            sessionUpdate = "tool_call_update",
            toolCallId = "C",
            status = "in_progress",
            content = ids.Select(id => new { type = "terminal", terminalId = id }).ToArray(),
        });
        string Bytes(string id, string text) => JsonSerializer.Serialize(new
        {
            sessionUpdate = "terminal_output_chunk",
            terminalId = id,
            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        });
        void Check(RowStore store, string expected, long bytes, params string[] terminalIds)
        {
            var detail = store.ToolCall("S", "C")!;
            Assert.Equal(expected, detail.Output);
            Assert.Equal(bytes, detail.OutputBytes);
            Assert.Equal(bounded, detail.OutputTruncated);
            Assert.Equal(terminalIds, detail.Terminals!.Select(terminal => terminal.TerminalId));
        }

        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db); var reader = new UpdateReader(); var converter = new RowConverter();
            Apply(store, reader, converter, References("Z", "A"));
            Apply(store, reader, converter, Bytes("Z", z));
            Apply(store, reader, converter, Bytes("A", "a"));
            Check(store, firstOutput, bounded ? 1048579 : 2, "Z", "A");

            Apply(store, reader, converter, References("A")); // A subset must not move A ahead of historical Z.
            Check(store, firstOutput, bounded ? 1048579 : 2, "Z", "A");
            Apply(store, reader, converter, References("A", "Z")); // Existing IDs keep their first-observed order.
            Check(store, firstOutput, bounded ? 1048579 : 2, "Z", "A");
            Apply(store, reader, converter, References("B", "A")); // A new ID appends after all history.
            Apply(store, reader, converter, Bytes("B", "b"));
            Check(store, bounded ? firstOutput : "zab", bounded ? 1048580 : 3, "Z", "A", "B");
            Apply(store, reader, converter, References());
            Check(store, bounded ? firstOutput : "zab", bounded ? 1048580 : 3, "Z", "A", "B");
            Assert.Equal(0, db.QueryFirst("SELECT sum(current) FROM tool_terminal_refs WHERE session_id='S' AND item_id='C' AND acp_session_id='P'", r => r.Long(0), default));
        }

        using (var db = TestDatabase.Open(file.Path))
        {
            var store = RowStore.Open(db);
            Check(store, bounded ? firstOutput : "zab", bounded ? 1048580 : 3, "Z", "A", "B");
            var reader = new UpdateReader(); var converter = new RowConverter();
            Apply(store, reader, converter, Bytes("Z", "!"));
            Apply(store, reader, converter, Bytes("A", "?"));
            Apply(store, reader, converter, Bytes("B", "#"));
            Check(store, bounded ? firstOutput : "z!a?b#", bounded ? 1048583 : 6, "Z", "A", "B");
        }
        using var finalDb = TestDatabase.Open(file.Path);
        Check(RowStore.Open(finalDb), bounded ? firstOutput : "z!a?b#", bounded ? 1048583 : 6, "Z", "A", "B");
        proof.WriteLine($"Stable historical order Z,A,B; subset/reorder/new reference/clear/reopen/late bytes verified; bounded={bounded}.");
    }

    // The real SQLite query and mapper run unchanged; measure bytes crossing the database boundary.
    private sealed class MeasuredDatabase(IAppDatabase inner) : IAppDatabase
    {
        public long BlobBytesRead { get; private set; }
        public IAtomicWriter Writer => inner.Writer;
        public IReadSnapshot OpenSnapshot(CancellationToken cancellationToken) => new MeasuredSnapshot(inner.OpenSnapshot(cancellationToken), this);
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) =>
            inner.Query(sql, row => map(new MeasuredRow(row, this)), cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) =>
            inner.QueryFirst(sql, row => map(new MeasuredRow(row, this)), cancel, args);

        private sealed class MeasuredSnapshot(IReadSnapshot inner, MeasuredDatabase owner) : IReadSnapshot
        {
            public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.Query(sql, row => map(new MeasuredRow(row, owner)), args);
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.QueryFirst(sql, row => map(new MeasuredRow(row, owner)), args);
            public void Dispose() => inner.Dispose();
        }

        private sealed class MeasuredRow(IRow inner, MeasuredDatabase owner) : IRow
        {
            public bool IsNull(int column) => inner.IsNull(column);
            public long Long(int column) => inner.Long(column);
            public double Real(int column) => inner.Real(column);
            public string Text(int column) => inner.Text(column);
            public byte[] Blob(int column)
            {
                var value = inner.Blob(column); owner.BlobBytesRead += value.LongLength; return value;
            }
        }
    }
}
