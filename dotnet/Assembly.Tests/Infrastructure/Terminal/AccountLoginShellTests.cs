using System.Diagnostics;
using Assembly.Infrastructure.Terminal;

namespace Assembly.Tests.Infrastructure.Terminal;

public sealed class AccountLoginShellTests
{
    [Fact]
    public async Task Effective_account_lookup_ignores_an_inherited_shell_choice()
    {
        var start = new ProcessStartInfo("/usr/bin/perl");
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(AccountLoginShell.Lookup);
        start.Environment["SHELL"] = "/not-the-accounts-shell";
        var result = await AccountLoginShell.ReadAsync(start, default);
        Assert.Null(result.Error);
        Assert.NotEqual("/not-the-accounts-shell", result.Path);
        Assert.True(AccountLoginShell.IsUsable(result.Path));
    }

    [Theory]
    [InlineData("print q{}")]
    [InlineData("print q{relative-shell}")]
    [InlineData("print q{/not/a/shell}")]
    [InlineData("exit 2")]
    [InlineData("print q{x} x 5000")]
    public async Task Invalid_or_failed_lookup_is_retained_as_unavailable_without_a_live_child(string code)
    {
        var start = Perl(code); var pid = 0;
        var result = await AccountLoginShell.ReadAsync(start, default, value => pid = value);
        Assert.Null(result.Path);
        Assert.Equal(AccountLoginShell.Unavailable, result.Error);
        Assert.True(pid > 0);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task Cancellation_reaps_only_the_actual_started_lookup_child_and_propagates()
    {
        using var stop = new CancellationTokenSource(); var pid = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountLoginShell.ReadAsync(Perl("sleep 60"), stop.Token,
            value => { pid = value; stop.Cancel(); }));
        Assert.True(pid > 0);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    private static ProcessStartInfo Perl(string code)
    {
        var start = new ProcessStartInfo("/usr/bin/perl"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(code); return start;
    }
}
