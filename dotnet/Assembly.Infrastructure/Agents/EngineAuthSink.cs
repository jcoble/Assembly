using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Agents;

/// <summary>Fixed callbacks for one runtime lifetime; original probe and begin claims remain authoritative.</summary>
internal sealed class EngineAuthSink(EngineRuntime runtime, TimeProvider time)
{
    private readonly CancellationToken _stopping = runtime.Stopping;

    internal Task<CommandResult> RecordCheckAsync(ProviderProbeBinding probe, JsonElement safeFacts, string installation,
        string authState, string? reason, CancellationToken runtimeStop)
    {
        _stopping.ThrowIfCancellationRequested(); runtimeStop.ThrowIfCancellationRequested();
        var prepared = EngineCommands.PrepareProviderCheck(new(installation, authState, safeFacts, reason, probe));
        var context = new CommandContext(Guid.NewGuid(), runtime.Machine, new(ActorKind.System, "provider.effects"),
            probe.TargetId, probe.ProviderVersion, 0, time.GetUtcNow().ToUnixTimeMilliseconds());
        var result = ((IEngineRuntime)runtime).Write(scope =>
        {
            _stopping.ThrowIfCancellationRequested(); runtimeStop.ThrowIfCancellationRequested();
            return runtime.Commands.ApplyProviderCheck(scope, context, prepared);
        });
        return Task.FromResult(result);
    }

    internal CommandResult Progress(SignInAttemptBinding attempt, string state, string? fixedReason)
    {
        _stopping.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(attempt);
        var commandId = Guid.NewGuid();
        return ((IEngineRuntime)runtime).Write(scope =>
        {
            _stopping.ThrowIfCancellationRequested();
            var version = scope.QueryFirst("SELECT version FROM engine_sign_in WHERE id=$id AND machine=$machine",
                row => (long?)row.Long(0), new SqlArg("$id", attempt.OperationId), new("$machine", runtime.Machine));
            if (version is null) return new CommandResult(false, false, "bad_args", 0, 0, 0);
            var context = new CommandContext(commandId, runtime.Machine, new(ActorKind.System, "sign_in.effects"),
                attempt.OperationId, version.Value, 0, time.GetUtcNow().ToUnixTimeMilliseconds());
            return runtime.Commands.Apply(scope, context, new SignInProgress(state, fixedReason, attempt));
        });
    }
}
