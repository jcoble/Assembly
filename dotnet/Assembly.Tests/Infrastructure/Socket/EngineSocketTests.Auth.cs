using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class EngineSocketTests
{
    [Fact]
    public async Task Auth_window_admits_exact_three_checked_owner_commands()
    {
        using var socket = await Open();
        Assert.True((await Command(socket, new CheckProvider(), "codex/")).Applied);
        Assert.Equal("checking", _f.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
        Assert.True((await Command(socket, new StartSignIn("codex", "", null, null), "operation")).Applied);
        Assert.Equal("starting", _f.State("engine_sign_in", "operation"));
        Assert.True((await Command(socket, new CancelSignIn(), "operation", 1)).Applied);
        Assert.Equal("cancelled", _f.State("engine_sign_in", "operation"));
        Assert.Equal(3, _f.Count("engine_command_receipt"));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("progress")]
    [InlineData("interrupt_check")]
    [InlineData("interrupt_sign_in")]
    [InlineData("expire")]
    [InlineData("claim")]
    public async Task Auth_window_cannot_submit_fixed_system_facts_or_worker_commands(string kind)
    {
        EngineCommand command = kind switch
        {
            "record" => new RecordProviderCheck("installed", "unverified", JsonSerializer.SerializeToElement((string?)null), null, new("codex/", "probe_provider/codex//1", 1, 1)),
            "progress" => new SignInProgress("signed_in", null, new("operation", "codex/", "begin_sign_in/operation", 1)),
            "interrupt_check" => new InterruptProviderCheck(),
            "interrupt_sign_in" => new InterruptSignIn(),
            "expire" => new ExpireSignIn(),
            _ => new ClaimEffect()
        };
        using var socket = await Open(); var result = await Command(socket, command, "operation");
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Equal(0, _f.Count("engine_provider")); Assert.Equal(0, _f.Count("engine_sign_in"));
    }

    [Fact]
    public async Task Auth_window_commands_reject_nonzero_epoch_without_creating_records_or_effects()
    {
        using var socket = await Open();
        foreach (var command in new EngineCommand[] { new CheckProvider(), new StartSignIn("codex", "", null, null), new CancelSignIn() })
        {
            var result = await Command(socket, command, "codex/", epoch: 1); Assert.False(result.Applied); Assert.Equal("bad_args", result.Error);
        }
        Assert.Equal(0, _f.Count("engine_provider")); Assert.Equal(0, _f.Count("engine_sign_in")); Assert.Equal(0, _f.Count("engine_outbox"));
    }
}
