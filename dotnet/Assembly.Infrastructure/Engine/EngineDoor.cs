using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
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
    AttachmentStore attachments, TimeProvider time, NeedAnswers needs) : IEngineDoor
{
    public async Task<Func<IWriteScope, bool>> PrepareNeedAnswerAsync(
        NeedAnswerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.Get(request.Session) ?? throw new KeyNotFoundException("unknown session");
        request = request with { Answer = request.Answer.Clone() };
        if (session.Machine is not null)
        {
            if (machines.For(session) is not RemoteMachine remote) throw new CallFailed("unknown owning machine");
            var answered = await remote.AnswerOwnerNeedAsync(session, request.Ask, request.Answer, cancellationToken);
            return _ => answered;
        }
        var target = ((IEngineRuntime)runtime).Read(scope => scope.QueryFirst("""
            SELECT n.id,n.version,s.epoch FROM engine_needs_me n
            JOIN engine_session s ON s.id=n.session_id
            WHERE n.session_id=$session AND n.chat_row_id=$ask AND n.state='open'
            """, row => new { Id = row.Text(0), Version = row.Long(1), Epoch = row.Long(2) },
            new SqlArg("$session", request.Session), new SqlArg("$ask", request.Ask)), cancellationToken);
        if (target is null) return _ => false;
        var command = new WindowCommand(Guid.NewGuid(), target.Id, target.Version, target.Epoch,
            new ResolveNeed("answered", request.Answer));
        var apply = await needs.PrepareOwnerAsync(command, cancellationToken);
        return scope => apply(scope).Applied;
    }

    public async Task<Func<IWriteScope, OwnerPromptAccepted>> PrepareOwnerPromptAsync(
        OwnerPromptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Session) || request.Text is null || request.AttachmentIds is null || request.Epoch < 0)
            throw new CallFailed("invalid owner prompt");
        if (SendFeature.ClientIdError(request.ClientId) is { } error) throw new CallFailed(error);
        // Freeze the original ordered payload before an await or the prepared writer can observe caller mutation.
        request = request with { AttachmentIds = request.AttachmentIds.ToArray() };
        var session = sessions.Get(request.Session) ?? throw new CallFailed("unknown session");
        if (session.Machine is not null)
        {
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
        var remote = ReadRemote(request.Session);
        if (remote is null)
            return runtime.Reader.OwnerPrompts(request, page => Assembly.Infrastructure.Http.JsonReply.Encode(json =>
                System.Text.Json.JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length, cancellationToken);
        return await remote.ReadOwnerPromptPageAsync(request, page => Assembly.Infrastructure.Http.JsonReply.Encode(json =>
            System.Text.Json.JsonSerializer.Serialize(json, page, SessionsJson.Default.OwnerPromptPage)).Length, request.Machine, cancellationToken);
    }

    public async Task<OwnerPromptChunk> ReadOwnerPromptChunkAsync(OwnerPromptChunkArgs request, CancellationToken cancellationToken)
    {
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
}
