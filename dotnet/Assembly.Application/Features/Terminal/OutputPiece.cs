namespace Assembly.Application.Features.Terminal;

/// <summary>A run of a terminal's output: <paramref name="Data"/> starts at <paramref name="Offset"/>; <paramref name="End"/> is the terminal's end when it was read.</summary>
public sealed record OutputPiece(long Offset, long End, byte[] Data);
