using Assembly.Engine.Reading;

namespace Assembly.Engine.Sessions;

// SQL selection and point-in-time audit data only; neither proves connection ownership.
public sealed record MachineStartMirrorGeneration(string Machine, long Generation);
public sealed record MachineStartCensusCut(string Machine, long Generation, long Through, CursorAt? At);
