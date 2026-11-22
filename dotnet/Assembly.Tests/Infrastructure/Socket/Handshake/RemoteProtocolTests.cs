using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Draft.Base;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket.Handshake;

/// <summary>
/// A remote on another socket protocol (TSK-1692): it is named with its reason, and one on an older protocol updates
/// itself once, when this backend is the one that updates it.
/// </summary>
public sealed class RemoteProtocolTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly MachinesSocketTests.StubRemote _stub = new();
    private readonly List<string> _refused = [];
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private MachineStatus _status = null!;
    private RemoteMachine _box = null!;

    public async Task InitializeAsync()
    {
        await _stub.StartAsync();
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _hub = new LiveHub(RowStore.Open(_db), topics: topics);
        _status = new MachineStatus(topics, ["box"]) { RefusedForProtocol = machine => { lock (_refused) _refused.Add(machine); } };
        _box = new RemoteMachine("box", _stub.Address, "box-token", _hub, SessionStore.Open(_db, TimeProvider.System, "checkouts"),
            TextWriter.Null, _status);
    }

    public async Task DisposeAsync()
    {
        await _box.DisposeAsync();
        await _stub.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    // Pins: spec test 18 (handshake)
    [Fact]
    public async Task Spec18_handshake_a_protocol_1_remote_is_refused_with_its_reason()
    {
        _stub.HelloClose = "expected a hello";

        var failure = await Assert.ThrowsAsync<MachineUnreachable>(() => _box.ProbeAsync(CancellationToken.None));

        Assert.Contains("runs protocol 1; this computer needs 2", failure.Message);
        Assert.Equal("runs protocol 1; this computer needs 2", _status.Items.Single(item => item.Name == "box").Reason);
        lock (_refused) Assert.Equal(["box"], _refused);
    }

    [Fact]
    public async Task A_remote_with_a_newer_protocol_is_never_updated()
    {
        _stub.HelloClose = $"expected a hello with protocol {SocketConnection.Protocol + 1}";

        var failure = await Assert.ThrowsAsync<MachineUnreachable>(() => _box.ProbeAsync(CancellationToken.None));

        Assert.Contains($"runs protocol {SocketConnection.Protocol + 1}; this computer needs {SocketConnection.Protocol}", failure.Message);
        lock (_refused) Assert.Empty(_refused);
    }

    [Fact]
    public async Task A_remote_refused_for_an_older_protocol_starts_its_update_once()
    {
        var status = new MachineStatus(new TopicSet(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source(starts: true);
        var updates = Updates(source, status, work);
        updates.Begin();

        status.ProtocolOlder("box");
        await Until(() => source.Automatic == 1 && !status.Items[1].Updating);
        await updates.OlderProtocolAsync("box", CancellationToken.None);

        Assert.Equal(1, source.Automatic);
        Assert.Equal(["backend"], source.Updated);
    }

    [Fact]
    public async Task A_lane_remote_refused_for_protocol_is_left_to_app_dev()
    {
        var status = new MachineStatus(new TopicSet(), ["box"]);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source(starts: false);

        await Updates(source, status, work).OlderProtocolAsync("box", CancellationToken.None);

        Assert.Equal(0, source.Automatic);
        Assert.Null(status.Items[1].Updates);
    }

    private static MachineUpdates Updates(IUpdateSource source, MachineStatus status, BackgroundWork work) =>
        new(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null);

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(5, deadline.Token);
    }

    private sealed class Source(bool starts) : IUpdateSource
    {
        public int Automatic;
        public string[] Updated = [];

        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) =>
            Task.FromResult(new UpdateWanted(null, "9.0.0", new Dictionary<string, string?>(), null, new Dictionary<string, string?>()));
        public bool StartsRemoteUpdates(string machine) => starts;
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            if (automatic) Interlocked.Increment(ref Automatic);
            Updated = [.. machines.SelectMany(machine => machine.Behind).Select(item => item.Kind)];
            return Task.CompletedTask;
        }
    }
}
