using System.Text.Json;
using Assembly.Engine.Events;
using Assembly.Engine.Rows;
using Assembly.Tests.Fixtures;

namespace Assembly.Tests.Rows;

/// <summary>Events run through one converter, with the rows and details they made, and helpers to read them.</summary>
internal sealed class Converted
{
    private Converted(List<NewRow> rows, List<ToolDetail> details)
    {
        Rows = rows;
        Details = details;
    }

    public IReadOnlyList<NewRow> Rows { get; }

    public IReadOnlyList<ToolDetail> Details { get; }

    public static Converted Fixture(string name) => From(FixtureReplay.Events(name));

    public static Converted From(IEnumerable<AgentEvent> events)
    {
        var converter = new RowConverter();
        var rows = new List<NewRow>();
        var details = new List<ToolDetail>();
        foreach (var agentEvent in events)
        {
            var batch = converter.Apply(agentEvent);
            rows.AddRange(batch.Rows);
            details.AddRange(batch.Details);
        }

        return new Converted(rows, details);
    }

    public IReadOnlyList<NewRow> OfKind(string kind) => Rows.Where(row => row.Kind == kind).ToList();

    public IReadOnlyList<NewRow> OfItem(string itemId) => Rows.Where(row => row.ItemId == itemId).ToList();

    public NewRow Last(string kind) => OfKind(kind)[^1];

    /// <summary>Each item once, in the order its first row came. Rows with no item count once each.</summary>
    public IReadOnlyList<string> ItemOrder() =>
        Rows.Select((row, index) => row.ItemId ?? $"#{index}:{row.Kind}").Distinct().ToList();

    /// <summary>The detail the store would hold for an item once every detail is applied, null keeping what was there.</summary>
    public ToolDetail Detail(string itemId) =>
        Details.Where(detail => detail.ItemId == itemId).Aggregate(new ToolDetail(itemId), (stored, next) => new ToolDetail(itemId,
            next.Input ?? stored.Input, next.Output ?? stored.Output, next.Diff ?? stored.Diff, next.Path ?? stored.Path));

    public static string Text(NewRow row, string property) => row.Payload.GetProperty(property).GetString()!;

    public static JsonElement Get(NewRow row, string property) => row.Payload.GetProperty(property);
}
