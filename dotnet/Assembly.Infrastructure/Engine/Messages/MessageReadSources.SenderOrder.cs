using System.Text.Json;
using Assembly.Engine.Messages.Reading;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Messages;

internal sealed partial class MessageReadSources
{
    private sealed record OrderHead(MessageKey Key, long CreatedMs, long? CreatedSeq, string? Name);

    private sealed class OriginStream(MessageScopeCoverage coverage, SenderPosition position)
    {
        internal MessageScopeCoverage Coverage { get; } = coverage;
        internal SenderPosition Position { get; set; } = position;
        internal Queue<(OrderHead Fact, MessageDisplay? Item)> Rows { get; } = new();
        internal bool ReadEnd { get; set; }
        internal bool InitialRead { get; set; } = true;
    }

    private async Task<MessagePage> SenderNamesAsync(MessageScope scope, MessageCursor cursor, int limit, OrderBudget budget,
        Dictionary<string, (RemoteMachine Remote, RemoteMachine.OwnerConnectionBinding Binding)> pins, CancellationToken ct)
    {
        var bucket = cursor.Name!.Bucket; var coverage = queries.SenderCoverage(scope, cursor, bucket, ct);
        if (cursor.Name.Sources.Count > 0 && (cursor.Name.Sources.Count != coverage.Count ||
            cursor.Name.Sources.Any(p => !coverage.Any(c => c.Machine == p.OriginMachine && p.ScopeConsumed <= c.Count))))
            throw new ArgumentException("bad_args");
        var streams = coverage.Select(c => new OriginStream(c, cursor.Name.Sources.SingleOrDefault(p => p.OriginMachine == c.Machine) ?? new(c.Machine, null, 0, false))).ToArray();
        if (streams.Any(s => s.Position.End && s.Position.ScopeConsumed != s.Coverage.Count)) throw new InvalidOperationException("query_changed");
        List<MessageDisplay> output = []; var moved = false;
        while (output.Count < limit)
        {
            ct.ThrowIfCancellationRequested(); var blocked = false;
            foreach (var stream in streams.Where(s => !s.Position.End))
            {
                while (stream.Rows.Count == 0 && !stream.Position.End)
                {
                    if (stream.ReadEnd)
                    {
                        if (stream.Position.ScopeConsumed != stream.Coverage.Count) throw new InvalidOperationException("query_changed");
                        stream.Position = stream.Position with { End = true }; moved = true; break;
                    }
                    // Reserve canonical membership and optional full-search admission too.
                    var take = stream.InitialRead && streams.Length > 1 ? 1 : 8; var cost = take * (scope.Search.Length > 0 ? 3 : 2);
                    if (!budget.CanRead(stream.Coverage.Machine, cost)) { blocked = true; break; }
                    budget.Charge(stream.Coverage.Machine, cost, 0); stream.InitialRead = false;
                    var cut = cursor.Cuts.Single(c => c.Machine == stream.Coverage.Machine);
                    var args = new SenderOrderArgs(bucket, cut.Through, cut.At, stream.Position.Consumed, take);
                    OrderHead[] facts; bool end;
                    if (stream.Coverage.Machine == machine)
                    {
                        var local = queries.LocalSender(args, ct); end = local.End;
                        facts = local.Items.Select(f => new OrderHead(f.Key, f.CreatedMs, f.CreatedSeq, null)).ToArray();
                    }
                    else
                    {
                        var page = (await ReadPinnedAsync(stream.Coverage.Machine, "sender_order",
                            JsonSerializer.SerializeToElement(args, MessageReadJson.Default.SenderOrderArgs), pins, ct).ConfigureAwait(false))
                            .Deserialize(MessageReadJson.Default.SenderOrderPage) ?? throw new IOException("Invalid sender order response");
                        foreach (var fact in page.Items)
                            if (fact.Name is null || JsonSerializer.SerializeToUtf8Bytes(fact, MessageReadJson.Default.SenderOrderFact).Length > 32768)
                                throw new InvalidOperationException("message_response_too_large");
                        end = page.End; facts = page.Items.Select(f => new OrderHead(f.Key, f.CreatedMs, f.CreatedSeq, f.Name)).ToArray();
                        budget.Charge(stream.Coverage.Machine, 0, JsonSerializer.SerializeToUtf8Bytes(page, MessageReadJson.Default.SenderOrderPage).Length);
                    }
                    queries.ValidateNameRead(scope, cursor, ct);
                    if (facts.Length > take || facts.Select(f => f.Key).Distinct().Count() != facts.Length || facts.Any(f =>
                        f.Key.OriginMachine != stream.Coverage.Machine || f.CreatedSeq > cut.Through) || facts.Length == 0 && !end)
                        throw new IOException("Invalid sender order identity");
                    var inputs = facts.Select(f => new MessageOrderInput(f.Key, f.CreatedMs, f.Name)).ToArray();
                    var selected = queries.OrderIntersection(scope, cursor, inputs, ct);
                    budget.Charge(stream.Coverage.Machine, 0, JsonSerializer.SerializeToUtf8Bytes(selected.ToArray(), MessageReadJson.Default.MessageDisplayArray).Length);
                    var eligible = selected.ToDictionary(i => i.Key);
                    stream.ReadEnd = end;
                    foreach (var fact in facts) stream.Rows.Enqueue((fact, eligible.GetValueOrDefault(fact.Key)));
                    ConsumeExcluded(stream, ref moved);
                }
                if (blocked) break;
            }
            if (blocked)
            {
                if (output.Count == 0 && !moved) throw new InvalidOperationException("message_order_resource_limit"); break;
            }
            var ready = streams.Where(s => s.Rows.Count > 0).ToArray(); if (ready.Length == 0) break;
            var heads = ready.Select(s => s.Rows.Peek().Fact).Select(f => new MessageOrderInput(f.Key, f.CreatedMs, f.Name)).ToArray();
            var comparisonBytes = JsonSerializer.SerializeToUtf8Bytes(heads, MessageReadJson.Default.MessageOrderInputArray).Length;
            if (ready.Any(s => !budget.CanRead(s.Coverage.Machine, 1)) || !budget.CanEncode(comparisonBytes))
            { if (output.Count == 0 && !moved) throw new InvalidOperationException("message_order_resource_limit"); break; }
            foreach (var stream in ready) budget.Charge(stream.Coverage.Machine, 1, 0);
            budget.Charge(machine, 0, comparisonBytes);
            var picked = ready[queries.FirstOrderHead(heads, ct)]; var row = picked.Rows.Dequeue();
            output.Add(row.Item!); Consume(picked, row.Fact, true); moved = true; ConsumeExcluded(picked, ref moved);
        }
        queries.ValidateNameRead(scope, cursor, ct);
        // A final prefetched EOF becomes certified only after all its rows were consumed.
        foreach (var stream in streams.Where(s => s.Rows.Count == 0 && s.ReadEnd && !s.Position.End))
        {
            if (stream.Position.ScopeConsumed != stream.Coverage.Count) throw new InvalidOperationException("query_changed");
            stream.Position = stream.Position with { End = true };
        }
        return queries.SenderPage(scope, cursor, streams.Select(s => s.Position).ToArray(), output, ct);
    }

    private static void ConsumeExcluded(OriginStream stream, ref bool moved)
    {
        while (stream.Rows.TryPeek(out var row) && row.Item is null)
        { stream.Rows.Dequeue(); Consume(stream, row.Fact, false); moved = true; }
    }

    private static void Consume(OriginStream stream, OrderHead fact, bool eligible) => stream.Position = stream.Position with
    {
        Consumed = new(fact.CreatedMs, fact.Key.OriginMachine, fact.Key.Id),
        ScopeConsumed = stream.Position.ScopeConsumed + (eligible ? 1 : 0)
    };
}
