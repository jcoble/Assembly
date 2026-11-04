using System.Text.Json;
using System.Threading.Channels;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mirror;
using Assembly.Engine.Providers;
using Assembly.Engine.Reading;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthReadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("provider", "codex/")]
    [InlineData("sign_in", "operation")]
    public async Task Auth_entity_reads_exact_target_and_safe_state_without_challenge_fields(string kind, string id)
    {
        await using var f = new AuthRuntimeFixture(); f.Begin();
        var entity = f.Runtime.Reader.Entity(kind, id, default)!.Value;
        Assert.Equal(kind, entity.GetProperty("entityKind").GetString()); Assert.Equal(id, entity.GetProperty("id").GetString());
        Assert.Equal("codex", entity.GetProperty("provider").GetString()); Assert.Equal("", entity.GetProperty("accountContext").GetString());
        Assert.False(entity.TryGetProperty("challenge", out _)); Assert.False(entity.TryGetProperty("code", out _)); Assert.False(entity.TryGetProperty("url", out _));
        if (kind == "provider") Assert.Equal(JsonValueKind.Null, entity.GetProperty("factsJson").ValueKind);
        else { Assert.Equal(301000, entity.GetProperty("deadlineMs").GetInt64()); Assert.Equal(JsonValueKind.Null, entity.GetProperty("originNeedId").ValueKind); }
    }

    [Fact]
    public async Task Provider_detail_retains_exact_safe_json_and_full_embedded_nul_strings()
    {
        await using var f = new AuthRuntimeFixture(); var raw = AuthRuntimeFixture.Facts("full\0model"); Store(f, raw);
        var entity = f.Runtime.Reader.Entity("provider", "codex/", default)!.Value;
        Assert.Equal(raw, entity.GetProperty("factsJson").GetString()); Assert.Equal(1000, entity.GetProperty("checkedMs").GetInt64());
        Assert.Equal("unverified", entity.GetProperty("authState").GetString());
    }

    [Fact]
    public async Task Oversize_safe_entity_refuses_existing_display_budget_without_changing_or_truncating_storage()
    {
        await using var f = new AuthRuntimeFixture(); var raw = AuthRuntimeFixture.Facts(new string('x', 50_000)); Store(f, raw);
        var error = Assert.Throws<InvalidOperationException>(() => f.Runtime.Reader.Entity("provider", "codex/", default));
        Assert.Contains("32 KiB", error.Message); Assert.Equal(raw, f.Core.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
    }

    [Fact]
    public async Task Providers_snapshot_contains_both_kinds_on_existing_global_cursor()
    {
        await using var f = new AuthRuntimeFixture(); f.Begin();
        using var snapshot = f.Runtime.Reader.Snapshot("providers", null, default); var page = snapshot.ReadNext();
        Assert.Equal(new[] { "provider", "sign_in" }, page.Items.Select(x => x.GetProperty("entityKind").GetString()));
        Assert.Equal(f.Runtime.Reader.HighWatermark(default), page.Through); Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Actual_provider_socket_stream_replays_state_only_events_and_keeps_global_cursor_gaps()
    {
        await using var f = new AuthRuntimeFixture(); f.Probe(); var frames = Channel.CreateUnbounded<string>();
        var published = Channel.CreateUnbounded<long>();
        using var publication = f.Runtime.Publisher.Register(e => published.Writer.TryWrite(e.Seq), out var cut);
        var high = f.Runtime.Reader.HighWatermark(default);
        while (cut < high) cut = await published.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        var subscription = new EngineSocketStream(f.Runtime).Subscribe(7, "providers", null, 0, frame => frames.Writer.TryWrite(frame), default);
        try
        {
            using var first = JsonDocument.Parse(await frames.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
            var root = first.RootElement; Assert.Equal("engine_events", root.GetProperty("t").GetString());
            Assert.Equal(f.Runtime.Reader.HighWatermark(default), root.GetProperty("through").GetInt64());
            Assert.Equal("provider.changed", Assert.Single(root.GetProperty("events").EnumerateArray()).GetProperty("kind").GetString());
            Assert.DoesNotContain("factsJson", root.GetRawText(), StringComparison.Ordinal);
            var before = f.Runtime.Reader.HighWatermark(default);
            Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
            using var stop = new CancellationTokenSource(Timeout); var found = false;
            while (!found)
            {
                using var next = JsonDocument.Parse(await frames.Reader.ReadAsync(stop.Token));
                found = next.RootElement.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "sign_in.changed");
                if (found) Assert.True(next.RootElement.GetProperty("through").GetInt64() > before);
            }
        }
        finally { subscription.Dispose(); await subscription.Completion.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task Auth_mirror_rows_remain_display_only_and_use_existing_entity_and_snapshot_paths()
    {
        await using var remote = new AuthRuntimeFixture(); remote.Begin(); using var local = new EngineFixture();
        using var snapshot = remote.Runtime.Reader.Snapshot("mirror", null, default); var page = snapshot.ReadNext();
        Assert.Contains(page.Items, row => row.GetProperty("entityKind").GetString() == "provider");
        local.Db.Writer.Write("test.auth.mirror", scope =>
        {
            MirrorApply.Open(scope, remote.Runtime.Machine, "workbox-test", 1000); MirrorApply.BeginSnapshot(scope, remote.Runtime.Machine, 1000);
            MirrorApply.SnapshotPage(scope, remote.Runtime.Machine, page.Items);
            return MirrorApply.EndSnapshot(scope, remote.Runtime.Machine, page.Through, page.At, 1000);
        });
        var reader = new EngineReader(local.Db, local.Machine);
        Assert.Equal("starting", reader.Entity("sign_in", "operation", default, remote.Runtime.Machine)!.Value.GetProperty("state").GetString());
        using var mirrored = reader.Snapshot("providers", null, default, remote.Runtime.Machine);
        Assert.Equal(2, mirrored.ReadNext().Items.Count); Assert.Equal(0, local.Count("engine_provider")); Assert.Equal(0, local.Count("engine_sign_in")); Assert.Equal(0, local.Count("engine_outbox"));
    }

    [Fact]
    public async Task Rejected_raw_challenge_reason_never_appears_in_auth_entity_or_events()
    {
        await using var f = new AuthRuntimeFixture(); var binding = f.Begin(); const string secret = "https://secret.invalid/?code=CANARY";
        Assert.False(f.Apply(new SignInProgress("waiting_for_person", secret, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        var entity = f.Runtime.Reader.Entity("sign_in", "operation", default)!.Value;
        Assert.DoesNotContain(secret, entity.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join("", f.Core.Events().Select(e => JsonSerializer.Serialize(e, EngineJson.Default.EngineEvent))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Additive_provider_topic_does_not_relabel_or_remove_existing_topics()
    {
        await using var f = new AuthRuntimeFixture(); f.Begin();
        foreach (var topic in new[] { "runs", "sessions", "needs_me", "messages", "prs", "usage" })
        {
            using var snapshot = f.Runtime.Reader.Snapshot(topic, null, default);
            var items = snapshot.ReadNext().Items;
            if (topic == "runs")
                Assert.Equal("{\"entityKind\":\"workflow_settings\",\"id\":\"workflow-settings\",\"version\":0,\"mutedUntil\":null}", Assert.Single(items).GetRawText());
            else Assert.Empty(items);
            var page = f.Runtime.Reader.Since(topic, null, 0, default); Assert.Empty(page.Events); Assert.Equal(f.Runtime.Reader.HighWatermark(default), page.Through);
        }
        var providers = f.Runtime.Reader.Since("providers", null, 0, default); Assert.Equal(2, providers.Events.Count);
    }

    private static void Store(AuthRuntimeFixture f, string raw)
    {
        var probe = f.Probe(); using var json = JsonDocument.Parse(raw);
        var prepared = EngineCommands.PrepareProviderCheck(new("installed", "unverified", json.RootElement, null, probe));
        Assert.True(((IEngineRuntime)f.Runtime).Write(scope => f.Runtime.Commands.ApplyProviderCheck(scope,
            new(Guid.NewGuid(), f.Runtime.Machine, new(ActorKind.System, "provider.effects"), "codex/", 1, 0, 1000), prepared)).Applied);
    }
}
