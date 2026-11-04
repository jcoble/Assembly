using Assembly.Infrastructure.Common.Processes;

namespace Assembly.Tests.Infrastructure.Common.Processes;

public sealed class ProcessGroupTests
{
    [Fact]
    public void A_ps_row_of_three_pids_is_read()
    {
        Assert.Equal((4089422, 1, 4089422), ProcessGroup.ParseRow("  4089422       1 4089422"));
    }

    [Theory]
    [InlineData("1 1 4294967295")]
    [InlineData("1 -1 1")]
    [InlineData("1 1 -")]
    [InlineData("1 1")]
    [InlineData("1 1 1 1")]
    [InlineData("")]
    public void A_row_that_is_not_three_pids_is_skipped_so_the_kill_still_runs(string row)
    {
        Assert.Null(ProcessGroup.ParseRow(row));
    }
}
