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

    [Fact]
    public async Task A_going_back_whose_database_could_not_go_back_keeps_the_copy_after_the_old_window_is_up()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo migrated > '{Database}'\nchmod 000 '{Backup}'\nexit 3");
        End(window); End(backend);
        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(60)));
        File.SetUnixFileMode(Backup, (UnixFileMode)0b110_000_000);

        AppSwap.Acknowledge(Folder, "1.0.0");

        Assert.True(File.Exists(Backup));
        Assert.Contains(Backup, SaidNote(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_database_that_cannot_be_folded_is_kept_as_it_is_and_the_old_one_still_goes_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (plan, window, backend) = Prepared($"echo not a database > '{Database}'\necho its log > '{Database}-wal'\nexit 3");
        End(window); End(backend);

        Assert.Equal(1, await AppSwap.RunAsync(plan).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("original", File.ReadAllText(Database));
        Assert.Equal("not a database\n", File.ReadAllText(Database + ".failed-2.0.0"));
        Assert.Equal("its log\n", File.ReadAllText(Database + "-wal.failed-2.0.0"));
        Assert.Empty(Directory.GetFiles(Home, "*.partial*"));
        Assert.Equal($"Assembly 2.0.0 didn't start, so this app went back to 1.0.0. Anything written after the update is kept in {Database}.failed-2.0.0 and its log {Database}-wal.failed-2.0.0, not in this app's database.", SaidNote());
    }
}
