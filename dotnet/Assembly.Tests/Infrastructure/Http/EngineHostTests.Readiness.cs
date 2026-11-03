using System.Net;
using System.Net.Sockets;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Http;

public sealed partial class EngineHostTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("remote")]
    public async Task Failed_listener_bind_leaves_recovered_work_unclaimed(string host)
    {
        var home = Directory.CreateTempSubdirectory("assembly-engine-bind-").FullName;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            using (var seed = new EngineFixture())
            {
                var accepted = seed.Apply(new StartSession("codex", null, home, "Pending", null, null, null), "pending");
                Assert.True(accepted.Applied, accepted.Error);
                seed.BackupTo(Path.Combine(home, "assembly.db"));
            }
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var effects = new HostEffects { Block = true };
            await Assert.ThrowsAnyAsync<IOException>(() => Host.Start(host, home, effects, port: port));
            // Failed startup releases both the home's owner lock and its database resources.
            using (HomeLock.Take(home)) { }
            using var db = TestDatabase.Open(Path.Combine(home, "assembly.db"));
            using var snapshot = db.OpenSnapshot(default);
            var pending = Assert.Single(snapshot.Query(
                "SELECT state,attempt FROM engine_outbox WHERE session_id=$session AND kind='start'",
                row => (State: row.Text(0), Attempt: row.Long(1)), new SqlArg("$session", "pending")));
            Assert.Equal(("pending", 0L), pending);
            Assert.False(effects.Entered.Task.IsCompleted);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(home, true);
        }
    }
}
