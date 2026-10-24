// How full the agent's context window is, for the composer's ring and hint
// (Assembly's contextMeterState, composerSlashCommands.ts). Plain functions, for node tests.

export type ContextMeter = {
  // Shares of the window, rounded: what is left and what is spent (100 - left).
  left: number;
  used: number;
  // What the ring paints, as a share of its circle: what is left, but never more
  // than 90 once anything is spent, so a touched window always shows a gap.
  arc: number;
  warm: boolean;
  hot: boolean;
  // "51,285 of 1,000,000 (5%)"
  detail: string;
};

// The smallest spent share the ring shows: a true 0.5% is a pixel of gap.
export const MIN_SPENT_ARC = 10;

// Null when the agent hasn't reported, or reports more used than the window holds.
export function contextMeter(context: { used: number; size: number } | null): ContextMeter | null {
  if (!context || context.size <= 0 || context.used < 0 || context.used > context.size) return null;
  const spent = (context.used / context.size) * 100;
  const left = Math.round(100 - spent);
  const exact = (n: number) => n.toLocaleString('en-US');
  return {
    left,
    used: 100 - left,
    arc: spent <= 0 ? 100 : 100 - Math.max(MIN_SPENT_ARC, spent),
    warm: left <= 25,
    hot: left <= 10,
    detail: `${exact(context.used)} of ${exact(context.size)} (${100 - left}%)`,
  };
}
