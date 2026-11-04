using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Engine.Messages.Reading;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Messages;

internal sealed partial class MessageReadSources
{
    private async Task<MessagePage> NamePageAsync(MessageScope scope, MessageCursor? cursor, int limit, CancellationToken ct)
    {
        var direct = queries.DirectNamePage(scope, cursor, limit, ct);
        if (direct.Page.Items.Count > 0 || direct.FirstBlockedBucket is null)
            return await FinishPageAsync(scope, direct.Page, ct, true).ConfigureAwait(false);
        var cut = direct.Page.Captured!; queries.ValidateNameRead(scope, cut, ct);
        var pins = new Dictionary<string, (RemoteMachine Remote, RemoteMachine.OwnerConnectionBinding Binding)>(StringComparer.Ordinal);
        var budget = new OrderBudget(scope.Sort == "sender" && scope.Search.Length > 0);
        var page = scope.Sort == "receiver" ? await ReceiverNamesAsync(scope, cut, limit, budget, pins, ct).ConfigureAwait(false)
            : await SenderNamesAsync(scope, cut, limit, budget, pins, ct).ConfigureAwait(false);
        return await FinishPageAsync(scope, page, ct, true, budget, pins).ConfigureAwait(false);
    }

    private async Task<MessagePage> ReceiverNamesAsync(MessageScope scope, MessageCursor cursor, int limit, OrderBudget budget, Dictionary<string, (RemoteMachine Remote, RemoteMachine.OwnerConnectionBinding Binding)> pins, CancellationToken ct)
    {
        var refs = queries.ReceiverRefs(scope, cursor, cursor.Name!.Bucket, ct); List<MessageOrderInput> names = [];
        foreach (var group in refs.GroupBy(r => r.Machine))
        {
            var args = new ReceiverOrderArgs(group.Select(r => new ReceiverOrderRef(r.SessionId, r.Version)).ToArray());
            budget.Charge(group.Key, args.Refs.Count * 2, 0);
            var page = group.Key == machine ? queries.OwningReceiver(args, ct) :
                (await ReadPinnedAsync(group.Key, "receiver_order",
                    JsonSerializer.SerializeToElement(args, MessageReadJson.Default.ReceiverOrderArgs), pins, ct).ConfigureAwait(false))
                    .Deserialize(MessageReadJson.Default.ReceiverOrderPage) ?? throw new IOException("Invalid receiver name response");
            queries.ValidateNameRead(scope, cursor, ct);
            if (page.Items.Count != args.Refs.Count || page.Items.Select(r => r.SessionId).Distinct(StringComparer.Ordinal).Count() != page.Items.Count ||
                page.Items.Any(r => !args.Refs.Any(a => a.SessionId == r.SessionId && a.Version == r.Version))) throw new IOException("Invalid receiver name identity");
            budget.Charge(group.Key, 0, JsonSerializer.SerializeToUtf8Bytes(page, MessageReadJson.Default.ReceiverOrderPage).Length);
            foreach (var item in page.Items)
            {
                if (JsonSerializer.SerializeToUtf8Bytes(item, MessageReadJson.Default.ReceiverOrderFact).Length > 32768)
                    throw new InvalidOperationException("message_response_too_large");
                names.Add(new(new(group.Key, item.SessionId), 0, item.Name));
            }
        }
        return queries.ReceiverPage(scope, cursor, cursor.Name.Bucket, names, limit, ct);
    }

    private async Task<JsonElement> ReadPinnedAsync(string source, string method, JsonElement args,
        Dictionary<string, (RemoteMachine Remote, RemoteMachine.OwnerConnectionBinding Binding)> pins, CancellationToken ct)
    {
        var transport = RequireForward(source);
        if (!pins.TryGetValue(source, out var pin))
        { pin = await transport.CaptureMessageOwnerAsync(source, ct).ConfigureAwait(false); pins.Add(source, pin); }
        try { return await transport.ReadMessageOnAsync(pin, method, args, ct).ConfigureAwait(false); }
        catch (CallFailed error) when (error.Reason is "message_response_too_large" or "message_order_resource_limit")
        { throw new CallFailed(source + ": " + error.Message, error.Reason); }
    }

    private MachineForward RequireForward(string source) => forward ?? throw new MachineUnreachable(source, "The required message name owner is unavailable.");

    private sealed class OrderBudget(bool searchReserved = false)
    {
        internal bool SearchReserved { get; } = searchReserved;
        private readonly Dictionary<string, int> sources = new(StringComparer.Ordinal);
        private int total; private int bytes;
        internal bool CanEncode(int size) => bytes + size <= 1024 * 1024 - 1024;
        internal bool CanRead(string source, int count) => sources.GetValueOrDefault(source) + count <= 200 && total + count <= 800 && bytes < 1024 * 1024 - 32768;
        internal void Charge(string source, int count, int encoded)
        {
            if (sources.GetValueOrDefault(source) + count > 200 || total + count > 800 || encoded > 1024 * 1024 - 1024 || bytes + encoded > 1024 * 1024 - 1024)
                throw new InvalidOperationException("message_order_resource_limit:" + source);
            sources[source] = sources.GetValueOrDefault(source) + count; total += count; bytes += encoded;
        }
    }
}
