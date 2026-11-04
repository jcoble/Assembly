using Assembly.Application.Features.Notion;
using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Features.Notion;

public sealed class NotionSettingsTests
{
    [Fact]
    public async Task Failed_save_preserves_active_credential_and_source()
    {
        await using var f = new NotionFixture();
        await f.Settings.SaveAsync(NotionFixture.Source, "old", default);
        f.FailNextSqlCommit();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Settings.SaveAsync(NotionFixture.Other, "new", default));
        Assert.Equal(NotionFixture.Source, f.Settings.Read().DataSourceId);
        Assert.Equal("old", await f.ActiveSecretText());
        Assert.Single(f.Secrets.Items);
    }

    [Fact]
    public async Task Clear_removes_only_recorded_credentials_and_cache()
    {
        await using var f = new NotionFixture();
        await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        f.Secrets.Items["unrelated"] = [42];
        await f.Settings.ClearAsync(default);
        Assert.False(f.Settings.Read().HasToken);
        Assert.Equal("unconfigured", f.Settings.Read().State);
        Assert.Single(f.Secrets.Items);
        Assert.True(f.Secrets.Items.ContainsKey("unrelated"));
    }

    [Fact]
    public async Task Callback_from_replaced_attempt_cannot_save_token()
    {
        await using var f = new NotionFixture();
        f.Enqueue("/notion/start", "{\"authorizationUrl\":\"https://api.notion.com/v1/oauth/authorize\"}");
        f.Enqueue("/notion/start", "{\"authorizationUrl\":\"https://api.notion.com/v1/oauth/authorize\"}");
        var first = await f.OAuth.BeginAsync(default);
        await f.OAuth.BeginAsync(default);
        var error = await Assert.ThrowsAsync<NotionException>(() => f.OAuth.ClaimAsync(first.OperationId, first.State, "code", default));
        Assert.Equal("stale_oauth", error.Code);
        Assert.False(f.Settings.Read().HasToken);
    }

    [Fact]
    public async Task Missing_token_and_unavailable_keychain_are_visible_without_secret_output()
    {
        await using var f = new NotionFixture();
        Assert.Equal("missing_token", (await Assert.ThrowsAsync<NotionException>(() => f.Settings.SaveAsync(NotionFixture.Source, null, default))).Code);
        f.Secrets.Unavailable = true;
        Assert.Equal("keychain_unavailable", (await Assert.ThrowsAsync<NotionException>(() => f.Settings.SaveAsync(NotionFixture.Source, "fake", default))).Code);
        Assert.False(f.Settings.Read().HasToken);
    }

    [Fact]
    public async Task Discovery_requires_one_complete_unambiguous_source()
    {
        await using var f = new NotionFixture();
        f.Enqueue("/v1/search", "{\"results\":[],\"has_more\":false}");
        Assert.Equal("ambiguous_data_source", (await Assert.ThrowsAsync<NotionException>(() => f.Settings.SaveAsync(null, "fake", default))).Code);
        f.Enqueue("/v1/search", "{\"results\":[{\"object\":\"data_source\",\"id\":\"" + NotionFixture.Source + "\"}],\"has_more\":false}");
        var saved = await f.Settings.SaveAsync(null, "fake", default);
        Assert.Equal(NotionFixture.Source, saved.DataSourceId);
        Assert.True(saved.HasToken);
    }

    [Fact]
    public async Task Clear_cleanup_failure_is_reconciled_without_restoring_connection()
    {
        await using var f = new NotionFixture();
        await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        f.Secrets.FailDelete = true;
        Assert.Equal("clearing", (await f.Settings.ClearAsync(default)).State);
        Assert.False(f.Settings.Read().HasToken);
        f.Secrets.FailDelete = false;
        await f.Settings.ReconcileAsync(default);
        Assert.Equal("unconfigured", f.Settings.Read().State);
        Assert.Empty(f.Secrets.Items);
    }

    [Fact]
    public async Task Old_generation_cannot_obtain_a_replacement_credential()
    {
        await using var f = new NotionFixture();
        var old = await f.Settings.SaveAsync(NotionFixture.Source, "fake", default);
        await f.Settings.ClearAsync(default);
        Assert.Equal("notion_connection_changed", (await Assert.ThrowsAsync<NotionException>(() => f.Settings.TokenAsync(old.Generation, default))).Code);
    }

    [Fact]
    public async Task Routes_reject_unknown_fields_and_phone_access()
    {
        await using var f = new NotionFixture();
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => f.RequestAsync("settings", new { unknown = true }));
        f.Peer = new PhonePeer("phone");
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<CallFailed>(() => f.RequestAsync("settings", new { }))).Reason);
        Assert.Empty(f.Secrets.Items); Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task Explicit_legacy_adoption_and_clear_only_delete_recorded_accounts()
    {
        await using var f = new NotionFixture();
        f.Secrets.Items["integration-token"] = "fake"u8.ToArray();
        await f.RequestAsync("saveSettings", new { dataSourceId = NotionFixture.Source, token = (string?)null });
        await f.Settings.ReconcileAsync(default);
        Assert.False(f.Secrets.Items.ContainsKey("integration-token"));
        Assert.True(f.Settings.Read().HasToken);
        await f.RequestAsync("clearSettings", new { });
        await f.Settings.ReconcileAsync(default);
        Assert.Empty(f.Secrets.Items);
    }
}
