using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Engine.Messages.Reading;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Messages;

/// <summary>One request's bounded authority reads through existing pinned machine sockets, never mirrored body bytes.</summary>
internal sealed class MessageReadSources(MessageQueries queries, string machine, MachineForward? forward)
{
    internal async Task<MessagePage> PageAsync(MessageScope scope, MessageCursor? cursor, int limit, CancellationToken ct)
    {
        var candidates = queries.Candidates(scope, cursor, limit, ct);
        var needed = (scope.Machine is null ? candidates.Cut.Select(cut => cut.Machine) :
            candidates.Items.SelectMany(item => new[] { item.Key.OriginMachine, item.Storage.Machine }).Append(scope.Machine)).Distinct(StringComparer.Ordinal).ToArray();
        var availability = await Task.WhenAll(needed.Select(async source => (Source: source, Available: source == machine || forward is not null && await forward.MessageSourceAvailableAsync(source, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        var missing = candidates.MissingMachines.Concat(availability.Where(source => !source.Available).Select(source => source.Source)).ToHashSet(StringComparer.Ordinal);
        MessageKey[] matches = [];
        if (scope.Search.Length > 0)
        {
            var groups = candidates.Items.GroupBy(item => item.Key.OriginMachine).ToArray();
            var results = await Task.WhenAll(groups.Select(async group =>
            {
                var origin = group.Key; var ids = group.Select(item => item.Key.Id).ToArray();
                if (ids.Length > 200) throw new InvalidOperationException("message_candidate_limit");
                if (missing.Contains(origin)) return (origin, Ids: Array.Empty<string>(), Missing: true);
                try
                {
                    var args = new MatchArgs(ids, scope.Search);
                    var result = origin == machine ? queries.Match(args, ct) : (await (forward ?? throw new KeyNotFoundException("Unknown source"))
                        .CallAsync(origin, "messages", "match", JsonSerializer.SerializeToElement(args, MessageReadJson.Default.MatchArgs), true, ct).ConfigureAwait(false))
                        .Deserialize(MessageReadJson.Default.BodyMatch) ?? throw new IOException("Invalid body match response");
                    if (result.Ids.Count > ids.Length || result.Ids.Any(id => !ids.Contains(id, StringComparer.Ordinal))) throw new IOException("Invalid body match identity");
                    return (origin, Ids: result.Ids.ToArray(), Missing: false);
                }
                catch (Exception error) when (error is MachineUnreachable or KeyNotFoundException or IOException) { return (origin, Ids: Array.Empty<string>(), Missing: true); }
            })).ConfigureAwait(false);
            foreach (var result in results.Where(result => result.Missing)) missing.Add(result.origin);
            matches = results.SelectMany(result => result.Ids.Select(id => new MessageKey(result.origin, id))).ToArray();
        }
        ct.ThrowIfCancellationRequested();
        var selected = scope.Search.Length == 0 ? candidates : queries.SelectMatches(scope, candidates, matches, ct);
        queries.ValidatePage(scope, selected, ct);
        var page = selected with
        {
            MissingMachines = missing.Order(StringComparer.Ordinal).ToArray(),
            Items = selected.Items.Select(item => missing.Contains(item.Key.OriginMachine) ? item with { BodyAvailable = false } : item).ToArray()
        };
        MessageQueries.ValidateBudget(page);
        return page;
    }

    internal async Task<MessageCount> CountAsync(MessageSessionKey session, IReadOnlyList<SeenCut> seen, string? storage, CancellationToken ct)
    {
        var count = queries.Count(session, seen, ct, storage);
        var missing = await Task.WhenAll(count.Cut.Where(cut => cut.Machine != machine).Select(async cut =>
            (cut.Machine, Available: forward is not null && await forward.MessageSourceAvailableAsync(cut.Machine, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var names = count.MissingMachines.Concat(missing.Where(source => !source.Available).Select(source => source.Machine)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        queries.ValidateCount(session, storage, count, ct);
        var result = count with { MissingMachines = names, Partial = count.Partial || names.Length > 0 };
        if (JsonSerializer.SerializeToUtf8Bytes(result, MessageReadJson.Default.MessageCount).Length > 1024 * 1024 - 1024)
            throw new InvalidOperationException("message_response_too_large");
        return result;
    }

    internal async Task<JsonElement> BodyAsync(BodyArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Machine) || string.IsNullOrWhiteSpace(args.MessageId)) throw new ArgumentException("bad_args");
        var body = args.Machine == machine ? queries.Body(args.MessageId, ct) : await (forward ?? throw new KeyNotFoundException("Unknown owning source"))
            .CallAsync(args.Machine, "messages", "body", JsonSerializer.SerializeToElement(args, MessageReadJson.Default.BodyArgs), true, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (body.GetProperty("storage").GetProperty("machine").GetString() != args.Machine || body.GetProperty("storage").GetProperty("id").GetString() != args.MessageId)
            throw new CallFailed("The message body source changed.", "query_changed");
        return body;
    }
}
