using Assembly.Application.Common.Calls;
using Assembly.Application.Common;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitCheckoutScopeTests
{
    [Fact]
    public async Task Remote_checkout_reads_keep_original_owner_and_source()
    {
        await using (var f = await Fixture.Start(remote: true))
        {
            using var client = await f.Open();
            var choices = await f.Call(client, "checkouts", f.Args());
            Assert.Equal(f.Root, choices.GetProperty("result").GetProperty("sessionRoot").GetString());
            Assert.Equal(f.Linked, Assert.Single(choices.GetProperty("result").GetProperty("worktrees").EnumerateArray()).GetProperty("path").GetString());
            Assert.Equal("result", (await f.Call(client, "status", f.Args(selected: true))).GetProperty("t").GetString());
            AssertCode(await f.Call(client, "status", new { session = f.Session, checkoutPath = f.Linked, expectedCheckoutRoot = f.Linked, expectedSource = f.Source }), "checkout_root_changed");
            AssertCode(await f.Call(client, "status", f.Args(checkout: Path.Combine(f.Home, "absent"))), "checkout_unavailable");
            AssertCode(await f.Call(client, "status", new { session = f.Session, checkoutPath = f.Linked, expectedCheckoutRoot = f.Root }), "bad_args");
            f.Git.Reset();
            AssertCode(await f.Call(client, "checkouts", new { session = f.Session, expectedSource = f.Source with { Generation = f.Source.Generation + 1 } }), "source_changed");
            Assert.Equal(0, f.Git.Count);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Git.BeforeRead = (_, args) => { if (args[0] != "status") return Task.CompletedTask; entered.TrySetResult(); return release.Task; };
            var pending = f.Call(client, "status", f.Args(selected: true));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await f.Hosts.DisconnectMirror();
                AssertCode(await pending, "source_changed");
            }
            finally { release.TrySetResult(); }
            f.Git.BeforeRead = null;
            // Reuse the actual replacement/reconnect/caught-up path: a different authenticated Engine now
            // occupies the same saved endpoint, with a genuine local session in the same repository.
            var replacement = new TwoHosts.Host(windowCalls: true);
            var replacementSession = replacement.Sessions.Create("fixture", "", workingFolder: f.Root);
            await using var replacementWork = new BackgroundWork(TimeProvider.System, TextWriter.Null);
            await using var replacementOperations = new GitOperations(replacementWork, replacement.Topics, TimeProvider.System, replacement.Fixture.Machine);
            replacement.AdditionalFeatures = calls => new SourceControlFeature(replacement.Sessions, replacement.Fixture.Db,
                replacementOperations, null, null, f.Git).Register(calls);
            await f.Hosts.ReplaceAtSameEndpoint(replacement);
            await f.Hosts.ReconnectSaved();
            await f.Hosts.Reached(replacement.Through);
            var current = f.Hosts.Mac.Fixture.Db.QueryFirst(
                "SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
                row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
            Assert.NotEqual(f.Source.Machine, current.Machine);
            Assert.Equal(f.Source.RouteMachine, current.RouteMachine);
            f.Git.Reset();
            AssertCode(await f.Call(client, "checkouts", f.Args()), "source_changed");
            AssertCode(await f.Call(client, "status", f.Args(selected: true)), "source_changed");
            Assert.Equal(0, f.Git.Count); // Old source cannot silently retarget even this identical repository path.
            f.Hosts.Mac.Sessions.Add(replacementSession.SessionId, "fixture", "", "workbox-test");
            var fresh = await f.Call(client, "checkouts", new { session = replacementSession.SessionId, expectedSource = current });
            Assert.Equal(f.Root, fresh.GetProperty("result").GetProperty("sessionRoot").GetString());
            Assert.Equal(2, f.Git.Count); // The new authenticated owner actually has the registered producer.
        }
        // Authenticated real old-owner dispatcher lacks the registration, not a fabricated exception.
        await using (var old = await Fixture.Start(remote: true, oldOwner: true))
        {
            using var client = await old.Open();
            AssertCode(await old.Call(client, "checkouts", old.Args()), "not_found");
            Assert.Equal(0, old.Git.Count);
        }
    }
}
