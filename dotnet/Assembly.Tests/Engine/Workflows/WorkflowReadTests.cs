using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Projects;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Reading;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed class WorkflowReadTests
{
    [Fact]
    public void Receipt_text_chunks_preserve_large_utf8_and_embedded_nul_with_stable_byte_offsets()
    {
        using var f = new Fixture(); var run = f.Start("one");
        var original = new string('a', 2047) + "🌱\0" + string.Concat(Enumerable.Repeat(new string('z', 100) + "🌱\0", 11_000));
        var id = f.Receipt(run, "build", JsonSerializer.SerializeToElement(new { summary = original, commits = Array.Empty<string>(), tests_added = Array.Empty<string>(), unknown = "not-public" }));
        var reader = new EngineReader(f.Core.Db, f.Core.Machine); var text = new StringBuilder();
        string? after = null; long offset = 0; var pages = 0; var complete = false;
        do
        {
            var page = reader.WorkflowReceipt(id, after, 200, default); pages++;
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) <= 1024 * 1024);
            Assert.True(page.GetProperty("sections").GetArrayLength() <= 200);
            Assert.Equal("pending", page.GetProperty("receipt").GetProperty("state").GetString());
            Assert.False(page.GetProperty("receipt").TryGetProperty("capture", out _));
            foreach (var section in page.GetProperty("sections").EnumerateArray())
            {
                Assert.True(Encoding.UTF8.GetByteCount(section.GetRawText()) <= 32 * 1024);
                Assert.NotEqual("unknown", section.GetProperty("section").GetString());
                if (section.GetProperty("section").GetString() != "summary") continue;
                Assert.Equal("$.report.receipt.summary", section.GetProperty("path").GetString());
                Assert.Equal(offset, section.GetProperty("offset").GetInt64());
                var value = section.GetProperty("value").GetString()!;
                text.Append(value); offset += Encoding.UTF8.GetByteCount(value);
                complete = section.GetProperty("complete").GetBoolean();
                Assert.Equal(offset == Encoding.UTF8.GetByteCount(original), complete);
            }
            after = page.TryGetProperty("next", out var next) ? next.GetString() : null;
        } while (after is not null);
        Assert.True(pages > 1); Assert.True(complete); Assert.Equal(original, text.ToString());
    }

    [Fact]
    public void Receipt_sections_follow_typed_field_then_structural_order_and_preserve_empty_containers_and_historical_path()
    {
        using var f = new Fixture(); var run = f.Start("one");
        var body = JsonSerializer.SerializeToElement(new
        {
            slices = Enumerable.Range(0, 250).Select(index => new { number = index + 1, title = "Slice " + index, needs = Array.Empty<int>(), files = Array.Empty<string>(), acceptance = new[] { new { id = "edge", text = "Restart", edge = true, restart = true, proved_by = "app" } } }).ToArray(),
            url = "https://www.notion.so/unverified",
            path = "historical/plan.md",
            private_field = "not-public"
        });
        var id = f.Receipt(run, "plan", body); var reader = new EngineReader(f.Core.Db, f.Core.Machine);
        List<JsonElement> sections = []; string? after = null;
        do
        {
            var page = reader.WorkflowReceipt(id, after, 200, default);
            sections.AddRange(page.GetProperty("sections").EnumerateArray());
            after = page.TryGetProperty("next", out var next) ? next.GetString() : null;
        } while (after is not null);
        Assert.Equal("url", sections[0].GetProperty("section").GetString());
        Assert.Equal("path", sections[^1].GetProperty("section").GetString());
        Assert.Equal("historical/plan.md", sections[^1].GetProperty("value").GetString());
        Assert.DoesNotContain(sections, item => item.GetProperty("section").GetString() == "private_field");
        Assert.Contains(sections, item => item.GetProperty("path").GetString() == "$.report.receipt.slices[0].needs" && item.GetProperty("type").GetString() == "array" && item.GetProperty("value").ValueKind == JsonValueKind.Null);
        Assert.Equal(Enumerable.Range(1, 250), sections.Where(item => item.GetProperty("path").GetString()!.EndsWith(".number", StringComparison.Ordinal)).Select(item => item.GetProperty("value").GetInt32()));
        Assert.Contains(sections, item => item.GetProperty("path").GetString() == "$.report.receipt.slices[249].acceptance[0].edge" && item.GetProperty("value").GetBoolean());
        Assert.False(sections[0].TryGetProperty("verified", out _));
    }

    [Fact]
    public void Receipt_continuations_bind_actual_identity_hash_and_owner_without_read_side_mutation()
    {
        using var f = new Fixture(); var run = f.Start("one");
        var body = JsonSerializer.SerializeToElement(new { summary = new string('x', 6000), commits = Array.Empty<string>(), tests_added = Array.Empty<string>() });
        var id = f.Receipt(run, "build", body); var other = f.Receipt(f.Start("another"), "build", body);
        var reader = new EngineReader(f.Core.Db, f.Core.Machine);
        var events = f.Core.Count("engine_event"); var commands = f.Core.Count("engine_command_receipt");
        var first = reader.WorkflowReceipt(id, null, 1, default); var next = first.GetProperty("next").GetString();
        Assert.Throws<ArgumentException>(() => reader.WorkflowReceipt(other, next, 1, default));
        Assert.Throws<ArgumentException>(() => reader.WorkflowReceipt(id, null, 201, default));
        f.Core.Db.Writer.Write("test.changed-receipt-hash", scope => scope.Execute("UPDATE engine_workflow_receipt SET hash=$hash WHERE id=$id", new SqlArg("$id", id), new("$hash", new string('f', 64))));
        Assert.Throws<ArgumentException>(() => reader.WorkflowReceipt(id, next, 1, default));
        f.Core.Db.Writer.Write("test.foreign-receipt", scope => scope.Execute("UPDATE engine_workflow_receipt SET machine='foreign' WHERE id=$id", new SqlArg("$id", id)));
        Assert.Equal("workflow_receipt_not_found", Assert.Throws<ArgumentException>(() => reader.WorkflowReceipt(id, null, 1, default)).Message);
        Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal(commands, f.Core.Count("engine_command_receipt"));
    }

    [Fact]
    public void History_pages_use_permanent_per_run_sequence_with_gaps_and_preserve_recorded_body()
    {
        using var f = new Fixture(); var run = f.Start("one"); var another = f.Start("two");
        f.Core.Db.Writer.Write("test.history-fixture", scope =>
        {
            scope.Execute("DELETE FROM engine_workflow_history WHERE run_id=$run", new SqlArg("$run", run));
            foreach (var seq in new[] { 2, 8, 20 })
                scope.Execute("""
                    INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
                    VALUES($id,1,$machine,1,1,$run,$seq,3,'recorded',100-$seq,json_object('recordedSeq',$seq))
                    """, new SqlArg("$id", run + "/history/" + seq), new("$machine", f.Core.Machine), new("$run", run), new("$seq", seq));
            return true;
        });
        var events = f.Core.Count("engine_event"); var receipts = f.Core.Count("engine_command_receipt");
        var reader = new EngineReader(f.Core.Db, f.Core.Machine);
        var first = reader.WorkflowHistory(run, null, 2, default);
        Assert.Equal(new long[] { 2, 8 }, first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("historySeq").GetInt64()));
        var next = first.GetProperty("next").GetString();
        var last = reader.WorkflowHistory(run, next, 2, default);
        var item = Assert.Single(last.GetProperty("items").EnumerateArray());
        Assert.Equal(20, item.GetProperty("historySeq").GetInt64());
        Assert.Equal(20, item.GetProperty("body").GetProperty("recordedSeq").GetInt64());
        Assert.False(last.TryGetProperty("next", out _));
        Assert.Throws<ArgumentException>(() => reader.WorkflowHistory(another, next, 2, default));
        Assert.Throws<ArgumentException>(() => reader.WorkflowHistory(run, null, 201, default));
        Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal(receipts, f.Core.Count("engine_command_receipt"));
    }

    [Fact]
    public void Static_metadata_is_read_from_the_snapshot_and_reported_labels_and_detail_override_it()
    {
        using var f = new Fixture();
        var run = f.StartYaml("name: Display\nsteps:\n  named: {run: [\"true\"], display: {label: Legacy, present: Checking, past: Checked, stage: verify, detail: Static detail}}\n  legacy: {run: [\"true\"], display: {label: Old label}}\n  unnamed: {run: [\"true\"]}\n");
        var steps = f.Read(run).GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("Checking", steps[0].GetProperty("labelPresent").GetString());
        Assert.Equal("Checked", steps[0].GetProperty("labelPast").GetString());
        Assert.Equal("verify", steps[0].GetProperty("stage").GetString());
        Assert.Equal("Static detail", steps[0].GetProperty("detail").GetString());
        Assert.Equal("Old label", steps[1].GetProperty("labelPresent").GetString());
        Assert.Equal("Old label", steps[1].GetProperty("labelPast").GetString());
        Assert.Equal(steps[2].GetProperty("name").GetString(), steps[2].GetProperty("labelPresent").GetString());
        Assert.Equal(JsonValueKind.Null, steps[0].GetProperty("progress").ValueKind);
        f.Core.Db.Writer.Write("test.reported-metadata", scope => scope.Execute("UPDATE engine_step SET label_present='Now checking',label_past='Actually checked',detail='Reported detail' WHERE run_id=$run AND definition_key='named'", new SqlArg("$run", run)));
        var reported = f.Read(run).GetProperty("steps")[0];
        Assert.Equal("Now checking", reported.GetProperty("labelPresent").GetString());
        Assert.Equal("Actually checked", reported.GetProperty("labelPast").GetString());
        Assert.Equal("Reported detail", reported.GetProperty("detail").GetString());
    }

    [Fact]
    public void Immutable_yaml_order_and_current_fields_match_the_existing_entity_projection_without_read_mutation()
    {
        using var f = new Fixture(); var run = f.Start("z_first", "a_second");
        f.Core.Db.Writer.Write("test.reported-display", scope => scope.Execute("""
            UPDATE engine_step SET progress=42,detail='Actual progress',label_present='Checking now',
              label_past='Checked',explanation='A recorded reason',outcome_reason='recorded_reason'
            WHERE run_id=$run AND definition_key='z_first';
            """, new SqlArg("$run", run)));
        var events = f.Core.Count("engine_event"); var receipts = f.Core.Count("engine_command_receipt");
        var page = f.Read(run); var steps = page.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(["z_first", "a_second"], steps.Select(step => step.GetProperty("definitionKey").GetString()));
        Assert.Equal(42, steps[0].GetProperty("progress").GetInt32());
        Assert.Equal("Checking now", steps[0].GetProperty("labelPresent").GetString());
        Assert.Equal("Checked", steps[0].GetProperty("labelPast").GetString());
        Assert.Equal("check", steps[0].GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, steps[1].GetProperty("progress").ValueKind);
        Assert.Equal(f.Core.Machine, steps[0].GetProperty("authorityMachine").GetString());
        var entities = new EngineReader(f.Core.Db, f.Core.Machine).Entities([new("step", steps[0].GetProperty("id").GetString()!)], null, default);
        Assert.True(JsonElement.DeepEquals(steps[0], Assert.Single(entities.Items)));
        Assert.Equal(events, f.Core.Count("engine_event")); Assert.Equal(receipts, f.Core.Count("engine_command_receipt"));
    }

    [Fact]
    public void Step_pages_use_definition_ordinal_and_continuations_are_bound_to_the_actual_run_snapshot()
    {
        using var f = new Fixture(); var keys = Enumerable.Range(0, 205).Reverse().Select(index => "n" + index).ToArray();
        var run = f.Start(keys); var first = f.Read(run);
        Assert.Equal(200, first.GetProperty("steps").GetArrayLength());
        var next = first.GetProperty("next").GetString(); var second = f.Read(run, next);
        Assert.Equal(5, second.GetProperty("steps").GetArrayLength()); Assert.False(second.TryGetProperty("next", out _));
        Assert.Equal(keys, first.GetProperty("steps").EnumerateArray().Concat(second.GetProperty("steps").EnumerateArray())
            .Select(step => step.GetProperty("definitionKey").GetString()));
        var another = f.Start("different"); Assert.Throws<ArgumentException>(() => f.Read(another, next));
        Assert.True(Encoding.UTF8.GetByteCount(first.GetRawText()) <= 1024 * 1024);
    }

    [Fact]
    public void Run_list_filters_and_keyset_pages_use_actual_owning_machine_state_and_project()
    {
        using var f = new Fixture(); var a = f.Start("one"); var b = f.Start("two"); var foreign = f.Start("foreign");
        f.Core.Db.Writer.Write("test.foreign-reference", scope => scope.Execute("UPDATE engine_run SET machine='coordinator' WHERE id=$id", new SqlArg("$id", foreign)));
        using var scope = f.Core.Db.OpenSnapshot(default);
        var filter = JsonSerializer.SerializeToElement(new { project = f.Project.Id, state = "queued", limit = 1 });
        var first = WorkflowQueries.Runs(scope, filter);
        var next = first.GetProperty("next").GetString();
        var second = WorkflowQueries.Runs(scope, JsonSerializer.SerializeToElement(new { project = f.Project.Id, state = "queued", limit = 1, after = next }));
        Assert.Equal(new[] { a, b }.OrderDescending(StringComparer.Ordinal), first.GetProperty("items").EnumerateArray()
            .Concat(second.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("id").GetString()));
        Assert.False(second.TryGetProperty("next", out _));
        Assert.Throws<ArgumentException>(() => WorkflowQueries.Runs(scope, JsonSerializer.SerializeToElement(new { state = "failed", after = next })));
        Assert.True(f.Read(foreign).GetProperty("run").GetProperty("reference").GetBoolean());
    }

    [Fact]
    public void Missing_immutable_snapshot_is_reported_and_oversized_display_is_refused_without_silent_truncation()
    {
        using var f = new Fixture(); var run = f.Start("one");
        f.Core.Db.Writer.Write("test.missing-snapshot", scope => scope.Execute("DELETE FROM engine_workflow_snapshot WHERE run_id=$run", new SqlArg("$run", run)));
        Assert.Equal("workflow_snapshot_required", Assert.Throws<InvalidOperationException>(() => f.Read(run)).Message);
        using (var scope = f.Core.Db.OpenSnapshot(default))
            Assert.Equal("workflow_snapshot_required", WorkflowQueries.Runs(scope, default).GetProperty("items")[0].GetProperty("displayPrerequisite").GetString());
        var large = f.Start("large");
        f.Core.Db.Writer.Write("test.oversize-display", scope => scope.Execute("UPDATE engine_step SET detail=$detail WHERE run_id=$run",
            new SqlArg("$detail", new string('x', 40_000)), new("$run", large)));
        Assert.Equal("workflow_display_item_too_large", Assert.Throws<InvalidOperationException>(() => f.Read(large)).Message);
    }

    private sealed class Fixture : IDisposable
    {
        internal EngineFixture Core { get; } = new();
        internal ProjectRecord Project { get; }
        internal Fixture()
        {
            Core.SeedSession("owner"); var projects = new ProjectStore(Core.Db, Core.Time);
            Project = Core.Db.Writer.Write("test.workflow-read-project", scope => projects.AddOrRestore(scope, Core.Machine, "/test", "Project", null).Project);
        }
        internal string Start(params string[] keys)
        {
            var yaml = "name: Read fixture\nsteps:\n" + string.Concat(keys.Select(key => $"  {key}:\n    run: [\"printf checked\"]\n"));
            return StartYaml(yaml);
        }
        internal string StartYaml(string yaml)
        {
            var snapshot = WorkflowParser.Parse(new(yaml, ImmutableSortedDictionary<string, string>.Empty));
            var request = new WorkflowStartRequest(Guid.NewGuid(), Guid.NewGuid().ToString("N"), 0, 0, "owner", Guid.NewGuid().ToString(),
                ".assembly/workflows/read.yml", Project.Id, "Read actual fixture", null, JsonSerializer.SerializeToElement(new { }));
            var context = new CommandContext(request.CommandId, Core.Machine, new(ActorKind.Window, "owner"), request.ProposedRunId, 0, 0, 1000);
            var prepared = WorkflowStarts.Prepare(context, request, snapshot, Project);
            var ack = Core.Db.Writer.Write("test.workflow-read-start", scope => WorkflowStarts.Apply(scope, new EngineCommands(Core.Machine, Core.Limits), context, prepared));
            Assert.True(ack.Result.Applied, ack.Result.Error); return ack.RunId;
        }
        internal string Receipt(string run, string kind, JsonElement body)
        {
            var id = Guid.NewGuid().ToString("N");
            var envelope = JsonSerializer.Serialize(new { context = new { secret = "private-context" }, capture = new { secret = "private-capture" }, report = new { receipt = body } });
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.GetRawText())));
            Core.Db.Writer.Write("test.stored-typed-receipt", scope => scope.Execute("""
                INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
                SELECT $id,1,$machine,1000,1000,$run,id,1,1,'owner',0,$hash,$kind,$body,'pending'
                FROM engine_step WHERE run_id=$run ORDER BY id LIMIT 1
                """, new SqlArg("$id", id), new("$machine", Core.Machine), new("$run", run), new("$hash", hash), new("$kind", kind), new("$body", envelope)));
            return id;
        }
        internal JsonElement Read(string run, string? after = null) => new EngineReader(Core.Db, Core.Machine).WorkflowRun(run, after, default);
        public void Dispose() => Core.Dispose();
    }
}
