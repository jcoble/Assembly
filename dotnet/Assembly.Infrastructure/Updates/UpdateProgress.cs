using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// The one figure across everything in a run, and calling it off. A run is the machines updating at the same time: it starts
/// with the first and ends with the last. Each download counts by its bytes and anything else with no size by finished items,
/// each worth an average download. The figure only goes up, and stops at 99: the run's end is the only thing that says it's
/// finished. Cancel is there until any install starts, and goes through the same token that stops the host's work.
/// </summary>
internal sealed class UpdateProgress(MachineStatus status)
{
    /// <summary>This computer's place: the empty SSH target the release shell runs it with.</summary>
    internal const string ThisMachine = "";

    private sealed class Place(string? machine, int pieces, bool downloads)
    {
        internal readonly string? Machine = machine;
        internal readonly int Pieces = pieces;
        internal readonly bool Downloads = downloads;
        internal int Done;
        internal bool Ended;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Place> _places = [];
    private readonly Dictionary<(string Place, string Name), (long Done, long Total)> _downloads = [];
    private CancellationTokenSource _cancel = new();
    private int _percent;
    private bool _installing, _cancelled, _cancellable;

    /// <param name="machine">Null is this app.</param>
    /// <param name="place">Its SSH target, which is how the steps further down know it.</param>
    /// <param name="pieces">What it updates that has no size of its own.</param>
    /// <param name="downloads">Whether anything it brings is downloaded before it installs.</param>
    internal CancellationToken Begin(string? machine, string place, int pieces, bool downloads)
    {
        lock (_gate)
        {
            _places[place] = new(machine, pieces, downloads);
            Publish(always: true);
            return _cancel.Token;
        }
    }

    internal void Download(string place, string name, long done, long total)
    {
        lock (_gate)
        {
            if (!_places.ContainsKey(place)) return;
            _downloads[(place, name)] = (done, total);
            Publish();
        }
    }

    /// <summary>One item with no size finished.</summary>
    internal void Piece(string place)
    {
        lock (_gate)
        {
            if (!_places.TryGetValue(place, out var found)) return;
            found.Done = Math.Min(found.Done + 1, found.Pieces);
            Publish();
        }
    }

    /// <summary>
    /// An install is about to change this machine, which takes Cancel away from the whole run. A Cancel that got here first
    /// stops it. A machine that isn't in a run, like a first install, changes nothing.
    /// </summary>
    internal void Installing(string place)
    {
        lock (_gate)
        {
            if (!_places.ContainsKey(place)) return;
            if (_cancelled) throw new OperationCanceledException(_cancel.Token);
            _installing = true;
            Publish();
        }
    }

    /// <summary>This machine has nothing left to do, however it went. The last one ends the run.</summary>
    internal void End(string place)
    {
        lock (_gate)
        {
            if (!_places.TryGetValue(place, out var found)) return;
            found.Ended = true;
            found.Done = found.Pieces;
            if (_places.Values.All(item => item.Ended))
            {
                _places.Clear();
                _downloads.Clear();
                _cancel = new();
                (_percent, _installing, _cancelled, _cancellable) = (0, false, false, false);
                return;
            }
            Publish();
        }
    }

    /// <summary>False once an install has started, or with nothing to call off.</summary>
    internal bool Cancel()
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (!_cancellable) return false;
            _cancelled = true;
            cancel = _cancel;
            Publish();
        }
        // Outside the lock: what it wakes may come straight back here.
        cancel.Cancel();
        return true;
    }

    private void Publish(bool always = false)
    {
        var (percent, cancellable) = (Math.Max(_percent, Combined()), !_installing && !_cancelled && _places.Values.Any(item => !item.Ended && item.Downloads));
        if (!always && percent == _percent && cancellable == _cancellable) return;
        (_percent, _cancellable) = (percent, cancellable);
        foreach (var place in _places.Values.Where(item => !item.Ended)) status.UpdateFigure(place.Machine, percent, cancellable);
    }

    private int Combined()
    {
        var sizes = _downloads.Values.Sum(item => item.Total);
        var worth = _downloads.Count == 0 ? 1 : (double)sizes / _downloads.Count;
        var whole = sizes + (_places.Values.Sum(item => item.Pieces) * worth);
        if (whole <= 0) return 0;
        var done = _downloads.Values.Sum(item => item.Done) + (_places.Values.Sum(item => item.Done) * worth);
        return Math.Min(99, (int)(done * 100 / whole));
    }
}
