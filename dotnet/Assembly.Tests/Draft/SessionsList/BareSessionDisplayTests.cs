using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Remote;
using static Assembly.Application.Features.Chat.Sessions.SessionsFeature;

namespace Assembly.Tests.Draft.SessionsList;

public sealed class BareSessionDisplayTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Owning_folder_and_project_classify_both_session_payloads_and_rail(string provider)
    {
        using var f = new EngineFixture();
        var home = Directory.CreateTempSubdirectory("assembly-bare-display-");
        try
        {
            var sessions = SessionStore.Open(f.Db, f.Time, Path.Combine(home.FullName, "checkouts"));
            var cutover = new SessionCutover(f.Db, sessions, new FakeLauncher(), Path.Combine(home.FullName, "checkouts"), f.Time,
                owningHome: home.FullName);
            f.Db.Writer.Write("test.schema", scope => { cutover.EnsureSchema(scope); return 0; });
            var commands = new EngineCommands(f.Machine, f.Limits, projectStart: cutover.ProjectStart, projectProjection: sessions);
            cutover.Attach(commands, f.Machine);
            var bare = Path.Combine(home.FullName, "assembly-bare");
            var first = f.Db.Writer.Write("test.create", scope => sessions.Create(scope, provider, "bare1", bare, null, null));
            var second = f.Db.Writer.Write("test.create", scope => sessions.Create(scope, provider, "bare2", bare, null, null));
            Assert.True(sessions.Item(sessions.Get(first.SessionId)!).IsBare);
            Assert.True(sessions.FindByClient("bare1", default, f.Machine).Facts!.IsBare);
            // A pre-project_id repository remains a repository, even when its basename is assembly-bare.
            f.SeedSession("legacy", provider: provider);
            f.SeedSession("named", provider: provider);
            f.Db.Writer.Write("test.locations", scope =>
            {
                scope.Execute("UPDATE engine_session SET folder=$folder WHERE id='legacy'", new SqlArg("$folder", Path.Combine(home.FullName, "repo", "assembly-bare")));
                scope.Execute("UPDATE engine_session SET folder=$folder,project_id='explicit' WHERE id='named'", new SqlArg("$folder", bare));
                return 0;
            });
            var rows = sessions.Rail(new RailArgs(GroupBy: new(false, true))).Sessions;
            var group = Assert.Single(rows, row => row.Kind == "project" && row.Group == "No project");
            Assert.Equal(2, group.Count);
            Assert.Equal(new[] { first.SessionId, second.SessionId }.Order().ToArray(), rows.Where(row => row.Kind == "session" && row.Group == "No project")
                .Select(row => row.SessionId).Order().ToArray());
            Assert.All(rows.Where(row => row.SessionId is "legacy" or "named"), row => Assert.Equal("assembly-bare", row.Group));
            Assert.Equal(2, sessions.Rail(new RailArgs(GroupBy: new(false, false), Filter: "No project")).Sessions.Count);
            var folded = sessions.Rail(new RailArgs(GroupBy: new(false, true), Folded: ["::" + group.RepoUrl])).Sessions;
            Assert.DoesNotContain(folded, row => row.Kind == "session" && row.Group == "No project");
            Assert.Equal(2, Assert.Single(folded, row => row.Group == "No project").Count);
            var displaySql = "SELECT " + DisplayQueries.Json("session") + " FROM engine_session t WHERE id=$id";
            foreach (var id in new[] { first.SessionId, "legacy", "named" })
            {
                using var display = JsonDocument.Parse(f.Text(displaySql, new SqlArg("$id", id))!);
                Assert.Equal(id == first.SessionId, display.RootElement.GetProperty("isBare").GetBoolean());
            }
            f.Reopen();
            var reopened = SessionStore.Open(f.Db, f.Time, Path.Combine(home.FullName, "checkouts"));
            Assert.True(reopened.Item(reopened.Get(first.SessionId)!).IsBare);
        }
        finally { home.Delete(true); }
    }

    [Fact]
    public void Unknown_owning_home_is_false_instead_of_a_null_correlation_field()
    {
        using var f = new EngineFixture();
        var sessions = SessionStore.Open(f.Db, f.Time, "/isolated-checkouts");
        f.SeedSession();
        sessions.Add("s", "codex", "/test", null!);
        f.Db.Writer.Write("test.client", scope => scope.Execute("UPDATE session_info SET client_id='legacy-client' WHERE session_id='s'"));
        Assert.False(sessions.FindByClient("legacy-client", default, f.Machine).Facts!.IsBare);
    }

    [Fact]
    public void Copied_create_reply_immediately_classifies_a_different_owner_home()
    {
        using var f = new EngineFixture();
        var sessions = SessionStore.Open(f.Db, f.Time, "/isolated-checkouts");
        var facts = new CreatedSessionFacts("remote-owner", "codex", null, "/different/home/assembly-bare", false,
            "/different/home/assembly-bare", IsBare: true);
        var accepted = sessions.AcceptCopiedCreation("remote-bare", facts, "Workbox Test", null, _ => { });
        Assert.True(accepted.IsBare);
        Assert.True(sessions.Item(sessions.Get(accepted.SessionId)!).IsBare);
        Assert.Null(sessions.Item(accepted).Root);
        sessions.AcceptCopiedCreation("older-copy", facts with { IsBare = false }, "Workbox Test", null, _ => { });
        Assert.True(sessions.AcceptCopiedCreation("older-copy", facts, "Workbox Test", null, _ => { }).IsBare);
        Assert.True(sessions.Item(sessions.Get("older-copy")!).IsBare);
    }

    [Fact]
    public async Task Mirrored_bare_sessions_keep_owner_classification_and_one_group_per_machine_without_facades()
    {
        var local = Directory.CreateTempSubdirectory("assembly-bare-local-");
        var remote = Directory.CreateTempSubdirectory("assembly-bare-remote-");
        try
        {
            await using var hosts = new TwoHosts(forwardCommands: true, machineLauncher: new FakeLauncher(), checkouts: local.FullName,
                owningHome: local.FullName, remoteOwningHome: remote.FullName);
            await hosts.Start();
            foreach (var host in new[] { hosts.Mac, hosts.Remote })
                for (var i = 0; i < 2; i++)
                {
                    var home = host == hosts.Mac ? local.FullName : remote.FullName;
                    host.Fixture.Db.Writer.Write("test.bare", scope => host.Sessions.Create(scope, i == 0 ? "claude" : "codex",
                        "bare" + i, Path.Combine(home, "assembly-bare"), null, null));
                }
            await hosts.StartMirror();
            await hosts.Reached(hosts.Remote.Through);
            var rail = hosts.Mac.Sessions.Rail(new RailArgs(GroupBy: new(false, true)));
            var groups = rail.Sessions.Where(row => row.Kind == "project" && row.Group == "No project").ToArray();
            Assert.Equal(2, groups.Length);
            Assert.All(groups, group => Assert.Equal(2, group.Count));
            Assert.Equal(2, groups.Select(group => group.RepoUrl).Distinct().Count());
            Assert.Equal(2, hosts.Mac.Fixture.Count("session_info"));
            Assert.Equal(4, rail.Sessions.Count(row => row.Kind == "session" && row.Group == "No project"));
            Assert.Equal("test", Assert.Single(rail.Sessions, row => row.SessionId == "s").Group);
            var facts = hosts.Remote.Sessions.FindByClient("bare0", default, hosts.Remote.Runtime.Machine);
            var copied = hosts.Mac.Sessions.AcceptCopiedCreation(facts.SessionId!, facts.Facts! with { IsBare = false },
                "workbox-test", null, _ => { });
            Assert.False(copied.IsBare);
            await hosts.DisconnectMirror();
            var generation = hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE state='live'");
            hosts.Mac.Fixture.Db.Writer.Write("test.old-display", scope => scope.Execute(
                "UPDATE engine_mirror_row SET row_json=json_remove(row_json,'$.isBare') WHERE entity_kind='session'"));
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = hosts.Mac.Topics.Subscribe("__engine_mirrored", null, _ => { }, (_, _) =>
            {
                if (hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE state='live'") > generation)
                    refreshed.TrySetResult();
            });
            try
            {
                await hosts.ReconnectSaved();
                await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { hosts.Mac.Topics.Unsubscribe(subscription); }
            Assert.True(hosts.Mac.Sessions.Item(hosts.Mac.Sessions.Get(copied.SessionId)!).IsBare);
            Assert.True(hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE state='live'") > generation);
            var after = hosts.Mac.Sessions.Rail(new RailArgs(GroupBy: new(false, true))).Sessions;
            // The deliberately unproven old facade remains separate from source-qualified canonical rows.
            Assert.Equal(4, after.Count(row => row.Kind == "session" && row.Group == "No project" && row.Source is not null));
            Assert.Equal(2, after.Count(row => row.Kind == "project" && row.Group == "No project"));
        }
        finally { local.Delete(true); remote.Delete(true); }
    }
}
