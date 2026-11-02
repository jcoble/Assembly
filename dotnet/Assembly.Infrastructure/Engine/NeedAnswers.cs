using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.NeedsMe;

namespace Assembly.Infrastructure.Engine;

/// <summary>Prepares fixed need operations outside their caller's existing atomic writer.</summary>
public sealed class NeedAnswers(EngineRuntime runtime, SessionRegistry sessions, TimeProvider time)
{
    internal Task<Func<IWriteScope, CommandResult>> PrepareOwnerAsync(WindowCommand request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Command is not ResolveNeed { State: "answered" } original)
            throw new ArgumentException("An owner answered need command is required.", nameof(request));
        var target = ((IEngineRuntime)runtime).Read(scope => scope.QueryFirst("""
            SELECT n.session_id,n.provider_request_id,n.chat_row_id,s.machine,s.holder,s.reserved_epoch,s.state
            FROM engine_needs_me n JOIN engine_session s ON s.id=n.session_id WHERE n.id=$id
            """, row => new
        {
            Session = row.Text(0),
            Request = row.IsNull(1) ? null : row.Text(1),
            Chat = row.IsNull(2) ? null : row.Text(2),
            Machine = row.Text(3),
            Holder = row.Text(4),
            Reserved = !row.IsNull(5),
            State = row.Text(6)
        }, new SqlArg("$id", request.EntityId)), cancellationToken);
        PreparedProviderAnswer? captured = null;
        string? error = target is null ? "answer_preparation_required" : null;
        if (target?.Request is not null)
        {
            if (target.Machine == runtime.Machine && target.Holder == "local" && !target.Reserved &&
                target.State is not ("foreign" or "handing_off" or "ended") &&
                !string.IsNullOrWhiteSpace(target.Chat) && original.Answer is { } answer)
                captured = sessions.For(target.Session).PrepareProviderAnswer(target.Chat, answer);
            if (captured is null) error = "answer_preparation_required";
        }
        var prepared = EngineCommands.PrepareOwnerNeedCommand(request.CommandId, runtime.Machine, request.EntityId,
            request.ExpectedVersion, request.Epoch, original, captured, error, cancellationToken);
        return Task.FromResult<Func<IWriteScope, CommandResult>>(scope =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Commands.ApplyPreparedOwnerNeed(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds());
        });
    }
}
