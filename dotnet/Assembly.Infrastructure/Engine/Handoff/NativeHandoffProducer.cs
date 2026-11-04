using System.Security.Cryptography;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Handoffs;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>Checks the actual transferred target transcript before the one admission writer allocates a start.</summary>
public sealed class NativeHandoffProducer(IAgentLauncher launcher, SessionStore sessions)
{
    public async Task<PreparedNativeAdoption> PrepareAsync(string machine, HandoffAdoptArgs args, CancellationToken ct)
    {
        var configured = ConfigTargetIdentity.Capture(launcher, args.Command.Provider);
        var launch = NativeLaunchContext.Capture(configured.Descriptor, configured.Environment, configured.TargetId,
            args.Command.ProviderSessionId, args.Command.Folder) ?? throw new HandoffRefused("unsupported");
        var capture = NativeSessionProducer.ResolveFile(launch, machine, args.Identity.HandoffId, args.Identity.NextEpoch);
        var path = NativeSessionFileInputs.ResolveProviderPath(capture.Storage.ProviderHome, capture.Current.SelectedRelativePath);
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != args.TransferCaptureSha256) throw new HandoffRefused("transfer_capture_changed");
        ct.ThrowIfCancellationRequested();
        ConfigTargetIdentity.Validate(launcher, configured.Descriptor, configured.Environment);
        return new(args, launch, capture);
    }

    public HandoffAck Adopt(IWriteScope scope, EngineCommands commands, PreparedNativeAdoption prepared, long nowMs)
    {
        var configured = ConfigTargetIdentity.Capture(launcher, prepared.Launch.ProviderId);
        if (NativeLaunchContext.Capture(configured.Descriptor, configured.Environment, configured.TargetId,
                prepared.Launch.AcpSessionId, prepared.Launch.Folder) != prepared.Launch)
            throw new HandoffRefused("native_witness_mismatch");
        return commands.ApplyNativeHandoffAdopt(scope, prepared, nowMs, (writer, id, repo) =>
        {
            var command = prepared.Args.Command;
            sessions.ProjectEngineSession(writer, new(id, command.Provider, repo ?? command.Folder, command.Title,
                command.ProviderSessionId, nowMs), command.Model, command.Folder);
            sessions.RetainNativeLaunch(writer, id, prepared.Launch);
            sessions.RetainNativeWitness(writer, prepared.Launch, prepared.Capture);
        });
    }
}
