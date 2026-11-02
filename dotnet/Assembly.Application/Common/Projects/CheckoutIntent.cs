namespace Assembly.Application.Common.Projects;

public sealed record CheckoutIntent(string Kind, string? Branch, string? Base);
