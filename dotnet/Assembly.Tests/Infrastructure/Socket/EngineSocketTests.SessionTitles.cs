using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Registered_title_stat_and_bulk_chunk_preserve_exact_one_mib_and_refuse_mutation()
    {
        _f.SeedSession();
        var title = "\0" + new string('x', 1048574) + "😀\0suffix";
        var context = new CommandContext(Guid.NewGuid(), _f.Machine, new(ActorKind.System, "test"), "s", 0, 0, 1000);
        Assert.True(_f.Db.Writer.Write("title", s => _runtime.Commands.ApplyPreparedSessionTitle(s, context, EngineCommands.PrepareSessionTitle(new ReportSessionTitle(title)))).Applied);
        using var normal = await Open();
        await Send(normal, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"session_title\",\"args\":{{\"session\":\"s\"}}}}");
        using var statFrame = JsonDocument.Parse(await OwnerWire(normal));
        var stat = statFrame.RootElement.GetProperty("result").Deserialize(EngineSocketJson.Default.SessionTitleStat)!;
        Assert.Equal(_f.Machine, stat.EngineMachine); Assert.Equal(Encoding.UTF8.GetByteCount(title), stat.Utf8Bytes);
        using var bulk = await Open(bulk: true);
        var args = new SessionTitleChunkArgs("s", stat.Version, stat.Epoch, stat.Utf8Bytes, 0, Machine: stat.EngineMachine);
        async Task<byte[]> Read(SessionTitleChunkArgs request)
        {
            await Send(bulk, $"{{\"t\":\"call\",\"id\":{long.MaxValue},\"feature\":\"engine\",\"method\":\"session_title_chunk\",\"args\":{JsonSerializer.Serialize(request, EngineSocketJson.Default.SessionTitleChunkArgs)}}}");
            return await OwnerWire(bulk);
        }
        var wire = await Read(args); using var frame = JsonDocument.Parse(wire);
        var chunk = frame.RootElement.GetProperty("result").Deserialize(EngineSocketJson.Default.SessionTitleChunk)!;
        Assert.Equal(1048576, chunk.Data.Length); Assert.Equal(1048576, chunk.NextOffset);
        Assert.Equal(Encoding.UTF8.GetBytes(title)[..1048576], chunk.Data);
        Assert.InRange(wire.Length, 1398104, 4 * 1024 * 1024);
        Assert.Equal(wire.Length, SocketDispatcher.Result(long.MaxValue, frame.RootElement.GetProperty("result")).Length);
        Assert.True(_f.Apply(new ReportCapabilities(true), "s", stat.Version, actor: new(ActorKind.System, "test")).Applied);
        using var changed = JsonDocument.Parse(await Read(args));
        Assert.Equal("error", changed.RootElement.GetProperty("t").GetString());
        Assert.Contains("title_changed", changed.RootElement.GetRawText());
    }
}
