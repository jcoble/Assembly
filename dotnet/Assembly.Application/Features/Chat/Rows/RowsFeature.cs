using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows.Context;
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
    private readonly IPathProbe _paths;
    private readonly IRowSourceBindings? _sources;

    public RowsFeature(LiveHub hub, RowStore store, SessionStore sessions, MachineSet machines, IPathProbe paths, IRowSourceBindings? sources = null)
    {
        _hub = hub;
        _store = store;
        _sessions = sessions;
        _machines = machines;
        _paths = paths;
        _sources = sources;
    }

    public void Register(ICallRegistry calls)
    {
        // Under the Topics lock, so a row saved meanwhile is in the catch-up or published after it (5.7). It reads the
        // store, never the hub, whose lock is taken before the Topics one when it publishes.
        Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state = (session, after, call) =>
        {
            call.SourceGuard?.EnsureCurrent();
            var last = _store.LastSeq(session);
            if (after is >= 0 and var from && from <= last && _store.RowsAfter(session, from, PageLimit + 1) is { Count: <= PageLimit } rows
                && rows.Select(RowWire.ToJson).ToList() is var json && json.Sum(row => (long)Encoding.UTF8.GetByteCount(row)) <= CatchUpBytes)
                return (from, json);
            return (last, []);
        };
        Action<string, CallContext> opened = (session, call) =>
        {
            if (call.SourceGuard is RowSourceBinding bound) { bound.EnsureCurrent(); bound.Watch(); return; }
            // A rows sub has the session's machine watch it, as rows/page and GET /sessions/{id}/rows do, so a remote
            // session's rows are copied in on a resume too, and keep coming while it's open.
            if (_sessions.Get(session) is { } info)
                _machines.Named(info.Machine)?.Watch(info);
        };
        if (_sources is null) calls.Rows(Topic, state, opened);
        else calls.Rows(Topic, state, opened, _sources);
        // Opening a session's rows has its machine watch it, as GET /sessions/{id}/rows does.
        calls.Read(Topic, "page", RowJsonContext.Rows.PageArgs, RowJsonContext.Rows.JsonElement, async (args, call) =>
        {
            var bound = await CaptureAsync(args.ExpectedSource, args.Session, call);
            if (bound?.Remote == true)
            {
                var remote = await bound.PageAsync(args.Before, Math.Clamp(args.Limit, 1, PageLimit), call.Cancellation);
                bound.EnsureCurrent(); return remote;
            }
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var page = _hub.Page(args.Session, args.Before, Math.Clamp(args.Limit, 1, PageLimit), call.Cancellation);
            if (bound is not null)
            {
                bound.Watch(); bound.EnsureCurrent(); return PageJson(page, bound.ActiveRun);
            }
            var machine = _machines.Named(session.Machine);
            machine?.Watch(session);
            return PageJson(page, machine?.ActiveRun(session));
        }, bulk: true);
        calls.Read(Topic, "subagents", RowJsonContext.Rows.SubagentArgs, RowJsonContext.Rows.SubagentPage, async (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var bound = await CaptureAsync(args.ExpectedSource, args.Session, call);
            if (bound?.Remote == true)
            {
                var remote = await bound.SubagentsAsync(args.After, args.Limit, call.Cancellation);
                bound.EnsureCurrent(); return remote;
            }
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var page = _store.Subagents(args.Session, args.After, args.Limit, call.Cancellation, args.ExpectedSource ?? call.ExpectedSource);
            if (bound is not null) { bound.Watch(); bound.EnsureCurrent(); }
            else _machines.Named(session.Machine)?.Watch(session);
            return page;
        }, bulk: true);
        calls.Read(Topic, "after", RowJsonContext.Rows.JsonElement, RowJsonContext.Rows.JsonElement, (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var session = args.GetProperty("session").GetString() ?? throw new CallFailed("session is required");
            var after = args.GetProperty("after").GetInt64();
            var limit = args.TryGetProperty("limit", out var requested) ? requested.GetInt32() : PageLimit;
            var rows = _store.RowsAfter(session, after, Math.Clamp(limit, 1, PageLimit));
            // A read is one frame, capped at 4 MB even on bulk. Leave space for its result envelope.
            var json = new List<string>();
            var bytes = 0;
            foreach (var row in rows)
            {
                var wire = RowWire.ToJson(row);
                var size = Encoding.UTF8.GetByteCount(wire) + 1;
                if (bytes + size > 4 * 1024 * 1024 - 1024) break;
                json.Add(wire);
                bytes += size;
            }
            using var result = JsonDocument.Parse("{\"rows\":[" + string.Join(',', json) + "]}");
            return Task.FromResult(result.RootElement.Clone());
        }, bulk: true);
        calls.Read(Topic, "toolRaw", RowJsonContext.Rows.RawToolArgs, RowJsonContext.Rows.StoredToolCall, (args, call) =>
        {
            if (_sessions.Get(args.Session) is not { Machine: null }) throw new KeyNotFoundException("unknown tool");
            return Task.FromResult(_store.ToolCallPrefix(args.Session, args.Item, args.OutputMaxBytes, args.DiffMaxBytes, call.Cancellation)
                ?? throw new KeyNotFoundException("unknown tool"));
        }, bulk: true);
        calls.Read(Topic, "tool", RowJsonContext.Rows.ToolArgs, RowJsonContext.Rows.ToolDetail, async (args, call) =>
        {
            var tool = _sessions.Get(args.Session) is { } session
                ? await _machines.For(session).ToolCallAsync(session, args.Item, call.Cancellation)
                : null;
            if (tool is null)
                throw new CallFailed("unknown tool call");
            var (output, outputBytes) = Cut(tool.Output, tool.OutputBytes);
            var (diff, diffBytes) = Cut(tool.Diff, tool.DiffBytes);
            return new ToolDetail(tool.Summary, tool.Input, output, diff, tool.Path, tool.OutputTruncated || outputBytes != Encoding.UTF8.GetByteCount(output ?? "") || diffBytes != Encoding.UTF8.GetByteCount(diff ?? ""),
                outputBytes, diffBytes, tool.Terminals);
        }, bulk: true);
        // Explicit remote context comes from the captured owner, never an unqualified copied-row cache.
        calls.Read(Topic, "files", RowJsonContext.Rows.SessionArgs, RowJsonContext.Rows.IReadOnlyListFileTouched, async (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var bound = await CaptureAsync(args.ExpectedSource, args.Session, call);
            if (bound?.Remote == true)
            {
                var files = await bound.FilesAsync(call.Cancellation);
                bound.EnsureCurrent(); return files;
            }
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            // A remote session's copied rows are read here with no disk check, since this disk isn't the one they name.
            var local = string.IsNullOrEmpty(session.Machine);
            var result = _store.FilesTouched(args.Session, call.Cancellation, local ? _sessions.Root(session) : null,
                local ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null, local ? _paths : null);
            bound?.EnsureCurrent(); return result;
        });
        calls.Read(Topic, "tokens", RowJsonContext.Rows.SessionArgs, RowJsonContext.Rows.SessionTokens, async (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var bound = await CaptureAsync(args.ExpectedSource, args.Session, call);
            if (bound?.Remote == true)
            {
                var tokens = await bound.TokensAsync(call.Cancellation);
                bound.EnsureCurrent(); return tokens;
            }
            if (_sessions.Get(args.Session) is null) throw new CallFailed("unknown session");
            var result = new SessionTokens(_store.SessionTokens(args.Session, call.Cancellation));
            bound?.EnsureCurrent(); return result;
        });
        calls.Read(Topic, "attachments", RowJsonContext.Rows.SessionArgs, RowJsonContext.Rows.SessionAttachments, async (args, call) =>
        {
            call.Cancellation.ThrowIfCancellationRequested();
            var bound = await CaptureAsync(args.ExpectedSource, args.Session, call);
            if (bound?.Remote == true)
            {
                var attachments = await bound.AttachmentsAsync(call.Cancellation);
                bound.EnsureCurrent(); return attachments;
            }
            var session = _sessions.Get(args.Session) ?? throw new CallFailed("unknown session");
            var prompts = _store.Attachments(args.Session, AttachmentPrompts + 1, AttachmentTail, call.Cancellation);
            bound?.EnsureCurrent();
            return new SessionAttachments(session.Machine, [.. prompts.Take(AttachmentPrompts)], prompts.Count > AttachmentPrompts);
        });
    }

    // The text cut to at most ToolCap bytes, at a whole character, and its full size in bytes.
    internal static (string? Text, long Bytes) Cut(string? text, long originalBytes)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) <= ToolCap) return (text, originalBytes);
        var bytes = new byte[ToolCap];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes, true, out _, out var used, out _);
        return (Encoding.UTF8.GetString(bytes, 0, used), originalBytes);
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

    private async Task<RowSourceBinding?> CaptureAsync(ExpectedSource? expected, string session, CallContext call)
    {
        if (expected is not null && call.ExpectedSource is not null && expected != call.ExpectedSource)
            throw new CallBadArguments("The row source arguments contradict the frame.");
        expected ??= call.ExpectedSource;
        if (expected is null) return null;
        if (call.Peer is not OwnerPeer) throw new CallFailed("Forbidden.", "forbidden");
        if (_sources is null) throw new CallFailed("Source-bound rows are unavailable.", "source_changed");
        var binding = await _sources.CaptureAsync(expected, session, call.Cancellation);
        call.SourceGuard = binding;
        binding.EnsureCurrent();
        return binding;
    }

    /// <summary><c>rows/page</c>'s args: the newest items that start below <c>before</c>.</summary>
    internal sealed record PageArgs([property: JsonRequired] string Session, long Before = long.MaxValue, int Limit = 50,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record SubagentArgs([property: JsonRequired] string Session, string? After = null, int Limit = SubagentPage.DefaultLimit,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    internal sealed record ToolArgs([property: JsonRequired] string Session, [property: JsonRequired] string Item);

    internal sealed record RawToolArgs
    {
        public string Session { get; }
        public string Item { get; }
        public int OutputMaxBytes { get; }
        public int DiffMaxBytes { get; }

        [JsonConstructor]
        public RawToolArgs(string session, string item, int outputMaxBytes, int diffMaxBytes)
        {
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(item) ||
                outputMaxBytes is < 1 or > ToolCap || diffMaxBytes is < 1 or > ToolCap)
                throw new JsonException("expected session, item and byte windows from 1 to 1048576");
            (Session, Item, OutputMaxBytes, DiffMaxBytes) = (session, item, outputMaxBytes, diffMaxBytes);
        }
    }

    internal sealed record SessionArgs([property: JsonRequired] string Session,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpectedSource? ExpectedSource = null);

    /// <summary>
    /// An opened tool row's detail. Its output and diff are cut at <see cref="ToolCap"/> bytes each, with
    /// <c>truncated</c> set and the full sizes in <c>outputBytes</c> and <c>diffBytes</c>.
    /// </summary>
    internal sealed record ToolDetail(string? Summary, string? Input, string? Output, string? Diff, string? Path, bool Truncated,
        long OutputBytes, long DiffBytes, IReadOnlyList<ToolTerminalState>? Terminals = null);
}
