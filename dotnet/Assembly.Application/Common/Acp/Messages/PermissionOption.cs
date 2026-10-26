namespace Assembly.Application.Common.Acp.Messages;

/// <summary><c>Kind</c> is <c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c> or <c>reject_always</c>.</summary>
public sealed record PermissionOption(string OptionId, string Name, string Kind);
