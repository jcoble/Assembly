using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Agents;

namespace Assembly.Tests.Features.Agents;

[CollectionDefinition("Agent catalogue environment", DisableParallelization = true)]
public sealed class AgentCatalogueEnvironmentCollection;

[Collection("Agent catalogue environment")]
public sealed class AgentCatalogueTests
{
    private static readonly AgentDescriptor Agent = new("fixture", "Fixture provider", "fixture-adapter", [], null);

    [Fact]
    public void A_successful_readiness_check_does_not_verify_login()
    {
        var facts = Classify(new(Initialize(), Session(), null, false, null));
        Assert.Equal(Installation.Installed, facts.Installation);
        Assert.Equal(AuthState.Unverified, facts.Auth);
        Assert.Null(facts.Problem);
        Assert.Equal("fixture-acp", facts.Adapter);
        Assert.Equal("2.0", facts.Version);
        Assert.Equal(123, facts.CheckedMs);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("api_key")]
    [InlineData("gateway")]
    [InlineData("external")]
    [InlineData("signed_in")]
    public void An_identity_category_or_unrecognized_status_is_not_verified_login(string kind)
    {
        var facts = Classify(new(Initialize(), Session(), new(kind, "Signed in"), false, null));
        Assert.Equal(AuthState.Unverified, facts.Auth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("account")]
    [InlineData("signed_in")]
    public void No_readiness_result_and_no_negative_auth_evidence_is_unknown(string? kind)
    {
        var facts = Classify(new(Initialize(), null, kind is null ? null : new(kind, "Signed in"), false, null));
        Assert.Equal(AuthState.Unknown, facts.Auth);
        Assert.Empty(facts.Methods);
        Assert.Empty(facts.Models);
    }

    [Fact]
    public void Explicit_none_is_negative_even_when_readiness_succeeds()
    {
        var facts = Classify(new(Initialize(), Session(), new("none", "Signed in"), false, null));
        Assert.Equal(AuthState.NeedsSignIn, facts.Auth);
    }

    [Fact]
    public void Actual_auth_required_wins_over_identity_readiness_and_another_problem()
    {
        var facts = Classify(new(Initialize(), Session(), new("account", "Signed in"), true, "network"));
        Assert.Equal(AuthState.NeedsSignIn, facts.Auth);
        Assert.Equal(ProviderProblem.network, facts.Problem);
    }

    [Fact]
    public void Safe_facts_keep_models_and_method_choices_without_private_fields()
    {
        var init = Initialize() with
        {
            AuthMethods = [new("browser", "Browser", "terminal", null,
            ["SECRET-ARGV"], [new("TOKEN", "SECRET-ENV")], Json("""{"private":"SECRET-META"}"""))]
        };
        var facts = Classify(new(init, Session("""
            [{"id":"model","type":"select","category":"model","currentValue":"m1",
              "options":[{"value":"m1","name":"First model"},{"value":"m2","name":"Second model"}]}]
            """), null, false, null));
        Assert.Equal([new ModelChoice("m1", "First model"), new ModelChoice("m2", "Second model")], facts.Models);
        Assert.Equal(new SignInChoice("browser", "Browser", "terminal"), Assert.Single(facts.Methods));
        var json = JsonSerializer.Serialize(facts, ProvidersJson.Default.ProviderFacts);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("args", json);
        Assert.DoesNotContain("env", json);
        Assert.DoesNotContain("meta", json);
    }

    [Fact]
    public void Null_version_keeps_identity_auth_models_and_real_empty_choices()
    {
        var facts = Classify(new(new(2, null, default, [], null), Session("""
            [{"id":"model","category":"model","type":"select","options":[{"value":"m","name":"Model"}]}]
            """), new("none", null), false, null));
        Assert.Equal(Agent.Id, facts.Adapter);
        Assert.Null(facts.Version);
        Assert.Equal(ProviderProblem.version_unknown, facts.Problem);
        Assert.Equal(AuthState.NeedsSignIn, facts.Auth);
        Assert.Equal(new ModelChoice("m", "Model"), Assert.Single(facts.Models));
        Assert.Empty(facts.Methods);
    }

    [Fact]
    public void Empty_model_and_method_lists_do_not_mean_unsupported_auth()
    {
        var facts = Classify(new(Initialize(), Session(), null, false, null));
        Assert.Empty(facts.Models);
        Assert.Empty(facts.Methods);
        Assert.Equal(AuthState.Unverified, facts.Auth);
    }

    [Theory]
    [InlineData("not_installed", Installation.NotInstalled, ProviderProblem.not_installed)]
    [InlineData("invalid_descriptor", Installation.Invalid, ProviderProblem.invalid_descriptor)]
    [InlineData("cannot_start", Installation.Installed, ProviderProblem.cannot_start)]
    [InlineData("timed_out", Installation.Installed, ProviderProblem.timed_out)]
    [InlineData("adapter_crashed", Installation.Installed, ProviderProblem.adapter_crashed)]
    [InlineData("network", Installation.Installed, ProviderProblem.network)]
    [InlineData("interrupted", Installation.Installed, ProviderProblem.interrupted)]
    [InlineData("adapter_error", Installation.Installed, ProviderProblem.adapter_error)]
    public void Known_failures_have_fixed_classification(string code, Installation installation, ProviderProblem problem)
    {
        var facts = Classify(new(null, null, null, false, code));
        Assert.Equal(installation, facts.Installation);
        Assert.Equal(AuthState.Unknown, facts.Auth);
        Assert.Equal(problem, facts.Problem);
    }

    [Theory]
    [InlineData("SECRET-CANARY https://example.invalid/?code=PRIVATE")]
    [InlineData("-32000")]
    [InlineData("timed_out: SECRET-CANARY")]
    [InlineData("1")]
    public void Unrecognized_problem_text_cannot_become_a_fact_or_fake_rpc_evidence(string code)
    {
        var facts = Classify(new(null, null, new("account", "SECRET-STATUS"), false, code));
        Assert.Equal(ProviderProblem.adapter_error, facts.Problem);
        Assert.Equal(AuthState.Unknown, facts.Auth);
        var json = JsonSerializer.Serialize(facts, ProvidersJson.Default.ProviderFacts);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("PRIVATE", json);
        Assert.DoesNotContain("https", json);
    }

    [Fact]
    public void A_stronger_failure_is_preserved_when_version_is_missing()
    {
        var facts = Classify(new(null, null, null, false, "cannot_start"));
        Assert.Equal(ProviderProblem.cannot_start, facts.Problem);
        Assert.Null(facts.Version);
    }

    [Theory]
    [InlineData("", "Provider", "adapter")]
    [InlineData("provider", " ", "adapter")]
    [InlineData("provider", "Provider", "")]
    public async Task Invalid_configuration_never_calls_the_probe(string id, string name, string command)
    {
        var port = new ProbePort((_, _, _, _) => throw new InvalidOperationException("Probe must not start"));
        var catalogue = new AgentCatalogue(port, "/unused-probe", Clock());
        var facts = await catalogue.CheckAsync(new(id, name, command, [], null), CancellationToken.None);
        Assert.Equal(0, port.Calls);
        Assert.Equal(Installation.Invalid, facts.Installation);
        Assert.Equal(ProviderProblem.invalid_descriptor, facts.Problem);
    }

    [Fact]
    public async Task Check_passes_the_exact_frozen_target_and_folder_without_creating_a_folder()
    {
        var folder = "/nonexistent-catalogue-fixture/probe";
        var time = Clock();
        CancellationToken observed = default;
        var port = new ProbePort((agent, target, actualFolder, ct) =>
        {
            Assert.Equal(folder, actualFolder);
            Assert.NotNull(agent.Env);
            Assert.Equal(ConfigTargetIdentity.Of(agent, agent.Env!), target);
            Assert.Equal(Agent.Id, agent.Id);
            Assert.True(ct.CanBeCanceled);
            observed = ct;
            return Task.FromResult(new ProviderProbeResult(Initialize(), Session(), null, false, null));
        });
        var facts = await new AgentCatalogue(port, folder, time).CheckAsync(Agent, CancellationToken.None);
        Assert.Equal(1, port.Calls);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), facts.CheckedMs);
        Assert.Equal(AuthState.Unverified, facts.Auth);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.False(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task Deadline_cancels_at_twenty_seconds_and_awaits_owned_probe_cleanup()
    {
        var time = Clock();
        var cleaning = Signal();
        var finishCleanup = Signal();
        var port = new ProbePort(async (_, _, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cleaning.SetResult(); await finishCleanup.Task; }
            return new(null, null, null, false, null);
        });
        var check = new AgentCatalogue(port, "/unused-probe", time).CheckAsync(Agent, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.False(cleaning.Task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await cleaning.Task;
        Assert.False(check.IsCompleted);
        finishCleanup.SetResult();
        var facts = await check;
        Assert.Equal(ProviderProblem.timed_out, facts.Problem);
        Assert.Equal(AuthState.Unknown, facts.Auth);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_awaits_cleanup_and_propagates_instead_of_returning_timeout()
    {
        var cleaning = Signal();
        var finishCleanup = Signal();
        using var caller = new CancellationTokenSource();
        var port = new ProbePort(async (_, _, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cleaning.SetResult(); await finishCleanup.Task; }
            return new(null, null, null, false, null);
        });
        var check = new AgentCatalogue(port, "/unused-probe", Clock()).CheckAsync(Agent, caller.Token);
        caller.Cancel();
        await cleaning.Task;
        Assert.False(check.IsCompleted);
        finishCleanup.SetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task An_already_cancelled_check_never_starts_work()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var port = new ProbePort((_, _, _, _) => throw new InvalidOperationException("Probe must not start"));
        var check = new AgentCatalogue(port, "/unused-probe", Clock()).CheckAsync(Agent, caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Captured_environment_ignores_a_late_ambient_key_while_raw_call_captures_it()
    {
        var key = "ASSEMBLY_CATALOGUE_FIXTURE_" + Guid.NewGuid().ToString("N");
        var captured = ConfigTargetIdentity.EnvironmentOf(Agent);
        Assert.False(captured.ContainsKey(key));
        var suppliedTarget = ConfigTargetIdentity.Of(Agent, captured);
        var targets = new List<string>();
        var port = new ProbePort((agent, target, _, _) =>
        {
            targets.Add(target);
            if (targets.Count == 1) Assert.False(agent.Env!.ContainsKey(key));
            else Assert.Equal("late-fixture-value", agent.Env![key]);
            return Task.FromResult(new ProviderProbeResult(Initialize(), Session(), null, false, null));
        });
        try
        {
            Environment.SetEnvironmentVariable(key, "late-fixture-value");
            var catalogue = new AgentCatalogue(port, "/unused-probe", Clock());
            await catalogue.CheckAsync(Agent, captured, CancellationToken.None);
            await catalogue.CheckAsync(Agent, CancellationToken.None);
            Assert.Equal(suppliedTarget, targets[0]);
            Assert.NotEqual(suppliedTarget, targets[1]);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task Probe_receives_owned_snapshots_of_the_actual_identity_collections()
    {
        List<string> arguments = ["original-argument"];
        List<string> air = ["original-air"];
        var environment = new Dictionary<string, string> { ["FIXTURE_ENV"] = "original-environment" };
        var descriptor = Agent with { Args = arguments, Air = air };
        var expectedTarget = ConfigTargetIdentity.Of(descriptor, environment);
        var entered = Signal();
        var release = Signal();
        var port = new ProbePort(async (agent, target, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            Assert.Equal(["original-argument"], agent.Args);
            Assert.Equal(["original-air"], agent.Air);
            Assert.Equal("original-environment", agent.Env!["FIXTURE_ENV"]);
            Assert.Equal(expectedTarget, target);
            Assert.Equal(ConfigTargetIdentity.Of(agent, agent.Env), target);
            return new(Initialize(), Session(), null, false, null);
        });
        var check = new AgentCatalogue(port, "/unused-probe", Clock())
            .CheckAsync(descriptor, environment, CancellationToken.None);
        await entered.Task;
        arguments[0] = "changed-argument";
        air[0] = "changed-air";
        environment["FIXTURE_ENV"] = "changed-environment";
        release.SetResult();
        Assert.Equal(AuthState.Unverified, (await check).Auth);
    }

    private static FakeTimeProvider Clock() => new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ProbePort(Func<AgentDescriptor, string, string, CancellationToken, Task<ProviderProbeResult>> probe) : IProviderAdapters
    {
        public int Calls { get; private set; }
        public Task<ProviderProbeResult> ProbeAsync(AgentDescriptor descriptor, string target, string folder, CancellationToken ct)
        {
            Calls++;
            return probe(descriptor, target, folder, ct);
        }
        public Task<ProviderAdapterCapture?> CaptureAsync(string provider, string target, CancellationToken ct) =>
            throw new InvalidOperationException("Catalogue must not capture an adapter");
        public Task RetireAsync(ProviderAdapterCapture capture, CancellationToken ct) =>
            throw new InvalidOperationException("Catalogue must not retire an adapter");
    }

    private static ProviderFacts Classify(ProviderProbeResult result) => ProviderFactsClassifier.Classify(Agent, result, 123);
    private static InitializeResponse Initialize() => new(2, new("fixture-acp", "2.0"), default, [], null);
    private static NewSessionResponse Session(string options = "[]") => new("probe-session", Json(options));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
