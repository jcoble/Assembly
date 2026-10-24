using System.Text.Json;

namespace Assembly.Engine.Rows;

/// <summary>
/// A row before the store gives it a seq. <c>ItemId</c> ties together the rows of one thing on screen
/// (a message's deltas and its final form, or a tool call's states); the newest row of an item is what shows.
/// </summary>
public sealed record NewRow(string? RunId, string? ItemId, string Kind, JsonElement Payload, long CreatedAt);
