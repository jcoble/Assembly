using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Projects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionRailCanonicalSourceTests
{
    [Fact]
    public async Task Current_mirror_roots_need_no_facade_and_unproven_copies_preserve_unknown_owner_after_replacement()
    {
        var home = Directory.CreateTempSubdirectory("assembly-rail-canonical-");
        var launcher = new FakeLauncher();
        try
        {
            await using var hosts = new TwoHosts(forwardCommands: true, machineLauncher: launcher, checkouts: home.FullName);
            var projects = new ProjectStore(hosts.Mac.Fixture.Db, hosts.Mac.Fixture.Time);
            hosts.Remote.SessionFeature = host =>
            {
                var machines = new MachineSet(new FakeMachine(null, host.Sessions));
                return new SessionsFeature(host.Sessions, machines,
                    new SessionRowSources(host.Fixture.Db, host.Sessions, machines),
                    owningMachine: host.Runtime.Machine, owningHome: home.FullName,
                    remoteFolders: new LocalProjectFolders(new GitCli()));
            };
            hosts.Mac.SessionFeature = host =>
            {
                var machines = new MachineSet(new FakeMachine(null, host.Sessions));
                return new SessionsFeature(host.Sessions, machines,
                    new DeferredSources(() => new SessionRowSources(host.Fixture.Db, host.Sessions, machines, hosts.Connections)),
                    owningMachine: host.Runtime.Machine, owningHome: home.FullName,
                    remoteCreate: (args, call) => hosts.Connections.CreateSessionAsync(host.Fixture.Db, projects,
                        host.Runtime.Machine, args, call));
            };
            await hosts.Start();
            hosts.Remote.Fixture.SeedSession("same-id", state: "idle");
            hosts.Remote.Fixture.SeedSession("ordinary-child", state: "idle", parentId: "s");
            Assert.Equal(0, hosts.Mac.Fixture.Count("session_info"));
            await hosts.StartMirror();
            using var socket = await ApiTests.OpenOwnerSocketAsync(hosts.Mac.Address, "isolated-token");
            var original = Source(hosts);
            var first = await Read(socket);
            var roots = Sessions(first);
            Assert.Equal(2, roots.Length);
            Assert.Equal(new[] { "s", "same-id" }, roots.Select(row => row.GetProperty("sessionId").GetString()).Order());
            Assert.Contains("codex", first.GetProperty("agents").EnumerateArray().Select(value => value.GetString()));
            foreach (var root in roots)
            {
                AssertSource(root, original);
                Assert.Equal("Test", root.GetProperty("title").GetString());
                foreach (var field in new[] { "preview", "lastError", "pinnedAt", "orchestratorAt", "position", "resumeCommand", "lastSeenMs" })
                    Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
            }
            Assert.Equal(0, hosts.Mac.Fixture.Count("session_info"));
            using var pushed = await ApiTests.OpenOwnerSocketAsync(hosts.Mac.Address, "isolated-token");
            await Send(pushed, $$"""{"t":"sub","id":77,"stream":"engine","topic":"sessions","machine":"{{original.Machine}}"}""");
            var initial = await ApiTests.ReadFrameAsync(pushed);
            Assert.Equal("engine_snapshot", initial.GetProperty("t").GetString());
            Assert.Equal(original.Machine, initial.GetProperty("machine").GetString());
            Assert.False(initial.GetProperty("more").GetBoolean());
            Assert.Equal(3, initial.GetProperty("items").GetArrayLength());
            Assert.Contains(initial.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetString() == "ordinary-child");
            await Send(pushed, $$"""{"t":"ack","id":77,"stream":"engine","after":{{initial.GetProperty("through").GetInt64()}}}""");

            // Actual imported local Engine identity collides with a remote id. Its transcript is not remote metadata.
            hosts.Mac.Fixture.Db.Writer.Write("test.local-import", scope =>
            {
                var now = hosts.Mac.Fixture.Time.GetUtcNow().ToUnixTimeMilliseconds();
                SessionImport.Import(scope, hosts.Mac.Runtime.Machine, now,
                    [new("same-id", null, "claude", null, home.FullName, "Local only", "")]);
                hosts.Mac.Sessions.ProjectEngineSession(scope, new("same-id", "claude", home.FullName, "Local only", null, now), null, home.FullName);
                return 0;
            });
            var rows = RowStore.Open(hosts.Mac.Fixture.Db);
            var converter = new RowConverter();
            rows.Commit("same-id", converter.Apply(new Assembly.Domain.Events.TurnStarted(1000, "source-derived-local-turn", "Local transcript")));
            rows.Commit("same-id", converter.Apply(new Assembly.Domain.Events.TurnEnded(1001, "end_turn", null)));
            var collision = Assert.Single(Sessions(await Read(socket)), row => row.GetProperty("sessionId").GetString() == "same-id");
            AssertSource(collision, original);
            Assert.NotEqual("Local only", collision.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, collision.GetProperty("preview").ValueKind);
            Assert.False(collision.GetProperty("unread").GetBoolean());

            // Seed the duplicate control through the real owner-qualified create/Facts pipeline, never Add/manual cache SQL.
            var project = hosts.Mac.Fixture.Db.Writer.Write("test.project", scope =>
                projects.AddOrRestore(scope, "workbox-test", home.FullName, "Owned project", null).Project);
            var create = new SessionsFeature.CreateArgs("claude", "rail_accepted", project.Id, "workbox-test", ExpectedSource: original);
            var accepted = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 2, "sessions", "create",
                JsonSerializer.Serialize(create, SessionsJson.Default.CreateArgs)));
            var acceptedId = accepted.GetProperty("sessionId").GetString()!;
            Assert.Equal("workbox-test", hosts.Mac.Sessions.Get(acceptedId)!.Machine);
            await hosts.Reached(hosts.Remote.Through);
            // Existing EngineMirror -> EnginePublisher -> registered sessions stream pushes the accepted row.
            JsonElement change;
            var pushedThrough = initial.GetProperty("through").GetInt64();
            do
            {
                change = await ApiTests.ReadFrameAsync(pushed);
                Assert.Equal("engine_events", change.GetProperty("t").GetString());
                Assert.Equal(original.Machine, change.GetProperty("machine").GetString());
                Assert.Equal(pushedThrough, change.GetProperty("after").GetInt64());
                Assert.True(change.GetProperty("through").GetInt64() > pushedThrough);
                pushedThrough = change.GetProperty("through").GetInt64();
                await Send(pushed, $$"""{"t":"ack","id":77,"stream":"engine","after":{{change.GetProperty("through").GetInt64()}}}""");
            } while (!change.GetProperty("events").EnumerateArray().Any(item =>
                item.GetProperty("entityKind").GetString() == "session" && item.GetProperty("entityId").GetString() == acceptedId));
            var unproven = Sessions(await Read(socket)).Where(row => row.GetProperty("sessionId").GetString() == acceptedId).ToArray();
            Assert.Equal(2, unproven.Length);
            Assert.Single(unproven, row => row.GetProperty("source").ValueKind == JsonValueKind.Null);
            // Acceptance stored no durable owner ID: retain the unknown facade instead of guessing its tuple.
            var deduplicated = Sessions(await Read(socket));
            Assert.Equal(4, deduplicated.Length);
            AssertSource(Assert.Single(deduplicated, row => row.GetProperty("sessionId").GetString() == acceptedId &&
                row.GetProperty("source").ValueKind != JsonValueKind.Null), original);
            Assert.Equal(0, launcher.Launches);
            var unknownCursorRow = Assert.Single(deduplicated, row => row.GetProperty("sessionId").GetString() == acceptedId &&
                row.GetProperty("source").ValueKind == JsonValueKind.Null);
            var knownCursorRow = Assert.Single(deduplicated, row => row.GetProperty("sessionId").GetString() == acceptedId &&
                row.GetProperty("source").ValueKind != JsonValueKind.Null);
            using (var unknownOrder = JsonDocument.Parse(unknownCursorRow.GetProperty("ordering").GetString()!))
            using (var knownOrder = JsonDocument.Parse(knownCursorRow.GetProperty("ordering").GetString()!))
            {
                Assert.Equal(unknownOrder.RootElement[0].GetRawText(), knownOrder.RootElement[0].GetRawText());
                Assert.Equal(unknownOrder.RootElement[1].GetRawText(), knownOrder.RootElement[1].GetRawText());
            }
            var afterUnknown = new SessionsFeature.RailArgs(Locations: ["remote"], GroupBy: new(false, false), Dir: "asc",
                After: new(unknownCursorRow.GetProperty("sectionIndex").GetInt32(), "", "", unknownCursorRow.GetProperty("header").GetInt32(),
                    unknownCursorRow.GetProperty("ordering").GetString()!), Limit: 1);
            var next = Sessions(ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 3, "sessions", "rail",
                JsonSerializer.Serialize(afterUnknown, SessionsJson.Default.RailArgs))));
            var nextKnown = Assert.Single(next);
            Assert.Equal(acceptedId, nextKnown.GetProperty("sessionId").GetString());
            AssertSource(nextKnown, original);

            await hosts.DisconnectMirror();
            var nextOwner = new TwoHosts.Host(windowCalls: true);
            nextOwner.Fixture.SeedSession(acceptedId, state: "idle", provider: "claude");
            await hosts.ReplaceAtSameEndpoint(nextOwner);
            await hosts.ReconnectSaved();
            await hosts.Reached(hosts.Remote.Through);
            var replacement = Source(hosts);
            Assert.NotEqual(original.Machine, replacement.Machine);
            var replaced = Sessions(await Read(socket));
            var current = replaced.Where(row => row.GetProperty("source").ValueKind != JsonValueKind.Null).ToArray();
            Assert.Equal(2, current.Length);
            foreach (var row in current) AssertSource(row, replacement);
            Assert.Contains(current, row => row.GetProperty("sessionId").GetString() == acceptedId);
            Assert.DoesNotContain(replaced, row => row.GetProperty("source").ValueKind != JsonValueKind.Null &&
                row.GetProperty("source").GetProperty("machine").GetString() == original.Machine);
            var oldCopy = Assert.Single(replaced, row => row.GetProperty("sessionId").GetString() == acceptedId &&
                row.GetProperty("source").ValueKind == JsonValueKind.Null);
            Assert.Equal(JsonValueKind.Null, oldCopy.GetProperty("source").ValueKind);
            Assert.Equal(0, launcher.Launches);
        }
        finally { home.Delete(true); }
    }

    private static Task Send(WebSocket socket, string frame) => socket.SendAsync(Encoding.UTF8.GetBytes(frame),
        WebSocketMessageType.Text, true, CancellationToken.None);
    private static ExpectedSource Source(TwoHosts hosts) => hosts.Mac.Fixture.Db.QueryFirst("""
        SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
        """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default,
        new SqlArg("$machine", hosts.Remote.Runtime.Machine))!;
    private static async Task<JsonElement> Read(WebSocket socket) => ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1,
        "sessions", "rail", """{"locations":["remote"],"groupBy":{"status":false,"project":false},"limit":200}"""));
    private static JsonElement[] Sessions(JsonElement rail) => rail.GetProperty("sessions").EnumerateArray()
        .Where(row => row.GetProperty("kind").GetString() == "session").ToArray();
    private static void AssertSource(JsonElement row, ExpectedSource expected)
    {
        var source = row.GetProperty("source");
        Assert.Equal(expected.Machine, source.GetProperty("machine").GetString());
        Assert.Equal(expected.Generation, source.GetProperty("generation").GetInt64());
        Assert.Equal(expected.RouteMachine, source.GetProperty("routeMachine").GetString());
    }
    private sealed class DeferredSources(Func<IRowSourceBindings> actual) : IRowSourceBindings
    {
        public Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct) => actual().CaptureAsync(source, session, ct);
        public Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct) => actual().CaptureEngineAsync(source, ct);
    }
}
