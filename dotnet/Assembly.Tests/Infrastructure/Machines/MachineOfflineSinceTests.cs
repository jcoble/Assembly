using System.Text.Json;
using Assembly.Application.Common.Machines;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Machines;

/// <summary>When a remote stopped answering: recorded once, kept while it stays down, and sent with the machine.</summary>
public sealed class MachineOfflineSinceTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    private readonly Topics _topics = new();

    private MachineStatus Status() => new(_topics, ["box"], _time);
    private static MachineItem Box(MachineStatus status) => status.Items.Single(item => item.Name == "box");

    [Fact]
    public void A_machine_not_checked_yet_has_no_time()
    {
        Assert.Null(Box(Status()).OfflineSinceMs);
    }

    [Fact]
    public void The_first_failure_sets_it_and_later_ones_keep_it()
    {
        var status = Status();
        status.Unreachable("box", "ssh tunnel exited");
        Assert.Equal(1000, Box(status).OfflineSinceMs);
        _time.Advance(TimeSpan.FromMinutes(5));
        status.Unreachable("box", "connection refused");
        Assert.Equal(1000, Box(status).OfflineSinceMs);
    }

    [Fact]
    public void Answering_clears_it_and_the_next_failure_starts_again()
    {
        var status = Status();
        status.Unreachable("box", "down");
        status.Reachable("box");
        Assert.Null(Box(status).OfflineSinceMs);
        _time.Advance(TimeSpan.FromMinutes(5));
        status.Unreachable("box", "down");
        Assert.Equal(301_000, Box(status).OfflineSinceMs);
    }

    [Fact]
    public void Editing_the_machines_profile_keeps_the_time()
    {
        var status = Status();
        status.Unreachable("box", "down");
        _time.Advance(TimeSpan.FromMinutes(5));
        status.Profile(new SavedMachine("box", "box", 9797, "token"));
        Assert.Equal(1000, Box(status).OfflineSinceMs);
        status.Unreachable("box", "still down");
        Assert.Equal(1000, Box(status).OfflineSinceMs);
    }

    [Fact]
    public void A_probe_scope_records_it_on_the_list_the_window_reads()
    {
        var status = Status();
        status.ForProbe("box").Unreachable("box", "down");
        Assert.Equal(1000, Box(status).OfflineSinceMs);
    }

    [Fact]
    public void The_machines_topic_carries_it_only_while_offline()
    {
        var status = Status();
        status.Unreachable("box", "down");
        var down = JsonSerializer.SerializeToElement(Box(status), Assembly.Application.Features.Chat.Sessions.SessionsJson.Default.MachineItem);
        Assert.Equal(1000, down.GetProperty("offlineSinceMs").GetInt64());
        status.Reachable("box");
        var up = JsonSerializer.SerializeToElement(Box(status), Assembly.Application.Features.Chat.Sessions.SessionsJson.Default.MachineItem);
        Assert.False(up.TryGetProperty("offlineSinceMs", out _));
    }
}
