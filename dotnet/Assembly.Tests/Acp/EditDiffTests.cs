using System.Text.Json;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Tests.Features.Chat.Rows;
using Assembly.Tests.Fixtures;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Acp;

public sealed class EditDiffTests
{
    [Theory]
    [InlineData("acp/tools.jsonl")]
    [InlineData("acp/codex-tools.jsonl")]
    public void Recorded_provider_edits_produce_the_same_patch_event_and_chat_detail(string fixture)
    {
        var events = FixtureReplay.Events(fixture);
        var edit = events.OfType<ToolCallChanged>().Last(change => change.Fields.Diffs is { Count: > 0 });
        var diff = Assert.Single(edit.Fields.Diffs!);
        Assert.EndsWith("/notes.txt", Assert.Single(diff.Paths));
        Assert.Contains("\n-apple\n+pear\n", diff.Patch);
        var turn = Converted.From(events);
        var row = turn.OfItem(edit.ToolCallId)[^1];
        Assert.Equal("completed", Converted.Text(row, "state"));
        Assert.Equal(1, Converted.Get(row, "added").GetInt32());
        Assert.Equal(1, Converted.Get(row, "removed").GetInt32());
        Assert.Equal(diff.Patch, turn.Detail(edit.ToolCallId).Diff);
        Assert.Equal(diff.Paths[0], turn.Detail(edit.ToolCallId).Path);
    }

    [Fact]
    public void One_patch_keeps_exact_text_and_per_file_counts_even_when_changes_are_in_another_order()
    {
        const string patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1,2 @@\n-old\n+new\n+++added content\ndiff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-one\n-two\n";
        var diff = UnifiedDiff.Of([new FileDiff(["/repo/b.txt", "/repo/a.txt", "/repo/binary"], patch)])!;
        Assert.Equal(patch, diff.Text);
        Assert.Equal(2, diff.Added);
        Assert.Equal(3, diff.Removed);
        Assert.Equal([new FileChange("/repo/b.txt", 0, 2), new FileChange("/repo/a.txt", 2, 1), new FileChange("/repo/binary", 0, 0)], diff.Files);

        using var json = JsonDocument.Parse($$$"""{"sessionUpdate":"tool_call_update","toolCallId":"t","kind":"edit","status":"completed","content":[{"type":"diff","changes":[{"operation":"delete","path":"/repo/b.txt"},{"operation":"modify","path":"/repo/a.txt"},{"operation":"add","path":"/repo/binary","fileType":"binary"}],"patch":{"format":"git_patch","text":"{{{JsonEncodedText.Encode(patch)}}}"}}]}""");
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        var store = RowStore.Open(db);
        var converter = new RowConverter();
        foreach (var change in new UpdateReader().Read(new("s", json.RootElement))) store.Commit("s", converter.Apply(change));
        Assert.Equal(patch, store.ToolCall("s", "t")!.Diff);
        var touched = store.FilesTouched("s", CancellationToken.None);
        Assert.Equal(3, touched.Count);
        Assert.Equal((2L, 1L), (touched.Single(f => f.Path == "/repo/a.txt").Added, touched.Single(f => f.Path == "/repo/a.txt").Removed));
        Assert.Equal((0L, 2L), (touched.Single(f => f.Path == "/repo/b.txt").Added, touched.Single(f => f.Path == "/repo/b.txt").Removed));
    }

    [Theory]
    [InlineData("\"b/space name.txt\"", "/repo/space name.txt")]
    [InlineData("\"b/caf\\303\\251.txt\"", "/repo/café.txt")]
    public void Git_quoted_paths_keep_their_counts(string header, string path)
    {
        var diff = UnifiedDiff.Of([new FileDiff([path], $"diff --git a/file b/file\n--- /dev/null\n+++ {header}\n@@ -0,0 +1 @@\n+one\n")])!;
        Assert.Equal(new FileChange(path, 1, 0), Assert.Single(diff.Files));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"format\":\"future\",\"text\":\"+not a git patch\"}")]
    public void Metadata_without_a_supported_patch_keeps_paths_without_inventing_content(string patch)
    {
        using var json = JsonDocument.Parse("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t\",\"kind\":\"edit\",\"content\":[{\"type\":\"diff\",\"changes\":[{\"operation\":\"add\",\"path\":\"/repo/new\"}],\"patch\":" + patch + "}]}");
        var change = Assert.IsType<ToolCallChanged>(Assert.Single(new UpdateReader().Read(new("s", json.RootElement))));
        Assert.Null(Assert.Single(change.Fields.Diffs!).Patch);
        var converted = Converted.From([change]);
        Assert.Equal("/repo/new", converted.Detail("t").Path);
        Assert.Equal("", converted.Detail("t").Diff);
        Assert.Equal(0, Converted.Get(converted.OfItem("t")[^1], "added").GetInt32());
    }

    [Fact]
    public void Recorded_permission_exposes_the_proposed_patch_before_the_edit_completes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures/acp/tools.jsonl");
        using var entry = JsonDocument.Parse(File.ReadLines(path).Single(line => line.Contains("\"method\":\"session/request_permission\"", StringComparison.Ordinal)));
        var request = entry.RootElement.GetProperty("line").GetProperty("params").Deserialize(AcpJsonContext.Default.RequestPermissionRequest)!;
        var reader = new UpdateReader();
        var change = Assert.IsType<ToolCallChanged>(Assert.Single(reader.ReadPermission(request)));
        var converted = Converted.From([change]);
        var row = converted.OfItem(change.ToolCallId)[^1];
        Assert.Equal("started", Converted.Text(row, "state"));
        Assert.Equal(1, Converted.Get(row, "added").GetInt32());
        Assert.Equal(1, Converted.Get(row, "removed").GetInt32());
        Assert.Contains("\n-apple\n+pear\n", converted.Detail(change.ToolCallId).Diff);
        var completed = Assert.IsType<ToolCallChanged>(Assert.Single(reader.Read(new(request.SessionId,
            JsonDocument.Parse("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"" + change.ToolCallId + "\",\"status\":\"completed\"}").RootElement))));
        Assert.False(completed.Started);
        var proposed = Assert.Single(change.Fields.Diffs!);
        var retained = Assert.Single(completed.Fields.Diffs!);
        Assert.Equal(proposed.Patch, retained.Patch);
        Assert.Equal(proposed.Paths, retained.Paths);
    }
}
