using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    internal ParentNeedSourceCapture ParentNeedInput(string intentId, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct);
        if (read.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != machine)
            throw new RelayRefused("wrong_machine");
        var row = read.QueryFirst("""
            SELECT version,state,created_ms,CAST(payload_json AS BLOB) FROM engine_outbox
            WHERE id=$id AND machine=$machine AND kind='parent_need' AND session_id IS NULL AND run_id IS NULL
            """, r => new { Version = r.Long(0), State = r.Text(1), Created = r.Long(2), Raw = r.Blob(3) },
            new SqlArg("$id", intentId), new("$machine", machine)) ?? throw new RelayRefused("bad_args");
        return ParentNeedSourceCapture.Create(machine, intentId, row.Version, row.State, row.Created, row.Raw, ct);
    }

    internal ParentNeedOutcome? ParentNeedOutcome(string intentId, ParentNeedSource expectedSource,
        string expectedInputSha256, CancellationToken ct)
    {
        using var read = db.OpenSnapshot(ct);
        var row = read.QueryFirst("""
            SELECT state,json_extract(payload_json,'$.format'),json_extract(payload_json,'$.source'),
                json_extract(payload_json,'$.inputSha256'),json_extract(payload_json,'$.phase'),
                json_extract(payload_json,'$.outcome'),json_extract(payload_json,'$.prepared.descriptor')
            FROM engine_outbox WHERE id=$id AND machine=$machine AND kind='parent_need'
                AND session_id IS NULL AND run_id IS NULL
            """, r => new
        {
            State = r.Text(0),
            Format = r.Long(1),
            Source = r.Text(2),
            Input = r.Text(3),
            Phase = (ParentNeedPhase)r.Long(4),
            Outcome = r.IsNull(5) ? null : r.Text(5),
            Descriptor = r.IsNull(6) ? null : r.Text(6)
        },
            new SqlArg("$id", intentId), new("$machine", machine)) ?? throw new RelayRefused("source_changed");
        if (row.Format != 1 || row.Input != expectedInputSha256 || expectedSource.SourceMachine != machine ||
            JsonSerializer.Deserialize(row.Source, EngineJson.Default.ParentNeedSource) != expectedSource)
            throw new RelayRefused("source_changed");
        if (row.State is "pending" or "uncertain") return null;
        if (row.State is not ("done" or "failed") || row.Phase != ParentNeedPhase.Outcome || row.Outcome is null)
            throw new RelayRefused("source_changed");
        var outcome = JsonSerializer.Deserialize(row.Outcome, EngineJson.Default.ParentNeedOutcome)!;
        var identity = row.Descriptor is null ? expectedInputSha256 : ParentNeedCodec.Identity(
            JsonSerializer.Deserialize(row.Descriptor, EngineJson.Default.NeedAnswerRequestDescriptor)!);
        if (outcome.IdentitySha256 != identity || row.Descriptor is null &&
            (outcome.Origin != "source" || outcome.Status != "refused" || outcome.Result is not null || outcome.EffectId is not null || outcome.EffectState is not null))
            throw new RelayRefused("source_changed");
        return outcome;
    }

    public ParentNeedPendingPage ParentNeedPending(ParentNeedPendingArgs args,
        Func<ParentNeedPendingPage, int> encodedBytes, CancellationToken ct)
    {
        if (args.Limit is < 1 or > 100 || args.After is { CreatedMs: < 0 } || args.Cut is { CreatedMs: < 0 } ||
            args.After is { } after && string.IsNullOrEmpty(after.IntentId) || args.Cut is { } cutInput && string.IsNullOrEmpty(cutInput.IntentId)) throw new RelayRefused("bad_args");
        using var read = db.OpenSnapshot(ct);
        if (read.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != machine)
            throw new RelayRefused("wrong_machine");
        var cut = args.Cut ?? read.QueryFirst("""
            SELECT created_ms,id FROM engine_outbox WHERE machine=$machine AND kind='parent_need'
                AND state IN ('pending','uncertain') AND json_extract(payload_json,'$.format')=1
                AND json_extract(payload_json,'$.source.sourceMachine')=$machine
            ORDER BY created_ms DESC,id DESC LIMIT 1
            """, r => new ParentNeedCursor(r.Long(0), r.Text(1)), new SqlArg("$machine", machine));
        if (cut is null) return new([], null, null, false);
        var rows = read.Query("""
            SELECT id,version,json_extract(payload_json,'$.source'),json_extract(payload_json,'$.phase'),
                json_extract(payload_json,'$.inputSha256'),json_extract(payload_json,'$.prepared.descriptor'),created_ms
            FROM engine_outbox WHERE machine=$machine AND kind='parent_need' AND state IN ('pending','uncertain')
                AND json_extract(payload_json,'$.format')=1 AND json_extract(payload_json,'$.source.sourceMachine')=$machine
                AND ($after IS NULL OR created_ms>$after OR (created_ms=$after AND id>$afterId))
                AND (created_ms<$cut OR (created_ms=$cut AND id<=$cutId))
            ORDER BY created_ms,id LIMIT $limit
            """, r => new ParentNeedPendingItem(r.Text(0), r.Long(1),
                JsonSerializer.Deserialize(r.Text(2), EngineJson.Default.ParentNeedSource)!,
                (ParentNeedPhase)r.Long(3), r.Text(4), r.IsNull(5) ? null : ParentNeedCodec.Identity(
                    JsonSerializer.Deserialize(r.Text(5), EngineJson.Default.NeedAnswerRequestDescriptor)!), r.Long(6)),
            new SqlArg("$machine", machine), new("$after", args.After?.CreatedMs), new("$afterId", args.After?.IntentId),
            new("$cut", cut.CreatedMs), new("$cutId", cut.IntentId), new("$limit", args.Limit));
        var lastRow = rows.LastOrDefault();
        var hasLater = lastRow is not null && read.QueryFirst("""
            SELECT EXISTS(SELECT 1 FROM engine_outbox WHERE machine=$machine AND kind='parent_need'
                AND state IN ('pending','uncertain') AND json_extract(payload_json,'$.format')=1
                AND json_extract(payload_json,'$.source.sourceMachine')=$machine
                AND (created_ms>$after OR (created_ms=$after AND id>$afterId))
                AND (created_ms<$cut OR (created_ms=$cut AND id<=$cutId)))
            """, r => r.Long(0) == 1, new SqlArg("$machine", machine), new("$after", lastRow.CreatedMs),
            new("$afterId", lastRow.IntentId), new("$cut", cut.CreatedMs), new("$cutId", cut.IntentId));
        List<ParentNeedPendingItem> items = [];
        ParentNeedPendingPage page = new(items, null, cut, false);
        for (var index = 0; index < rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[index];
            if (!Enum.IsDefined(row.Phase) || row.Phase == ParentNeedPhase.Outcome || !RelayHash.IsSha(row.InputSha256))
                throw new RelayRefused("source_changed");
            var cursor = new ParentNeedCursor(row.CreatedMs, row.IntentId);
            var more = index + 1 < rows.Count || hasLater;
            var candidate = new ParentNeedPendingPage([.. items, row], more ? cursor : null, cut, more);
            if (encodedBytes(candidate) > 4 * 1024 * 1024)
            {
                if (items.Count == 0) throw new RelayRefused("frame_too_large");
                var last = items[^1];
                return new(items, new(last.CreatedMs, last.IntentId), cut, true);
            }
            items.Add(row); page = candidate;
        }
        if (encodedBytes(page) > 4 * 1024 * 1024) throw new RelayRefused("frame_too_large");
        return page;
    }
}
