using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

// What a window saying it's up lets go of after a going back.
public sealed partial class AppSwapTests
{
    [Fact]
    public async Task After_a_going_back_the_copy_goes_once_the_old_versions_window_is_up()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nexit 3");
        End(window); End(backend);
        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(Backup));

        // The version that failed saying it's up changes nothing; the old one back in place does.
        AppSwap.Acknowledge(Folder, "2.0.0");
        Assert.True(File.Exists(Backup));
        AppSwap.Acknowledge(Folder, "1.0.0");

        Assert.Empty(Directory.GetFiles(Folder, AppSwap.BackupPrefix + "*"));
        Assert.Equal("migrated\n", File.ReadAllText(Database + ".failed-2.0.0"));
        Assert.Equal(WentBackKeepingLaterWrites, SaidNote());
    }
}
