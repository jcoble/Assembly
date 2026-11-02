using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Features.Updates;

public sealed class RemoteCliLocatorTests
{
    [Fact]
    public void Existing_profile_migrates_and_private_locator_survives_reopen_without_rotating_generation()
    {
        using var file = new TempDatabase();
        var profile = new SavedMachine("saved", "workbox-test", 12345, "custom-token-file");
        string generation; var locator = Locator();
        using (var db = TestDatabase.Open(file.Path))
        {
            db.Writer.Write("test.old-schema", scope => scope.Execute("""
                CREATE TABLE saved_machines(name TEXT PRIMARY KEY,ssh TEXT NOT NULL,port INTEGER NOT NULL,token_file TEXT NOT NULL);
                INSERT INTO saved_machines VALUES ('saved','workbox-test',12345,'custom-token-file');
                """));
            var store = new MachineStore(db);
            var captured = store.CaptureProfile(profile.Name, default)!;
            Assert.Equal(profile, captured.Profile); Assert.True(Guid.TryParseExact(captured.Generation, "N", out _));
            Assert.Null(store.GetCliLocator(profile.Name, default));
            generation = captured.Generation;
            Assert.True(db.Writer.Write("test.publish", scope => store.SetCliLocator(scope, captured, locator)));
        }
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = new MachineStore(db); var current = store.GetCliLocator(profile.Name, default)!;
            Assert.Equal(profile, current.Profile.Profile); Assert.Equal(generation, current.Profile.Generation);
            Assert.Equal(locator.InstallationId, current.Locator.InstallationId);
            Assert.Equal(locator.CaptureFile, current.Locator.CaptureFile);
            Assert.Equal(locator.Targets.Single(), current.Locator.Targets.Single());
        }
    }

    [Fact]
    public void Identical_profile_replacement_and_remove_readd_fence_an_old_locator_publication()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = new MachineStore(db); var profile = new SavedMachine("saved", "workbox-test");
        store.Import(profile); var original = store.CaptureProfile(profile.Name, default)!;
        Assert.True(db.Writer.Write("test.publish", scope => store.SetCliLocator(scope, original, Locator())));
        store.Import(profile);
        Assert.NotEqual(original.Generation, store.CaptureProfile(profile.Name, default)!.Generation);
        Assert.Null(store.GetCliLocator(profile.Name, default));
        Assert.False(db.Writer.Write("test.stale", scope => store.SetCliLocator(scope, original, Locator())));
        db.Writer.Write("test.remove-readd", scope => { store.Remove(scope, profile.Name); store.Set(scope, profile); return 0; });
        Assert.False(db.Writer.Write("test.stale-after-readd", scope => store.SetCliLocator(scope, original, Locator())));
        var current = store.CaptureProfile(profile.Name, default)!;
        Assert.True(db.Writer.Write("test.current", scope => store.SetCliLocator(scope, current, Locator())));
    }

    [Fact]
    public void Profile_and_locator_changes_roll_back_together_in_the_existing_writer()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = new MachineStore(db); var profile = new SavedMachine("saved", "workbox-test");
        store.Import(profile); var original = store.CaptureProfile(profile.Name, default)!; var locator = Locator();
        Assert.True(db.Writer.Write("test.publish", scope => store.SetCliLocator(scope, original, locator)));
        Assert.Throws<InvalidOperationException>(() => db.Writer.Write<int>("test.profile-rollback", scope =>
        { store.Set(scope, profile with { Ssh = "replacement" }); throw new InvalidOperationException("rollback"); }));
        var retained = store.GetCliLocator(profile.Name, default)!;
        Assert.Equal(original, retained.Profile); Assert.Equal(locator.InstallationId, retained.Locator.InstallationId);
        Assert.Throws<InvalidOperationException>(() => db.Writer.Write<int>("test.locator-rollback", scope =>
        { Assert.True(store.SetCliLocator(scope, original, Locator())); throw new InvalidOperationException("rollback"); }));
        Assert.Equal(locator.InstallationId, store.GetCliLocator(profile.Name, default)!.Locator.InstallationId);
        Assert.False(db.Writer.Write("test.wrong-profile", scope => store.SetCliLocator(scope,
            original with { Profile = profile with { Port = profile.Port + 1 } }, Locator())));
    }

    [Fact]
    public void Public_profile_and_machine_item_never_include_private_locator_or_generation()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var store = new MachineStore(db); var profile = new SavedMachine("saved", "workbox-test");
        store.Import(profile); var captured = store.CaptureProfile(profile.Name, default)!; var locator = Locator();
        Assert.True(db.Writer.Write("test.publish", scope => store.SetCliLocator(scope, captured, locator)));
        var saved = JsonSerializer.Serialize(store.Get(profile.Name, default), SessionsJson.Default.SavedMachine);
        var item = JsonSerializer.Serialize(MachineStore.Item(profile), SessionsJson.Default.MachineItem);
        Assert.DoesNotContain(locator.CaptureFile, saved + item); Assert.DoesNotContain(captured.Generation, saved + item);
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(new[] { "name", "port", "ssh", "tokenFile" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void Actual_capture_projection_keeps_only_opaque_targets_and_runtime_locator_metadata()
    {
        using var f = new CliCheckFixture(); var capture = RemoteCliCaptureTests.Capture(f);
        var locator = RemoteCliCapture.Locator(capture, Path.Combine(f.Home, "owner-chosen-capture"));
        var json = JsonSerializer.Serialize(locator, RemoteCliLocatorJson.Default.RemoteCliLocator);
        Assert.Equal(capture.InstallationId, locator.InstallationId);
        Assert.Equal(f.Args.ConfigurationTargetId, locator.Targets.Single().ConfigurationTargetId);
        Assert.Equal(f.Args.AccountContext, locator.Targets.Single().AccountContext);
        Assert.DoesNotContain(Path.Combine(f.Home, "private-account-canary"), json);
        Assert.DoesNotContain("PRIVATE_ARG", json); Assert.DoesNotContain("PRIVATE_HOME", json);
    }

    private static RemoteCliLocator Locator() => new("actual-machine-id", Guid.NewGuid().ToString("N"),
        "/owner-selected/private-capture", "/owner-selected/dotnet", ["/owner-selected/Assembly.Remote.dll"], "/owner-selected/home",
        [new("configured-provider", "codex", "", new string('a', 64))]);
}
