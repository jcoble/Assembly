using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

/// <summary>Fixed typed-message effects only; no callback or wire-selectable assertion.</summary>
internal sealed class RelayEffectAssertion
{
    private readonly string _messageId;
    private readonly bool _outcome;
    private RelayEffectAssertion(string messageId, bool outcome) { _messageId = messageId; _outcome = outcome; }
    internal static RelayEffectAssertion Send(string messageId) => new(messageId, false);
    internal static RelayEffectAssertion Outcome(string messageId) => new(messageId, true);

    internal void Validate(IWriteScope scope, CommandContext context, IReadOnlyList<EffectDraft> effects)
    {
        var sent = false;
        foreach (var effect in effects)
        {
            if (effect.Kind == "stop" && !_outcome && effect.Id == "message-stop/" + _messageId)
            {
                if (scope.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE id=$id AND state='held' AND to_session_id=$to",
                    r => r.Long(0), new("$id", _messageId), new("$to", effect.SessionId)) == 1) continue;
            }
            if (effect.Kind == "send" && !sent)
            {
                var delivery = effect.Payload.Deserialize(EngineJson.Default.DeliverMessage);
                if (delivery is not null)
                {
                    var message = scope.QueryFirst("""
                        SELECT attempt,to_session_id FROM engine_message
                        WHERE id=$id AND machine=$machine AND in_flight=1
                            AND to_session_id=(SELECT to_session_id FROM engine_message WHERE id=$origin)
                        """, r => (Attempt: r.Long(0), To: r.Text(1)), new("$id", delivery.MessageId),
                        new("$machine", context.Machine), new("$origin", _messageId));
                    if (message.Attempt > 0 && effect.Id == $"send/{delivery.MessageId}/{message.Attempt}" &&
                        delivery.ToSessionId == message.To && effect.SessionId == message.To)
                    {
                        sent = true;
                        continue;
                    }
                }
            }
            throw new InvalidOperationException("Typed relay produced an unsupported effect.");
        }
    }
}
