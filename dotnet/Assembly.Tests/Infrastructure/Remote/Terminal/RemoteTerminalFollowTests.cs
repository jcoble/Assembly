using System.Text;
using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Terminal;
using Assembly.Infrastructure.Machines;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Terminal;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteTerminalFollowTests
{
    [Fact]
    public async Task Fixed_program_read_follows_before_replay_without_start_or_list_and_last_view_releases_only_follow()
    {
        await using var f = await Fixture.Start();
        var output = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, json) =>
        { var row = JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!; if (row.Data?.Length > 0) output.TrySetResult(row); });
        var second = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, _) => { });
        Assert.Empty((await f.Routed.ReadAsync(f.Id, 0, default)).Data);
        Assert.Equal(0, f.OwnerCalls.Starts); Assert.Equal(0, f.OwnerCalls.Lists);
        Assert.Single(f.RemotePty.Started); Assert.Empty(f.LocalPty.Started);
        f.Hosts.Mac.Topics.Unsubscribe(first);
        Assert.True(f.Hosts.Remote.Topics.HasSubscribers("terminal", f.Program.Id));
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Removed(string topic, string? key) { if (topic == "terminal" && key == f.Program.Id) removed.TrySetResult(); }
        f.Hosts.Remote.Topics.Unsubscribed += Removed;
        f.Hosts.Mac.Topics.Unsubscribe(second);
        // A rapid reopen must survive an already queued release of the old view.
        using var reopened = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, json) =>
        { var row = JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!; if (row.Data?.Length > 0) output.TrySetResult(row); });
        await f.Routed.ReadAsync(f.Id, 0, default);
        f.RemotePty.Started[0].Emit("still-followed"u8.ToArray());
        Assert.Equal("still-followed"u8.ToArray(), (await output.Task.WaitAsync(TimeSpan.FromSeconds(10))).Data);
        removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hosts.Mac.Topics.Unsubscribe(reopened);
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.RemotePty.Started[0].Disposed);
        Assert.Single(f.RemotePty.Started);
        f.Hosts.Remote.Topics.Unsubscribed -= Removed;
    }

    [Fact]
    public async Task Terminal_only_loss_recovers_quiet_final_bytes_then_exit_on_the_same_actual_owner()
    {
        await using var f = await Fixture.Start();
        var lost = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<OutputRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new byte[5 * 1024 * 1024 + 37];
        for (var i = 0; i < expected.Length; i++) expected[i] = (byte)(i % 251);
        var data = new byte[expected.Length];
        var received = new bool[expected.Length];
        var count = 0;
        using var viewer = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, json) =>
        {
            var row = JsonSerializer.Deserialize(json, TerminalJson.Default.OutputRow)!;
            if (row.Unavailable == TerminalUnavailable.ConnectionLost) { lost.TrySetResult(row); return; }
            Assert.NotNull(row.Seq); Assert.NotNull(row.PrevSeq);
            if (row.Data is { } bytes)
                for (var i = 0; i < bytes.Length; i++)
                {
                    var offset = row.PrevSeq!.Value + i;
                    Assert.InRange(offset, 0, expected.LongLength - 1);
                    if (received[offset]) Assert.Equal(data[offset], bytes[i]);
                    else { received[offset] = true; count++; }
                    data[offset] = bytes[i];
                }
            if (row.Exit is not null) exited.TrySetResult(row);
        });
        await f.Routed.ReadAsync(f.Id, 0, default);
        var binding = await f.Hosts.ConnectedMachine.CaptureOwnerBindingAsync(f.Hosts.Remote.Runtime.Machine, default);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hosts.Remote.OpeningTerminal = () => resume.Task;
        f.Hosts.Remote.TerminalEndpoint.DropAll();
        var loss = await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(loss.Seq); Assert.Null(loss.PrevSeq); Assert.Null(loss.Data); Assert.Null(loss.Exit);
        for (var from = 0; from < expected.Length; from += 4096)
            f.RemotePty.Started[0].Emit(expected.AsSpan(from, Math.Min(4096, expected.Length - from)).ToArray());
        f.RemotePty.Started[0].End(7);
        Assert.Equal(7, await f.Owner.ExitedAsync(f.Program.Id, default).WaitAsync(TimeSpan.FromSeconds(10)));
        resume.TrySetResult();
        var ending = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(7, ending.Exit);
        Assert.Equal(expected.Length, count);
        Assert.Equal(expected, data);
        Assert.Equal(expected.LongLength + 1, ending.Seq);
        Assert.True(await f.Hosts.ConnectedMachine.IsCurrentOwnerBindingAsync(binding, default));
        Assert.Equal(0, f.OwnerCalls.Starts); Assert.Single(f.RemotePty.Started); Assert.Empty(f.LocalPty.Started);
    }

    [Fact]
    public async Task Owner_absence_reports_missing_without_fabricating_offsets_or_recreating_the_fixed_program()
    {
        await using var f = await Fixture.Start();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var missing = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewer = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, json) =>
        {
            using var row = JsonDocument.Parse(json);
            if (!row.RootElement.TryGetProperty("unavailable", out var state)) return;
            if (state.GetString() == "connection_lost") lost.TrySetResult();
            if (state.GetString() == "terminal_missing") missing.TrySetResult(row.RootElement.Clone());
        });
        await f.Routed.ReadAsync(f.Id, 0, default);
        f.Hosts.Remote.TerminalEndpoint.DropAll();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Owner.CloseAsync(f.Program.Id);
        var unavailable = await missing.Task.WaitAsync(TimeSpan.FromSeconds(15));
        foreach (var property in new[] { "seq", "prevSeq", "data", "exit" }) Assert.False(unavailable.TryGetProperty(property, out _));
        Assert.Single(f.RemotePty.Started); Assert.Empty(f.LocalPty.Started);
        Assert.Equal(0, f.OwnerCalls.Starts);
    }

    [Fact]
    public async Task An_unregistered_serialized_id_cannot_touch_an_actual_owner_terminal_after_client_replacement()
    {
        await using var f = await Fixture.Start();
        using var hub = new LiveHub(RowStore.Open(f.Hosts.Mac.Fixture.Db));
        await using var replacement = new RemoteMachine("workbox-test", f.Hosts.Remote.Address, "isolated-token", hub,
            f.Hosts.Mac.Sessions, TextWriter.Null, new MachineStatus(f.Hosts.Mac.Topics, []), topics: f.Hosts.Mac.Topics);
        var connections = f.Hosts.Remote.TerminalConnections;
        var failure = await Assert.ThrowsAsync<CallFailed>(() => replacement.Terminals.CallAsync("read", new ReadArgs(f.Program.Id, 0),
            TerminalJson.Default.ReadArgs, TerminalJson.Default.OutputPiece, default));
        Assert.Equal("source_changed", failure.Reason);
        Assert.Equal(connections, f.Hosts.Remote.TerminalConnections);
        Assert.Equal(0, f.OwnerCalls.Reads); Assert.Equal(0, f.OwnerCalls.Starts);
        Assert.False(f.RemotePty.Started[0].Disposed);
    }

    [Fact]
    public async Task A_registered_handle_refuses_the_actual_replacement_engine_before_a_terminal_send()
    {
        await using var f = await Fixture.Start();
        var original = f.Hosts.Remote.Runtime.Machine;
        using var viewer = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, _) => { });
        await f.Routed.ReadAsync(f.Id, 0, default);
        var binding = await f.Hosts.ConnectedMachine.CaptureOwnerBindingAsync(original, default);
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revoked = binding.Invalidated.Register(() => invalidated.TrySetResult());
        var replacement = new TwoHosts.Host();
        await f.Hosts.ReplaceAtSameEndpoint(replacement);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await invalidated.Task.WaitAsync(limit.Token);
        await f.Hosts.ConnectedMachine.ProbeAsync(limit.Token);
        var actual = await f.Hosts.ConnectedMachine.ReadMirrorOwnerAsync(limit.Token);
        Assert.NotEqual(original, actual.Info.Machine);
        Assert.True(binding.IsInvalidated);
        var before = replacement.TerminalConnections;
        var failure = await Assert.ThrowsAsync<CallFailed>(() => f.Routed.WriteAsync(f.Id, "must not retarget"u8.ToArray(), limit.Token).AsTask());
        Assert.Equal("source_changed", failure.Reason);
        Assert.Equal(before, replacement.TerminalConnections);
        Assert.Empty(f.RemotePty.Started[0].Written);
        Assert.Empty(f.LocalPty.Started);
    }

    [Fact]
    public async Task Cancelling_a_fixed_attach_and_closing_its_view_joins_the_owner_read_and_releases_follow()
    {
        await using var f = await Fixture.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OwnerCalls.Reading = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { finished.TrySetResult(); }
        };
        void Removed(string topic, string? key) { if (topic == "terminal" && key == f.Program.Id) removed.TrySetResult(); }
        f.Hosts.Remote.Topics.Unsubscribed += Removed;
        using var viewer = f.Hosts.Mac.Topics.Subscribe("terminal", f.Id, _ => { }, (_, _) => { });
        using var cancel = new CancellationTokenSource();
        var read = f.Routed.ReadAsync(f.Id, 0, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Hosts.Mac.Topics.Unsubscribe(viewer);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Hosts.Remote.Topics.HasSubscribers("terminal", f.Program.Id));
        Assert.False(f.RemotePty.Started[0].Disposed);
        Assert.Equal(0, f.OwnerCalls.Starts); Assert.Equal(0, f.OwnerCalls.Lists);
        f.Hosts.Remote.Topics.Unsubscribed -= Removed;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TwoHosts Hosts = new();
        internal readonly FakePtyStarter RemotePty = new(), LocalPty = new();
        internal readonly BackgroundWork Work = new(TimeProvider.System, TextWriter.Null);
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("assembly-terminal-follow-");
        internal TerminalHost Owner = null!, Local = null!;
        internal RoutedTerminals Routed = null!;
        internal Counting OwnerCalls = null!;
        internal ProgramTerminal Program = null!;
        internal string Id = null!;
        internal static async Task<Fixture> Start()
        {
            var f = new Fixture();
            try
            {
                f.Owner = new(f.RemotePty, f.Hosts.Remote.Topics, TimeProvider.System, ("/bin/sh", null));
                f.OwnerCalls = new(f.Owner);
                f.Hosts.Remote.AdditionalFeatures = calls => new TerminalFeature(f.OwnerCalls).Register(calls);
                await f.Hosts.Start(); await f.Hosts.StartMirror();
                f.Local = new(f.LocalPty, f.Hosts.Mac.Topics, TimeProvider.System, ("/bin/sh", null));
                f.Routed = new(f.Local, f.Hosts.Mac.Sessions, new MachineSet(f.Hosts.ConnectedMachine, f.Hosts.ConnectedMachine), f.Hosts.Mac.Topics, f.Work);
                f.Program = await f.Owner.StartProgramAsync(new ProgramStart("/bin/sh", [], new Dictionary<string, string?>(), f._folder.FullName, "signin:controlled"), default);
                // Metadata is from the actual host producer and authenticated owning Engine, never a guessed route identity.
                f.Id = await f.Hosts.ConnectedMachine.Terminals.RegisterAsync(f.Hosts.Remote.Runtime.Machine, f.Program.Id, default);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Routed is not null) await Routed.DisposeAsync();
            await Hosts.DisposeAsync();
            if (Owner is not null) await Owner.DisposeAsync();
            if (Local is not null) await Local.DisposeAsync();
            await Work.DisposeAsync(); _folder.Delete(true);
        }
    }
    private sealed class Counting(TerminalHost owner) : ITerminals
    {
        internal int Starts, Lists, Reads;
        internal Func<CancellationToken, Task>? Reading;
        public Task<TerminalInfo> StartAsync(StartArgs a, CancellationToken ct = default) { Starts++; return owner.StartAsync(a, ct); }
        public Task<IReadOnlyList<TerminalInfo>> ListAsync(string? id, CancellationToken ct) { Lists++; return owner.ListAsync(id, ct); }
        public async Task<OutputPiece> ReadAsync(string id, long offset, CancellationToken ct)
        { Reads++; if (Reading is { } reading) await reading(ct); return await owner.ReadAsync(id, offset, ct); }
        public ValueTask WriteAsync(string id, byte[] data, CancellationToken ct) => owner.WriteAsync(id, data, ct);
        public Task ResizeAsync(string id, int cols, int rows, CancellationToken ct) => owner.ResizeAsync(id, cols, rows, ct);
        public ValueTask CloseAsync(string id, CancellationToken ct = default) => owner.CloseAsync(id, ct);
        public ValueTask CloseOwnerAsync(string id, CancellationToken ct = default) => owner.CloseOwnerAsync(id, ct);
    }
}
