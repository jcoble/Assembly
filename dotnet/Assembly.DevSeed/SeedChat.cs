using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Rows;

namespace Assembly.DevSeed;

public enum SeedStatus { Working, Done, Settled, Ended }
public sealed record SeedRow(long Seq, string? RunId, string? ItemId, string Kind, JsonElement Payload, long CreatedAt);

/// <summary>Writes one conversation the way a provider's stream would have left it. Scenario files read as the chat itself.</summary>
public sealed class SeedChat(string key, string provider, string project, string title)
{
    private const long RowGapMs = 3_500;
    private readonly List<Func<Cursor, Cursor>> _steps = [];
    private readonly List<SeedChat> _children = [];
    private int _attachmentCount;

    public string Key => key;
    public string Id => SeedWorld.Id("seed", key);
    public string Provider => provider;
    public string Project => project;
    public string Title => title;
    public string? Branch { get; private set; }
    public string? Machine { get; private set; }
    public double MinutesAgo { get; private set; } = 60;
    public SeedStatus Status { get; private set; } = SeedStatus.Done;
    public bool Unread { get; private set; }
    public bool Pinned { get; private set; }
    public string? Parent { get; private set; }
    public string? Cost { get; private set; }
    public IReadOnlyList<SeedChat> Children => _children;
    public List<(string File, int Seed)> Attachments { get; } = [];

    private sealed record Cursor(SeedWorld World, string Folder, string ChatId, List<SeedRow> Rows, List<ToolDetail> Details, string? Run, int Item, int Tools, long At);

    public SeedChat On(string branch) { Branch = branch; return this; }
    public SeedChat OnMachine(string name) { Machine = name; return this; }
    public SeedChat Ago(double minutes) { MinutesAgo = minutes; return this; }
    public SeedChat Is(SeedStatus status) { Status = status; return this; }
    public SeedChat MarkUnread() { Unread = true; return this; }
    public SeedChat Pin() { Pinned = true; return this; }
    public SeedChat Costing(string usd) { Cost = usd; return this; }

    public SeedChat Turn(string prompt, int attachments = 0)
    {
        var files = new List<string>();
        for (var i = 0; i < attachments; i++)
        {
            var name = SeedWorld.GuidOf(key + "/image/" + _attachmentCount).ToString("D") + ".png";
            files.Add(name); Attachments.Add((name, _attachmentCount++));
        }
        var turn = _steps.Count;
        _steps.Add(c =>
        {
            var run = SeedWorld.Id("seed-run", key + "/" + turn);
            var text = string.Concat(files.Select(file => "Attached image: " + c.World.Attachment(c.ChatId, file) + "\n")) + prompt;
            c = Add(c with { Run = run, Item = 0, Tools = 0 }, run, null, RowKind.User, new { text });
            return Add(c, run, null, RowKind.Turn, new { state = "started" });
        });
        return this;
    }

    public SeedChat Think(string text) => Step(c => Add(Next(c), c.Run, c.Run + ":" + c.Item + ":thinking", RowKind.Thinking, new { text, done = true }));

    public SeedChat Say(string markdown) => Step(c =>
    {
        var blocks = JsonDocument.Parse(SafeMarkdown.ToJson(SafeMarkdown.Parse(markdown))).RootElement.Clone();
        return Add(Next(c), c.Run, c.Run + ":" + c.Item, RowKind.Message, new { blocks, text = markdown });
    });

    public SeedChat Plan(params (string Text, string Status)[] items) => Step(c =>
        Add(c, c.Run, c.Run + ":plan", RowKind.Plan, new PlanPayload(items.Select(item => new PlanItem(item.Text, item.Status)).ToArray())));

    public SeedChat Read(string path, string contents) => Tool("Read", "read", "Read " + path, path, null, new(ItemId: "", Output: contents, Path: null));
    public SeedChat Find(string pattern, string results) => Tool("Grep", "search", "Search for " + pattern, null, null, new(ItemId: "", Input: JsonSerializer.Serialize(new { pattern }), Output: results));
    public SeedChat Run(string command, string output, bool failed = false) =>
        Tool("Bash", "execute", command, null, null, new(ItemId: "", Input: JsonSerializer.Serialize(new { command }), Output: output), failed ? "failed" : "completed");
    public SeedChat Edit(string path, string diff) =>
        Tool("Edit", "edit", "Edit " + path, path, Count(diff), new(ItemId: "", Diff: diff, Path: null));

