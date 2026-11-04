using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Handoff;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Handoffs;

/// <summary>A real provider home on disk behind the real producer, opener and ACP client. The test plays only the agent.</summary>
internal sealed class NativeProvider : IAsyncDisposable
{
    internal const string NativeId = "0194f1d2-7a3b-7c11-9d2e-5f6a7b8c9d0e";
    private readonly BackgroundWork _work;
    internal readonly EngineFixture Engine;
    internal readonly string Vendor;
    internal readonly string Home = Directory.CreateTempSubdirectory("assembly-native-home-").FullName;
    internal readonly FakeLauncher Launcher = new();
    internal readonly SessionStore Sessions;
    internal readonly SessionRegistry Hosts;
    internal readonly NativeSessionProducer Producer;
    internal readonly RowStore Rows;

    internal NativeProvider(EngineFixture engine, string vendor = "claude")
    {
        (Engine, Vendor) = (engine, vendor);
        Launcher.Agents = [new(vendor, vendor, vendor + "-acp", [],
            new Dictionary<string, string> { [AccountContext.ConfigHomeVariable(vendor)!] = Home }, VendorFamily: vendor)];
        Sessions = SessionStore.Open(engine.Db, engine.Time, Home);
        engine.Db.Writer.Write("test.projection-schema", scope =>
        {
            SessionStore.EnsureEngineProjectionSchema(scope);
            return 0;
        });
        Rows = RowStore.Open(engine.Db);
        _work = new(engine.Time, TextWriter.Null);
        Hosts = new(new LiveHub(Rows), Sessions, Launcher, engine.Time, TextWriter.Null, _work);
        Producer = new(engine.Db, Sessions);
        Hosts.BindNativeSessions(Producer);
    }

    internal string Account => NativeLaunchContext.Capture(Launcher.Agents[0],
        ConfigTargetIdentity.EnvironmentOf(Launcher.Agents[0]), "probe", NativeId, "/probe")!.AccountContext;

    internal string TranscriptPath(string folder) => Vendor == "claude"
        ? Path.Combine(Home, "projects", ClaudeSessionFile.ProjectKey(folder), NativeId + ".jsonl")
        : Path.Combine(Home, "sessions", "2026", "11", "04", $"rollout-2026-11-04T10-00-00-{NativeId}.jsonl");

    internal static string Line(string vendor, string folder, string text) => vendor == "claude"
        ? JsonSerializer.Serialize(new { type = "user", sessionId = NativeId, cwd = folder, message = text })
        : JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = folder, message = text } });

    /// <summary>Writes the transcript the provider itself would have left for this conversation.</summary>
    internal string WriteTranscript(string folder)
    {
        var path = TranscriptPath(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var first = Vendor == "claude" ? "" : JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = NativeId, cwd = folder } }) + "\n";
        File.WriteAllText(path, first + Line(Vendor, folder, "remember kiwi") + "\n");
        return path;
    }

    /// <summary>The one real resume exchange: initialize, session/resume in the folder, session/close, then the adapter stops.</summary>
    internal async Task<JsonElement> AnswerResumeAsync()
    {
        var link = await Launcher.NextAsync();
        var open = await link.StartAsync();
        Assert.Equal("session/resume", open.GetProperty("method").GetString());
        Assert.Equal(NativeId, await link.CloseAsync());
        return open.GetProperty("params");
    }

    internal async Task RefuseResumeAsync(int code)
    {
        var link = await Launcher.NextAsync();
        var initialize = await link.ReadAsync("initialize");
        await link.ReplyAsync(initialize, """{"protocolVersion":2,"capabilities":{"session":{}},"authMethods":[],"_meta":{}}""");
        await link.ReplyErrorAsync(await link.ReadAsync("session/resume"), code, "refused by the agent");
    }

    /// <summary>Real target adoption: actual transcript, real prepare, real writer, the one admitted start.</summary>
    internal async Task<(Effect Start, string Folder)> AdoptAsync(HandoffIdentity identity, string acceptFingerprint, string? folder = null)
    {
        folder ??= Directory.CreateTempSubdirectory("assembly-native-target-").FullName;
        var transcript = File.Exists(TranscriptPath(folder)) ? TranscriptPath(folder) : WriteTranscript(folder);
        var producer = new NativeHandoffProducer(Launcher, Sessions);
        var capture = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(transcript)));
        var prepared = await producer.PrepareAsync(Engine.Machine, new(identity,
            new(Vendor, "model", folder, "Adopted", NativeId, identity.SourceMachine, identity.SourceSessionId, identity.NextEpoch),
            acceptFingerprint, capture), default);
        var ack = Engine.Db.Writer.Write("test.native-adopt", scope =>
            producer.Adopt(scope, new EngineCommands(Engine.Machine, Engine.Limits), prepared, 1000));
        Assert.True(ack.Receipt?.Applied == true, ack.Reason);
        var id = $"start/{identity.HandoffId}/1";
        Assert.True(Engine.Apply(new ClaimEffect(), id, epoch: identity.NextEpoch, actor: new(ActorKind.System, "engine.worker")).Applied);
        return (EffectTransitions.Read(Engine.Db, id, default)!, folder);
    }

    internal AdapterEffects Effects() => new(Engine.Db, Hosts, Sessions, Rows);

    internal Assembly.Engine.Contracts.CommandResult Complete(Effect effect, EffectReceipt receipt, long epoch) =>
        Engine.Apply(new CompleteEffect(receipt), effect.Id, 1, epoch, new(ActorKind.System, "engine.worker"));

    internal HandoffAck Ack(HandoffIdentity identity)
    {
        using var scope = Engine.Db.OpenSnapshot(default);
        return new EngineCommands(Engine.Machine, Engine.Limits).ReadHandoffAck(scope, identity);
    }

    /// <summary>Drives a whole target resume with a willing agent and returns the owner's own evidence.</summary>
    internal async Task<HandoffTargetEvidence> ResumeAsync(HandoffIdentity identity, string acceptFingerprint)
    {
        var (start, _) = await AdoptAsync(identity, acceptFingerprint);
        var running = Effects().ExecuteAsync(start, default);
        await AnswerResumeAsync();
        var receipt = await running;
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        var completed = Complete(start, receipt, identity.NextEpoch);
        Assert.True(completed.Applied, completed.Error);
        return Assert.IsType<HandoffTargetEvidence>(Ack(identity).Target);
    }

    public async ValueTask DisposeAsync()
    {
        await Hosts.DisposeAsync();
        await _work.DisposeAsync();
        Directory.Delete(Home, true);
    }

}
