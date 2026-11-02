using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions.Configuration;

internal sealed record CollectedConfig(ConfigObservation Observation, IReadOnlyDictionary<string, string> Choices,
    KeyValuePair<string, string>? ConfirmedModel);

/// <summary>One model-first collector for ordinary opens, restored reads and tentative idle picks.</summary>
internal static class ConfigChoiceCollector
{
    internal static async Task<CollectedConfig> CollectAsync(RunningAdapter running, string nativeId, ConfigObservation observed,
        SessionConfig config, bool strictInitial, KeyValuePair<string, string>? pick, StageTimes stages, CancellationToken ct)
    {
        var choices = config.Choices.ToDictionary(pair => pair.Key, pair => pair.Value);
        var required = strictInitial ? new Dictionary<string, string>(choices) : new Dictionary<string, string>();
        var model = ConfigOptions.Model(observed.Options);
        KeyValuePair<string, string>? confirmedModel = null;
        if (strictInitial && (model is null || !choices.ContainsKey(model.Value.Id)))
            throw new CallFailed("The agent no longer offers the initial model selection.");
        if (config.PendingModel is { } pending)
        {
            if (model is not { } selector) throw ModelUnavailable(pending);
            var selected = choices.TryGetValue(selector.Id, out var chosen) && selector.Values.Contains(chosen) ? chosen : pending;
            if (strictInitial && choices.GetValueOrDefault(selector.Id) != selected || !selector.Values.Contains(selected))
                throw ModelUnavailable(selected);
            choices[selector.Id] = selected;
            required[selector.Id] = selected;
            confirmedModel = new(selector.Id, selected);
        }
        if (pick is { } selectedPick)
        {
            choices[selectedPick.Key] = selectedPick.Value;
            required[selectedPick.Key] = selectedPick.Value;
            if (confirmedModel is { } previous && previous.Key == selectedPick.Key) confirmedModel = selectedPick;
        }
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var modelId = ConfigOptions.Model(observed.Options)?.Id;
            var id = ConfigOptions.Selects(observed.Options).OrderBy(option => option.Id == modelId ? 0 : 1)
                .Select(option => option.Id).FirstOrDefault(id => choices.ContainsKey(id) && !applied.Contains(id));
            if (id is null) break;
            applied.Add(id);
            var option = ConfigOptions.Selects(observed.Options).First(option => option.Id == id);
            var value = choices[id];
            if (!option.Values.Contains(value))
            {
                if (required.ContainsKey(id)) throw new CallFailed("The agent no longer offers the selected setting.");
                continue;
            }
            if (option.Current == value) continue;
            try
            {
                var result = await running.Link.Client.SetConfigOptionAsync(nativeId, id, value, ct);
                var changed = ConfigObservation.Read(running.Updates, nativeId, result.ConfigOptions);
                observed = changed with { Commands = changed.Commands ?? observed.Commands };
            }
            catch (JsonRpcException) when (!required.ContainsKey(id))
            {
                // Legacy optional choices may disappear. Never copy raw provider failures into settings errors/logs.
                Console.Error.WriteLine("The agent did not accept a previously saved optional setting.");
            }
        }
        ct.ThrowIfCancellationRequested();
        if (required.Any(pair => ConfigOptions.Selects(observed.Options).FirstOrDefault(option => option.Id == pair.Key).Current != pair.Value))
            throw new CallFailed("The agent did not confirm all selected settings.");
        if (confirmedModel is { } confirmation && ConfigOptions.ModelValue(observed.Options) != confirmation.Value)
            throw ModelUnavailable(confirmation.Value);
        if (choices.Count > 0) stages.Mark("session config");
        return new(observed, choices, confirmedModel);
    }

    private static CallFailed ModelUnavailable(string selector) =>
        new($"The selected model '{selector}' is not available for this agent.", "model_unavailable");
}
