using System.Text.Json;
using Assembly.Application.Features.Agents;

namespace Assembly.Tests.Features.Agents;

public sealed class ProviderRuntimeSettingsTests
{
    [Fact]
    public void Owner_settings_preserve_unset_and_explicit_empty_values_without_inventing_defaults()
    {
        var unset = JsonSerializer.Deserialize("""
            {"binaryPath":null,"configHome":null,"shadowHome":null,"launchArguments":[]}
            """, ProviderRuntimeJson.Default.ProviderRuntimeSettings)!;
        var empty = JsonSerializer.Deserialize("""
            {"binaryPath":"","configHome":"","shadowHome":"","launchArguments":[]}
            """, ProviderRuntimeJson.Default.ProviderRuntimeSettings)!;

        Assert.Null(unset.BinaryPath);
        Assert.Null(unset.ConfigHome);
        Assert.Null(unset.ShadowHome);
        Assert.Equal("", empty.BinaryPath);
        Assert.Equal("", empty.ConfigHome);
        Assert.Equal("", empty.ShadowHome);
        Assert.Empty(unset.LaunchArguments);
        Assert.Empty(empty.LaunchArguments);
        var savedUnset = JsonSerializer.SerializeToElement(unset, ProviderRuntimeJson.Default.ProviderRuntimeSettings);
        var savedEmpty = JsonSerializer.SerializeToElement(empty, ProviderRuntimeJson.Default.ProviderRuntimeSettings);
        Assert.Equal(JsonValueKind.Null, savedUnset.GetProperty("configHome").ValueKind);
        Assert.Equal("", savedEmpty.GetProperty("configHome").GetString());
    }

    [Fact]
    public void Save_keeps_actual_machine_provider_and_revision_separate_from_literal_CLI_arguments()
    {
        var save = JsonSerializer.Deserialize("""
            {"machine":"engine-owner-7","provider":"custom/codex","expectedRevision":"revision-before",
             "settings":{"binaryPath":"/fixture/bin/codex","configHome":"/fixture/base",
                         "shadowHome":"/fixture/account","launchArguments":["--config","value with spaces","$(literal)",""]}}
            """, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs)!;

        Assert.Equal("engine-owner-7", save.Machine);
        Assert.Equal("custom/codex", save.Provider);
        Assert.Equal("revision-before", save.ExpectedRevision);
        Assert.Equal("/fixture/bin/codex", save.Settings.BinaryPath);
        Assert.Equal("/fixture/base", save.Settings.ConfigHome);
        Assert.Equal("/fixture/account", save.Settings.ShadowHome);
        Assert.Equal(["--config", "value with spaces", "$(literal)", ""], save.Settings.LaunchArguments);
        var settings = JsonSerializer.SerializeToElement(save, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs).GetProperty("settings");
        Assert.Equal(["binaryPath", "configHome", "launchArguments", "shadowHome"],
            settings.EnumerateObject().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData("env", "{}")]
    [InlineData("command", "\"node\"")]
    [InlineData("args", "[]")]
    public void Runtime_settings_reject_environment_bags_and_ACP_launcher_fields(string name, string value)
    {
        var json = "{\"binaryPath\":null,\"configHome\":null,\"shadowHome\":null,\"launchArguments\":[],\"" + name + "\":" + value + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ProviderRuntimeJson.Default.ProviderRuntimeSettings));
    }

    [Fact]
    public void Reads_and_saves_require_explicit_selection_and_CAS_fields()
    {
        var read = JsonSerializer.Deserialize("""{"machine":"engine-owner-7","provider":"codex"}""",
            ProviderRuntimeJson.Default.ProviderRuntimeReadArgs)!;
        Assert.Equal("engine-owner-7", read.Machine);
        Assert.Equal("codex", read.Provider);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"provider":"codex"}""",
            ProviderRuntimeJson.Default.ProviderRuntimeReadArgs));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
            {"machine":"engine-owner-7","provider":"codex",
             "settings":{"binaryPath":null,"configHome":null,"shadowHome":null,"launchArguments":[]}}
            """, ProviderRuntimeJson.Default.ProviderRuntimeSaveArgs));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
            {"binaryPath":null,"configHome":null,"shadowHome":null}
            """, ProviderRuntimeJson.Default.ProviderRuntimeSettings));
    }

    [Fact]
    public void Reply_retains_owner_revision_configuration_and_opaque_account_identities()
    {
        var reply = new ProviderRuntimeReply("engine-owner-7", "codex", "revision-after", "configuration-3",
            "acct-v1:" + new string('a', 64), new(null, "/fixture/base", null, []));
        var json = JsonSerializer.SerializeToElement(reply, ProviderRuntimeJson.Default.ProviderRuntimeReply);
        Assert.Equal(["accountContext", "configurationTargetId", "machine", "provider", "revision", "settings"],
            json.EnumerateObject().Select(property => property.Name).Order());
        var decoded = json.Deserialize(ProviderRuntimeJson.Default.ProviderRuntimeReply)!;
        Assert.Equal(reply.Machine, decoded.Machine);
        Assert.Equal(reply.Provider, decoded.Provider);
        Assert.Equal(reply.Revision, decoded.Revision);
        Assert.Equal(reply.ConfigurationTargetId, decoded.ConfigurationTargetId);
        Assert.Equal(reply.AccountContext, decoded.AccountContext);
        Assert.Equal(reply.Settings.ConfigHome, decoded.Settings.ConfigHome);
    }
}
