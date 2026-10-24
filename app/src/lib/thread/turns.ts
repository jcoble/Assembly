// One run's row from Node's turn query, as the relay's `stats` row carries it.
export type TurnStats = {
  runId: string;
  status: string;
  startedAt: number | null;
  durationMs: number | null;
  firstTokenMs: number | null;
  model: string | null;
  inputTokens: number | null;
  outputTokens: number | null;
  cacheHitPct: number | null;
  costUsd: number | null;
  toolCalls: number;
  branch: string | null;
  head: string | null;
  prevBranch: string | null;
  prevHead: string | null;
};

// Assembly's "Worked for" duration.
export function formatWorkedFor(ms: number): string {
  const safe = Math.max(0, ms);
  if (safe < 10_000) return `${(safe / 1000).toFixed(1)}s`;
  const seconds = Math.round(safe / 1000);
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

const clock = new Intl.DateTimeFormat(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' });
const count = (n: number) => (n < 1000 ? String(n) : `${Math.round(n / 100) / 10}k`);

// The line under a turn: how long it worked, when it started, and what it cost.
// A live turn with no stats yet shows only its running time.
export function turnLine(stats: TurnStats | undefined, liveStartedAt: number | null, now: number): string {
  const startedAt = stats?.startedAt ?? liveStartedAt;
  const running = stats ? stats.status === 'running' : liveStartedAt !== null;
  const head = running
    ? `Working for ${formatWorkedFor(now - (startedAt ?? now))}`
    : stats?.status === 'failed'
      ? 'Failed'
      : stats?.status === 'aborted'
        ? 'Stopped'
        : stats?.durationMs != null
          ? `Worked for ${formatWorkedFor(stats.durationMs)}`
          : 'Worked';
  if (!stats) return head;
  const parts = [head];
  if (stats.startedAt !== null) parts.push(clock.format(stats.startedAt));
  if (stats.model) parts.push(stats.model);
  if (stats.inputTokens != null) parts.push(`${count(stats.inputTokens)} in`);
  if (stats.outputTokens != null) parts.push(`${count(stats.outputTokens)} out`);
  if (stats.cacheHitPct != null) parts.push(`${Math.round(stats.cacheHitPct)}% cached`);
  if (stats.costUsd != null) parts.push(`$${stats.costUsd.toFixed(3)}`);
  return parts.join(' · ');
}

const checkout = (branch: string | null, head: string) =>
  !branch || branch === 'HEAD' ? head.slice(0, 7) : `${branch}@${head.slice(0, 7)}`;

// "Checkout changed" when this run started on a different branch or commit
// than the previous run left the thread's checkout on.
export function checkoutLine(stats: TurnStats | undefined): string | null {
  if (!stats?.head || !stats.prevHead) return null;
  if (stats.head === stats.prevHead && stats.branch === stats.prevBranch) return null;
  return `Checkout changed · ${checkout(stats.prevBranch, stats.prevHead)} → ${checkout(stats.branch, stats.head)}`;
}
