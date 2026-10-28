namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Watermark contract.</summary>
public sealed record BoardWatermark(string MachineId, long Seq);
