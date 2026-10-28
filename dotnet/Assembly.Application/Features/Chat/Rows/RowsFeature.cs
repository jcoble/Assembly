using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A session's rows over the socket (plan 5.1, 5.7, C1): the <c>rows</c> topic, keyed by session, with <c>after</c> for a
/// resume, and <c>rows/page</c> for the history above it. A resume catches up at most a page; one further behind, or a
/// first open, starts from the session's last seq, and the page call loads the history before it.
/// </summary>
internal sealed class RowsFeature : IFeature
{
    public const string Topic = "rows";

    /// <summary>The most rows a page call reads, and the most a resume catches up.</summary>
    public const int PageLimit = 500;

    /// <summary>
    /// The most row JSON a resume catches up, in bytes. A socket closes past 16 MB queued (SocketConnection.MaxWaiting),
    /// so a bigger catch-up would close it, and the page would ask again from the same seq forever.
    /// </summary>
    public const int CatchUpBytes = 8 * 1024 * 1024;

    /// <summary>The most bytes of a tool call's output, and of its diff, that <c>rows/tool</c> sends.</summary>
    public const int ToolCap = 1024 * 1024;

    /// <summary>
    /// The most prompts <c>rows/attachments</c> sends, and the characters of each one's end: its "Attached image:" lines
    /// are last. Even escaped at 6 bytes a character that's under 2.5 MB, inside the socket's 4 MB answer.
    /// </summary>
    public const int AttachmentPrompts = 50;
    public const int AttachmentTail = 8192;

    private readonly LiveHub _hub;
    private readonly RowStore _store;
    private readonly SessionStore _sessions;
    private readonly MachineSet _machines;

    public RowsFeature(LiveHub hub, RowStore store, SessionStore sessions, MachineSet machines)
    {
        _hub = hub;
        _store = store;
        _sessions = sessions;
        _machines = machines;
    }

    public void Register(ICallRegistry calls)
    {
        // Under the Topics lock, so a row saved meanwhile is in the catch-up or published after it (5.7). It reads the
        // store, never the hub, whose lock is taken before the Topics one when it publishes.
        calls.Rows(Topic, (session, after, _) =>
        {
            var last = _store.LastSeq(session);
            if (after is >= 0 and var from && from <= last && _store.RowsAfter(session, from, PageLimit + 1) is { Count: <= PageLimit } rows
                && rows.Select(RowWire.ToJson).ToList() is var json && json.Sum(row => (long)Encoding.UTF8.GetByteCount(row)) <= CatchUpBytes)
                return (from, json);
            return (last, []);
        }, (session, _) =>
        {
            // A rows sub has the session's machine watch it, as rows/page and GET /sessions/{id}/rows do, so a remote
            // session's rows are copied in on a resume too, and keep coming while it's open.
            if (_sessions.Get(session) is { } info)
                _machines.Named(info.Machine)?.Watch(info);
        });
        // Opening a session's rows has its machine watch it, as GET /sessions/{id}/rows does.
        calls.Read(Topic, "page", RowJsonContext.Rows.PageArgs, RowJsonContext.Rows.JsonElement, (args, call) =>
        {
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var page = _hub.Page(args.Session, args.Before, Math.Clamp(args.Limit, 1, PageLimit), call.Cancellation);
            var machine = _machines.Named(session.Machine);
            machine?.Watch(session);
            return Task.FromResult(PageJson(page, machine?.ActiveRun(session)));
        }, bulk: true);
        calls.Read(Topic, "tool", RowJsonContext.Rows.ToolArgs, RowJsonContext.Rows.ToolDetail, async (args, call) =>
        {
            var tool = _sessions.Get(args.Session) is { } session
                ? await _machines.For(session).ToolCallAsync(session, args.Item, call.Cancellation)
                : null;
            if (tool is null)
                throw new CallFailed("unknown tool call");
            var (output, outputBytes) = Cut(tool.Output);
            var (diff, diffBytes) = Cut(tool.Diff);
            return new ToolDetail(tool.Summary, tool.Input, output, diff, tool.Path, output != tool.Output || diff != tool.Diff,
                outputBytes, diffBytes);
        }, bulk: true);
        // The drawer's Context tab: what the session's tool calls changed, from this machine's copy of its rows.
        calls.Read(Topic, "files", RowJsonContext.Rows.SessionArgs, RowJsonContext.Rows.IReadOnlyListFileTouched, (args, call) =>
            Task.FromResult(_sessions.Get(args.Session) is null
                ? throw new CallFailed("unknown session")
                : _store.FilesTouched(args.Session, call.Cancellation)));
        // The Context tab's attachments: the prompts that name images, from this machine's copy of the session's rows,
        // and the machine that has the images.
        calls.Read(Topic, "attachments", RowJsonContext.Rows.SessionArgs, RowJsonContext.Rows.SessionAttachments, (args, call) =>
        {
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var prompts = _store.Attachments(args.Session, AttachmentPrompts + 1, AttachmentTail, call.Cancellation);
            return Task.FromResult(new SessionAttachments(session.Machine, [.. prompts.Take(AttachmentPrompts)],
                prompts.Count > AttachmentPrompts));
        });
    }

    // The text cut to at most ToolCap bytes, at a whole character, and its full size in bytes.
    private static (string? Text, long Bytes) Cut(string? text)
    {
        if (text is null)
            return (null, 0);
        var size = Encoding.UTF8.GetByteCount(text);
        if (size <= ToolCap)
            return (text, size);
        var bytes = Encoding.UTF8.GetBytes(text);
        var end = ToolCap;
        while ((bytes[end] & 0xC0) == 0x80)
            end--;
        return (Encoding.UTF8.GetString(bytes, 0, end), bytes.Length);
    }

    // {rows, hasBefore, before, activeRunId}, as GET /sessions/{id}/rows answers it.
    private static JsonElement PageJson(RowPage page, string? activeRun)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, RowWire.Options))
        {
            json.WriteStartObject();
            json.WriteStartArray("rows");
            foreach (var row in page.Rows)
                RowWire.Write(json, row);
            json.WriteEndArray();
            json.WriteBoolean("hasBefore", page.HasBefore);
            if (page.Before is { } before)
                json.WriteNumber("before", before);
            else
                json.WriteNull("before");
            json.WriteString("activeRunId", activeRun);
            json.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary><c>rows/page</c>'s args: the newest items that start below <c>before</c>.</summary>
    internal sealed record PageArgs([property: JsonRequired] string Session, long Before = long.MaxValue, int Limit = 50);

    internal sealed record ToolArgs([property: JsonRequired] string Session, [property: JsonRequired] string Item);

    internal sealed record SessionArgs([property: JsonRequired] string Session);

    /// <summary>
    /// An opened tool row's detail. Its output and diff are cut at <see cref="ToolCap"/> bytes each, with
    /// <c>truncated</c> set and the full sizes in <c>outputBytes</c> and <c>diffBytes</c>.
    /// </summary>
    internal sealed record ToolDetail(string? Summary, string? Input, string? Output, string? Diff, string? Path, bool Truncated,
        long OutputBytes, long DiffBytes);
}