    /// <summary>An approval that was answered, with its command having run.</summary>
    public SeedChat Approved(string title, string command, string output) => Ask(title, command, "answered", "allow_once").Run(command, output);
    /// <summary>The turn stops here, waiting on the user. A finished turn holding an open ask survives a restart.</summary>
    public SeedChat Waiting(string title, string command) => Ask(title, command, "open", null);

    public SeedChat Background(string name, string state = "completed") => Step(c =>
        Add(Next(c), c.Run, "task-" + key + "-" + c.Item, RowKind.Task, new TaskPayload(name, state, false, null)));

    public SeedChat Helper(SeedChat child, string description, string state = "idle")
    {
        child.Parent = Id; child.MinutesAgo = MinutesAgo; child.Status = Status; child.Branch = Branch;
        _children.Add(child);
        return Step(c => Add(Next(c), c.Run, "agent-" + child.Key, RowKind.Subagent,
            new SubagentPayload(child.Id, child.Title, description, state, null, null, false, null, child.Provider)));
    }

    public SeedChat Error(string message, string code) => Step(c => Add(c, c.Run, null, RowKind.Error, new ErrorPayload(message, code)));
    public SeedChat Context(long used, long size) => Step(c => Add(c, c.Run, "context:" + c.Run, RowKind.Context, new ContextPayload(used, size)));

    /// <summary>Closes the turn with its stats line; a chat left without this reads as running.</summary>
    public SeedChat End(string status = "completed", long inputTokens = 18_400, long outputTokens = 2_150, decimal? costUsd = 0.21m) => Step(c =>
    {
        var started = c.Rows.Last(row => row.RunId == c.Run && row.Kind == RowKind.Turn).CreatedAt;
        var model = SeedWorld.ModelOf[provider];
        c = Add(c, c.Run, null, RowKind.Stats, new StatsPayload(c.Run!, status, started, c.At - started, 1_400, model, inputTokens, outputTokens, 61.5, costUsd, c.Tools));
        return Add(c, c.Run, null, RowKind.Turn, new { state = status == "completed" ? "finished" : status == "aborted" ? "cancelled" : "failed" });
    });

    public (IReadOnlyList<SeedRow> Rows, IReadOnlyList<ToolDetail> Details) Build(SeedWorld world, string folder)
    {
        // The last row lands at MinutesAgo, however long the chat is.
        var rows = new List<SeedRow>(); var details = new List<ToolDetail>();
        var start = world.Ago(MinutesAgo) - Estimate() * RowGapMs;
        var context = new Cursor(world, folder, Id, rows, details, null, 0, 0, start);
        foreach (var step in _steps) context = step(context);
        return (rows, details);
    }

    private long Estimate() => _steps.Count * 2L;

    private SeedChat Step(Func<Cursor, Cursor> step) { _steps.Add(step); return this; }
    private static Cursor Next(Cursor c) => c with { Item = c.Item + 1 };

    private SeedChat Ask(string title, string command, string state, string? answer)
    {
        var options = new[] { new AskOption("allow", "Allow once", "allow_once"), new AskOption("always", "Always allow", "allow_always"), new AskOption("reject", "Reject", "reject_once") };
        return Step(c => Add(Next(c), c.Run, "ask:" + key + ":" + c.Item, RowKind.Ask, new AskPayload("permission", state, title, command, null, options, null,
            answer is null ? null : JsonSerializer.SerializeToElement("allow"))));
    }

    private SeedChat Tool(string name, string kind, string summary, string? relative, (int Added, int Removed)? lines, ToolDetail detail, string state = "completed") => Step(c =>
    {
        var id = "tool-" + key + "-" + c.Rows.Count;
        var path = relative is null ? null : Path.Combine(c.Folder, relative);
        c = Add(c with { Tools = c.Tools + 1 }, c.Run, id, RowKind.Tool, new ToolPayload(name, "started", summary, path, null, null, false, kind));
        c.Details.Add(detail with { ItemId = id, Path = path });
        return Add(c, c.Run, id, RowKind.Tool, new ToolPayload(name, state, summary, path, lines?.Added, lines?.Removed, true, kind));
    });

    private static (int Added, int Removed) Count(string diff)
    {
        var lines = diff.Split('\n');
        return (lines.Count(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)),
            lines.Count(line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)));
    }

    private static Cursor Add<T>(Cursor c, string? run, string? item, string kind, T payload)
    {
        var at = c.At + RowGapMs;
        c.Rows.Add(new(c.Rows.Count + 1, run, item, kind, JsonSerializer.SerializeToElement(payload, SeedJson.Options), at));
        return c with { At = at };
    }
}
