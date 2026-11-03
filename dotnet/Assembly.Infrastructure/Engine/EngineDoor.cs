using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Engine.Needs;

namespace Assembly.Infrastructure.Engine;

/// <summary>Prepares an owner prompt for the caller's existing writer; performs no provider send.</summary>
public sealed partial class EngineDoor(EngineRuntime runtime, SessionStore sessions, MachineSet machines,
    AttachmentStore attachments, TimeProvider time, NeedAnswers needs, LiveHub? hub = null,
    IRowSourceBindings? sources = null) : IEngineDoor
{
    public async Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(
        NeedAnswerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedSource is not null) throw SourceChanged();
        var session = sessions.Get(request.Session) ?? throw new KeyNotFoundException("unknown session");
        request = request with { Answer = request.Answer.Clone() };
        if (session.Machine is not null)
        {
            if (machines.For(session) is not RemoteMachine remote) throw new CallFailed("unknown owning machine");
            var answered = await remote.AnswerOwnerNeedAsync(session, request.Ask, request.Answer, cancellationToken);
            return _ => answered;
        }
        return await PrepareLocalNeedAnswerAsync(request, cancellationToken);
    }

    private async Task<Func<IWriteScope, bool>> PrepareLocalNeedAnswerAsync(NeedAnswerRequest request, CancellationToken cancellationToken)
    {
        var target = ((IEngineRuntime)runtime).Read(scope => scope.QueryFirst("""
            SELECT n.id,n.version,s.epoch FROM engine_needs_me n
            JOIN engine_session s ON s.id=n.session_id
            WHERE n.session_id=$session AND n.chat_row_id=$ask AND n.state='open'
            """, row => new { Id = row.Text(0), Version = row.Long(1), Epoch = row.Long(2) },
            new SqlArg("$session", request.Session), new SqlArg("$ask", request.Ask)), cancellationToken);
        if (target is null) return _ => false;
        var command = new WindowCommand(Guid.NewGuid(), target.Id, target.Version, target.Epoch,
            new ResolveNeed("answered", request.Answer));
        var apply = await needs.PrepareOwnerAsync(command, cancellationToken, new("chat", null, null));
        return scope => apply(scope).Applied;
    }

    public async Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(
        OwnerPromptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedSource is not null) throw SourceChanged();
        if (string.IsNullOrWhiteSpace(request.Session) || request.Text is null || request.AttachmentIds is null || request.Epoch < 0)
            throw new CallFailed("invalid owner prompt");
        if (SendFeature.ClientIdError(request.ClientId) is { } error) throw new CallFailed(error);
        // Freeze the original ordered payload before an await or the prepared writer can observe caller mutation.
        request = request with { AttachmentIds = request.AttachmentIds.ToArray() };
        var session = sessions.Get(request.Session) ?? throw new CallFailed("unknown session");
        if (session.Machine is not null)
        {
            if (request.LocalOnly) throw new CallFailed("session moved");
            if (machines.For(session) is not RemoteMachine remote) throw new CallFailed("unknown owning machine");
            var accepted = await remote.SendOwnerPromptAsync(request, cancellationToken);
            return _ => accepted;
        }
        ValidateLocal(request);
        var command = new SendOwnerPrompt(request.Session, request.Text, request.ClientId, request.AttachmentIds, request.Epoch);
        var prepared = OwnerPromptFingerprint.Prepare(runtime.Machine, command, cancellationToken);
        var id = OwnerPromptIdentity.MessageId(request.Session, request.ClientId);
        var commandId = OwnerPromptIdentity.CommandId(runtime.Machine, request.Session, request.ClientId);
        return scope =>
        {
            var owner = scope.QueryFirst("""
                SELECT machine,parent_session_id,helper,EXISTS(SELECT 1 FROM session_info parent WHERE parent.session_id=child.parent_session_id)
                FROM session_info child WHERE session_id=$id
                """, r => new { Machine = r.IsNull(0) ? null : r.Text(0), Native = !r.IsNull(1) && r.Long(2) == 0 && r.Long(3) != 0 },
                new SqlArg("$id", request.Session)) ?? throw new CallFailed("unknown session");
            if (owner.Machine is not null) throw new CallFailed("session moved");
            if (owner.Native) throw new CallFailed("a subagent's session takes prompts from its parent only");
            var context = new CommandContext(commandId, runtime.Machine, new(ActorKind.Window, "owner"), id, 0, 0,
                time.GetUtcNow().ToUnixTimeMilliseconds());
            var result = runtime.Commands.ApplyOwnerPrompt(scope, context, prepared);
            if (!result.Applied) throw new CallFailed(result.Error == "command_id_conflict" ? "client_id_conflict" : result.Error ?? "owner prompt refused");
            return new(request.Session, id, request.ClientId, "queued", result.Version, result.Seq, result.Epoch, null);
        };
    }

    public async Task<OwnerPromptPage> ReadOwnerPromptsAsync(OwnerPromptReadArgs request, CancellationToken cancellationToken)
    {
        if (request.ExpectedSource is not null) throw SourceChanged();
        var remote = ReadRemote(request.Session);
        if (remote is null)
            return runtime.Reader.OwnerPrompts(request, page => Assembly.Infrastructure.Http.JsonReply.Encode(json =>
                System.Text.Json.JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length, cancellationToken);
        return await remote.ReadOwnerPromptPageAsync(request, page => Assembly.Infrastructure.Http.JsonReply.Encode(json =>
            System.Text.Json.JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length, request.Machine, cancellationToken);
    }

    public async Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CancellationToken cancellationToken)
    {
        if (request.ExpectedSource is not null) throw SourceChanged();
        var remote = ReadRemote(request.Session);
        if (remote is null) return runtime.Reader.OwnerPromptContent(request, cancellationToken);
        var result = await remote.ReadOwnerPromptAsync("owner_prompt_chunk", System.Text.Json.JsonSerializer.SerializeToElement(
            request with { Machine = null }, SessionsJson.Default.OwnerPromptChunkArgs), true, request.Machine, cancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize(result, SessionsJson.Default.OwnerPromptChunk)!;
    }

    private RemoteMachine? ReadRemote(string sessionId)
    {
        var session = sessions.Get(sessionId) ?? throw new KeyNotFoundException("unknown session");
        if (session.Machine is null) return null;
        return machines.For(session) as RemoteMachine ?? throw new CallFailed("unknown owning machine");
    }

    private void ValidateLocal(OwnerPromptRequest request)
    {
        var session = sessions.Get(request.Session) ?? throw new CallFailed("unknown session");
        if (session.Machine is not null) throw new CallFailed("session moved");
        if (!session.Helper && session.ParentSessionId is { } parent && sessions.Get(parent) is not null)
            throw new CallFailed("a subagent's session takes prompts from its parent only");
        if (request.AttachmentIds.Any(reference => !attachments.Owns(request.Session, reference)))
            throw new CallFailed("attachments must belong to the selected session");
    }

    public async Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareSourceOwnerPromptAsync(OwnerPromptRequest request, CallContext call)
    {
        var source = SendFeature.Source(request.ExpectedSource, call);
        if (source is null) return await PrepareOwnerPromptAsync(request, call.Cancellation);
        if (request.AttachmentIds is null) throw new CallFailed("invalid owner prompt");
        request = request with { AttachmentIds = request.AttachmentIds.ToArray() };
        var captured = await CaptureChatAsync(request.Session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        if (captured.Machine is RemoteMachine remote)
        {
            if (request.LocalOnly) throw new CallFailed("session moved");
            var accepted = await remote.SendOwnerPromptAsync(request, stop.Token, captured.Live);
            return Commit(captured, call, _ => accepted);
        }
        var apply = await PrepareOwnerPromptAsync(request with { ExpectedSource = null, LocalOnly = true }, stop.Token);
        return Commit(captured, call, apply);
    }

    public async Task<Func<IWriteScope, bool>> PrepareSourceNeedAnswerAsync(NeedAnswerRequest request, CallContext call)
    {
        var source = SendFeature.Source(request.ExpectedSource, call);
        if (source is null) return await PrepareNeedAnswerAsync(request, call.Cancellation);
        request = request with { Answer = request.Answer.Clone() };
        var captured = await CaptureChatAsync(request.Session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        if (captured.Machine is RemoteMachine remote)
        {
            var answered = await remote.AnswerBoundAsync(request.Session, request.Ask, request.Answer, captured.Live!, stop.Token);
            return Commit(captured, call, _ => answered);
        }
        var apply = await PrepareLocalNeedAnswerAsync(request with { ExpectedSource = null }, stop.Token);
        return Commit(captured, call, apply);
    }

    public async Task<Func<IWriteScope, bool>> PrepareStopAsync(string session, string run, ExpectedSource source, CallContext call)
    {
        var captured = await CaptureChatAsync(session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        if (captured.Machine is RemoteMachine remote)
        {
            var stopped = await remote.StopBoundAsync(session, run, captured.Live!, stop.Token);
            return Commit(captured, call, _ => stopped);
        }
        // The actual owner, never a copied run or a caller's guessed session, establishes membership.
        if (hub is null || hub.SessionOfRun(run) != session) throw new CallFailed("that run isn't running");
        BeforeEffect(captured, call);
        var result = await captured.Machine.StopAsync(captured.Session, run, stop.Token);
        return Commit(captured, call, _ => result);
    }

    public async Task<Func<IWriteScope, bool>> PrepareStopTaskAsync(string session, string task, ExpectedSource source, CallContext call)
    {
        var captured = await CaptureChatAsync(session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        var result = captured.Machine is RemoteMachine remote
            ? await remote.StopTaskBoundAsync(session, task, captured.Live!, stop.Token)
            : await captured.Machine.StopTaskAsync(captured.Session, task, stop.Token);
        return Commit(captured, call, _ => result);
    }

    public async Task<SessionConfig> ReadSessionConfigAsync(string session, ExpectedSource source, CallContext call)
    {
        var captured = await CaptureChatAsync(session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        var result = captured.Machine is RemoteMachine remote
            ? await remote.ConfigBoundAsync(session, captured.Live!, stop.Token)
            : await captured.Machine.ConfigAsync(captured.Session, stop.Token);
        BeforeEffect(captured, call);
        return result;
    }

    public async Task<SessionConfig> WarmSessionConfigAsync(string session, string target, ExpectedSource source, CallContext call)
    {
        var captured = await CaptureChatAsync(session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        var result = captured.Machine is RemoteMachine remote
            ? await remote.WarmConfigBoundAsync(session, target, captured.Live!, stop.Token)
            : await captured.Machine.WarmConfigAsync(captured.Session, target, stop.Token);
        BeforeEffect(captured, call);
        return result;
    }

    public async Task<Func<IWriteScope, SessionConfig>> PrepareSessionConfigAsync(string session, string config, string value,
        string? target, ExpectedSource source, CallContext call)
    {
        var captured = await CaptureChatAsync(session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        var result = captured.Machine is RemoteMachine remote
            ? await remote.SetConfigBoundAsync(session, config, value, target, captured.Live!, stop.Token)
            : await captured.Machine.SetConfigAsync(captured.Session, config, value, stop.Token, target);
        return Commit(captured, call, _ => result);
    }

    private async Task<SessionRowSources.CommandBinding> CaptureChatAsync(string session, ExpectedSource source, CallContext call)
    {
        source = SendFeature.Source(source, call) ?? throw SourceChanged();
        call.Cancellation.ThrowIfCancellationRequested();
        if (sources is not SessionRowSources actual) throw SourceChanged();
        var captured = await actual.CaptureCommandAsync(source, session, call.Cancellation);
        call.SourceGuard = captured.Guard; // Dispatcher owns it through the writer and queued reply.
        BeforeEffect(captured, call);
        return captured;
    }

    private static void BeforeEffect(SessionRowSources.CommandBinding captured, CallContext call)
    {
        captured.Guard.EnsureDisplayCurrent();
        call.Cancellation.ThrowIfCancellationRequested();
    }

    private static CancellationTokenSource Stop(CallContext call, SessionRowSources.CommandBinding captured) =>
        CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, captured.Guard.Changed);

    private static Func<IWriteScope, T> Commit<T>(SessionRowSources.CommandBinding captured, CallContext call, Func<IWriteScope, T> apply) => scope =>
    {
        call.Cancellation.ThrowIfCancellationRequested();
        captured.Guard.EnsureDisplayCurrent(scope);
        return apply(scope);
    };

    private static CallFailed SourceChanged() => new("The selected chat source changed.", "source_changed");

    public async Task<OwnerPromptPage> ReadSourceOwnerPromptsAsync(OwnerPromptReadArgs request, CallContext call)
    {
        var source = PendingSource(request.Machine, request.ExpectedSource, call);
        if (call.ReplyId is not { } id) throw new CallFailed("reply metadata unavailable");
        var captured = await CaptureChatAsync(request.Session, source, call);
        using var stop = Stop(call, captured);
        int Bytes(OwnerPromptPage page) => Assembly.Infrastructure.Socket.SocketDispatcher.Result(id,
            System.Text.Json.JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.OwnerPromptPage)).Length;
        BeforeEffect(captured, call);
        var page = captured.Machine is RemoteMachine remote
            ? await remote.ReadBoundOwnerPromptsAsync(request, Bytes, captured.Live!, stop.Token)
            : runtime.Reader.OwnerPrompts(request with { Machine = null, ExpectedSource = null }, Bytes, stop.Token);
        BeforeEffect(captured, call);
        if (page.EngineMachine != source.Machine) throw SourceChanged();
        return page;
    }

    public async Task<OwnerPromptChunk> ReadSourceOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CallContext call)
    {
        var source = PendingSource(request.Machine, request.ExpectedSource, call);
        var captured = await CaptureChatAsync(request.Session, source, call);
        using var stop = Stop(call, captured);
        BeforeEffect(captured, call);
        var chunk = captured.Machine is RemoteMachine remote
            ? await remote.ReadBoundOwnerPromptChunkAsync(request, captured.Live!, stop.Token)
            : runtime.Reader.OwnerPromptContent(request with { Machine = null, ExpectedSource = null }, stop.Token);
        BeforeEffect(captured, call);
        if (chunk.ContentSha256 != request.ContentSha256 || chunk.Part != request.Part || chunk.Offset != request.Offset)
            throw SourceChanged();
        return chunk;
    }

    private static ExpectedSource PendingSource(string? machine, ExpectedSource? source, CallContext call)
    {
        var expected = SendFeature.Source(source, call) ?? throw SourceChanged();
        if (machine is not null && machine != expected.Machine)
            throw new CallBadArguments("The pending-message machine contradicts its source.");
        return expected;
    }
}
