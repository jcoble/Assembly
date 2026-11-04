using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AgentsFileTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("assembly-agents-file-").FullName;
    private string FilePath => Path.Combine(_home, "agents.json");
    private readonly Topics _topics = new();
    private const string Original = """
        [{"id":"codex","name":"Codex","vendorFamily":"codex","command":"node","args":["adapter.js"],
          "cli":"/fixture/codex","env":{"CODEX_HOME":"/fixture/old","UNRELATED":"keep"},"extension":{"keep":true}},
         {"id":"other","name":"Other","command":"custom-adapter","args":["--unchanged"],"env":{"PRIVATE":"retained"}}]
        """;

    private AgentsFile Open()
    {
        File.WriteAllText(FilePath, Original);
        return Bound(FilePath);
    }

    private AgentsFile Bound(string path)
    {
        var source = new AgentsFile(path);
        source.BindOwner("engine-owner-7", _topics);
        return source;
    }

    [Fact]
    public void Unbound_bootstrap_catalogue_is_readable_but_owner_operations_refuse_before_file_access()
    {
        File.WriteAllText(FilePath, Original);
        var source = new AgentsFile(FilePath);
        var revision = source.Revision;
        File.Delete(FilePath);

        Assert.Equal(["codex", "other"], source.Select(agent => agent.Id));
        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.Read("codex")).Reason);
        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.Save("codex", revision, new(null, null, null, []))).Reason);
        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.Reload()).Reason);
        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.Current()).Reason);
        Assert.False(File.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void Binding_is_immutable_except_an_identical_machine_and_same_topic_instance_repeat()
    {
        File.WriteAllText(FilePath, Original);
        var source = new AgentsFile(FilePath);
        var changes = new List<string>();
        using var subscription = _topics.Subscribe(AgentsFile.Topic, "engine-owner-7", _ => { }, (_, json) => changes.Add(json));
        source.BindOwner("engine-owner-7", _topics);
        source.BindOwner("engine-owner-7", _topics);

        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.BindOwner("another-engine", _topics)).Reason);
        Assert.Equal("configuration_unavailable", Assert.Throws<CallFailed>(() => source.BindOwner("engine-owner-7", new Topics())).Reason);
        Assert.Equal("engine-owner-7", source.Current().Machine);
        Assert.Empty(changes);
        Assert.Equal(Original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Bound_topic_snapshot_never_takes_a_file_lock_or_reads_the_file()
    {
        var source = Open();
        var revision = source.Revision;
        File.Delete(FilePath);
        using var held = new FileStream(source.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(new ProviderRuntimeChanged("engine-owner-7", revision), source.Current());
    }

    [Fact]
    public void Home_save_atomically_replaces_the_actual_file_preserves_unrelated_configuration_and_updates_the_same_launcher()
    {
        var source = Open();
        var launcher = new AdapterLauncher(source, _home, new UnclaimedPermissions());
        var before = source.Read("codex");
        var capture = ConfigTargetIdentity.Capture(launcher, "codex");
        var published = new List<string>();
        using var subscription = _topics.Subscribe(AgentsFile.Topic, "engine-owner-7", _ => { }, (_, json) =>
        {
            Assert.Equal("/fixture/new", launcher.Agents.Single(agent => agent.Id == "codex").Env!["CODEX_HOME"]);
            Assert.Equal("/fixture/new", JsonNode.Parse(File.ReadAllText(FilePath))![0]!["env"]!["CODEX_HOME"]!.GetValue<string>());
            published.Add(json);
        });

        var after = source.Save("codex", before.Revision, before.Settings with { ConfigHome = "/fixture/new" });

        Assert.Equal("engine-owner-7", after.Machine);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.NotEqual(before.ConfigurationTargetId, after.ConfigurationTargetId);
        Assert.NotEqual(before.AccountContext, after.AccountContext);
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.Validate(launcher, capture.Descriptor, capture.Environment));
        Assert.Equal("/fixture/old", capture.Environment["CODEX_HOME"]);
        var actual = JsonNode.Parse(File.ReadAllText(FilePath))!.AsArray();
        Assert.Equal("node", actual[0]!["command"]!.GetValue<string>());
        Assert.Equal("adapter.js", actual[0]!["args"]![0]!.GetValue<string>());
        Assert.Equal("keep", actual[0]!["env"]!["UNRELATED"]!.GetValue<string>());
        Assert.True(actual[0]!["extension"]!["keep"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Original)![1], actual[1]));
        var change = Assert.Single(published);
        var safe = JsonSerializer.Deserialize(change, ProviderRuntimeJson.Default.ProviderRuntimeChanged)!;
        Assert.Equal(after.Revision, safe.Revision);
        Assert.DoesNotContain("/fixture", change);
        Assert.DoesNotContain("UNRELATED", change);
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
        Assert.True(File.Exists(source.LockPath));
    }

    [Fact]
    public void Exact_source_revision_CAS_refuses_a_second_writer_without_losing_the_first_save()
    {
        var first = Open();
        var second = Bound(FilePath);
        var read = first.Read("codex");
        var accepted = first.Save("codex", read.Revision, read.Settings with { ConfigHome = "/fixture/first" });
        var committed = File.ReadAllBytes(FilePath);

        var refusal = Assert.Throws<CallFailed>(() => second.Save("codex", read.Revision,
            read.Settings with { ConfigHome = "/fixture/second" }));

        Assert.Equal("runtime_settings_conflict", refusal.Reason);
        Assert.Equal(committed, File.ReadAllBytes(FilePath));
        Assert.Equal(accepted.Revision, second.Reload());
        Assert.Equal("/fixture/first", second.Read("codex").Settings.ConfigHome);
    }

    [Fact]
    public void Permanent_descriptor_sidecar_refuses_a_competing_OS_lock_then_allows_read_after_release()
    {
        var source = Open();
        using (new FileStream(source.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = Assert.Throws<CallFailed>(() => source.Read("codex"));
            Assert.Equal("runtime_settings_busy", refused.Reason);
            Assert.Equal(Original, File.ReadAllText(FilePath));
        }
        Assert.Equal("/fixture/old", source.Read("codex").Settings.ConfigHome);
    }

    [Theory]
    [InlineData("binary", "runtime_binary_unsupported")]
    [InlineData("arguments", "runtime_arguments_unsupported")]
    [InlineData("shadow", "runtime_shadow_unsupported")]
    public void Unsupported_runtime_changes_never_publish_or_mutate_the_descriptor(string field, string reason)
    {
        var source = Open();
        var before = source.Read("codex");
        var settings = field switch
        {
            "binary" => before.Settings with { BinaryPath = "" },
            "arguments" => before.Settings with { LaunchArguments = ["--new-runtime-argument"] },
            _ => before.Settings with { ShadowHome = "/fixture/shadow" }
        };
        var changes = new List<string>();
        using var subscription = _topics.Subscribe(AgentsFile.Topic, "engine-owner-7", _ => { }, (_, json) => changes.Add(json));

        var refusal = Assert.Throws<CallFailed>(() => source.Save("codex", before.Revision, settings));

        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(Original, File.ReadAllText(FilePath));
        Assert.Equal(before.Revision, source.Revision);
        Assert.Empty(changes);
    }

    [Fact]
    public void Unknown_family_binary_changes_refuse_but_existing_metadata_can_roundtrip()
    {
        Open();
        var document = JsonNode.Parse(File.ReadAllText(FilePath))!.AsArray();
        document[0]!["vendorFamily"] = "custom-family";
        File.WriteAllText(FilePath, document.ToJsonString());
        var source = Bound(FilePath);
        var before = source.Read("codex");
        var bytes = File.ReadAllBytes(FilePath);

        Assert.Equal("runtime_binary_unsupported", Assert.Throws<CallFailed>(() => source.Save("codex", before.Revision,
            before.Settings with { BinaryPath = "/fixture/different" })).Reason);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal(before.Settings.BinaryPath, source.Save("codex", before.Revision, before.Settings).Settings.BinaryPath);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void Known_family_binary_save_and_removal_preserve_C3_account_and_ACP_identity_but_refuse_old_capture(string family)
    {
        Open();
        var document = JsonNode.Parse(File.ReadAllText(FilePath))!.AsArray();
        document[0]!["vendorFamily"] = family;
        document[0]!["env"]!["CODEX_PATH"] = "/fixture/explicit-codex";
        document[0]!["env"]!["CLAUDE_CODE_EXECUTABLE"] = "/fixture/explicit-claude";
        File.WriteAllText(FilePath, document.ToJsonString());
        var source = Bound(FilePath);
        var launcher = new AdapterLauncher(source, _home, new UnclaimedPermissions());
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        var before = source.Read("codex");

        var saved = source.Save("codex", before.Revision, before.Settings with { BinaryPath = "/fixture/new binary" });

        Assert.Equal("/fixture/new binary", saved.Settings.BinaryPath);
        Assert.Equal(before.ConfigurationTargetId, saved.ConfigurationTargetId);
        Assert.Equal(before.AccountContext, saved.AccountContext);
        Assert.Equal("node", source[0].Command);
        Assert.Equal(["adapter.js"], source[0].Args);
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor, captured.Environment));
        var removed = source.Save("codex", saved.Revision, saved.Settings with { BinaryPath = null });
        Assert.Null(removed.Settings.BinaryPath);
        Assert.False(JsonNode.Parse(File.ReadAllText(FilePath))![0]!.AsObject().ContainsKey("cli"));
        Assert.Equal("/fixture/explicit-codex", source[0].Env!["CODEX_PATH"]);
        Assert.Equal("/fixture/explicit-claude", source[0].Env!["CLAUDE_CODE_EXECUTABLE"]);
    }

    [Fact]
    public void Existing_CLI_arguments_and_shadow_metadata_roundtrip_without_becoming_ACP_arguments()
    {
        Open();
        var document = JsonNode.Parse(File.ReadAllText(FilePath))!.AsArray();
        document[0]!["cliArgs"] = new JsonArray("--saved-cli-argument");
        document[0]!["shadowHome"] = "/fixture/existing-shadow";
        File.WriteAllText(FilePath, document.ToJsonString());
        var source = Bound(FilePath);
        var before = source.Read("codex");

        var after = source.Save("codex", before.Revision, before.Settings with { ConfigHome = "/fixture/new" });

        Assert.Equal(["--saved-cli-argument"], after.Settings.LaunchArguments);
        Assert.Equal("/fixture/existing-shadow", after.Settings.ShadowHome);
        Assert.Equal(["adapter.js"], source.Single(agent => agent.Id == "codex").Args);
        Assert.Equal(["--saved-cli-argument"], source.Single(agent => agent.Id == "codex").CliArgs);
    }

    [Fact]
    public void Explicit_empty_home_and_removed_override_are_distinct_persisted_settings()
    {
        var source = Open();
        var before = source.Read("codex");
        var launcher = new AdapterLauncher(source, _home, new UnclaimedPermissions());
        var captured = ConfigTargetIdentity.Capture(launcher, "codex");
        var empty = source.Save("codex", before.Revision, before.Settings with { ConfigHome = "" });
        Assert.Equal("", source.Single(agent => agent.Id == "codex").Env!["CODEX_HOME"]);
        Assert.Equal("", empty.AccountContext);

        var removed = source.Save("codex", empty.Revision, empty.Settings with { ConfigHome = null });

        Assert.Null(removed.Settings.ConfigHome);
        Assert.False(source.Single(agent => agent.Id == "codex").Env!.ContainsKey("CODEX_HOME"));
        Assert.Equal("keep", source.Single(agent => agent.Id == "codex").Env!["UNRELATED"]);
        Assert.Throws<CallFailed>(() => ConfigTargetIdentity.RequireSameConfiguration(launcher,
            captured.Descriptor with { Env = captured.Environment }, captured.Environment));
    }

    [Fact]
    public void Cancelled_save_preserves_bytes_and_cannot_announce_a_new_catalogue()
    {
        var source = Open();
        var before = source.Read("codex");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => source.Save("codex", before.Revision,
            before.Settings with { ConfigHome = "/fixture/not-saved" }, cancelled.Token));

        Assert.Equal(Original, File.ReadAllText(FilePath));
        Assert.Equal(before.Revision, source.Revision);
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void Physical_parent_aliases_share_one_permanent_descriptor_lock()
    {
        var source = Open();
        var alias = Path.Combine(_home, "alias");
        Directory.CreateSymbolicLink(alias, _home);
        var other = Bound(Path.Combine(alias, "agents.json"));

        Assert.Equal(source.Path, other.Path);
        Assert.Equal(source.LockPath, other.LockPath);
        using var held = new FileStream(source.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("runtime_settings_busy", Assert.Throws<CallFailed>(() => other.Reload()).Reason);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
