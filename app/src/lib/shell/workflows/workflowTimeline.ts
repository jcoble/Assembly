/**
 * The run timeline: one list, in time order, of what happened in a workflow run.
 *
 * It merges three sources: the run's ledger rows (`listWorkflowRunHistory`),
 * the agent messages sent during the run, and the run snapshot for what only
 * it holds (the PR link in the `pr` step's command output, the `proof` step's
 * files). Ledger rows that only record bookkeeping (scheduling, each recorded
 * command, a step about to start) are left out.
 */
import type { AgentMessage } from '../../tauriSource.ts';
import type { WorkflowHistoryRow, WorkflowRunRecord } from './workflowTypes.ts';

export type WorkflowTimelineKind =
  | 'run' | 'start' | 'finish' | 'checks' | 'review' | 'failed' | 'timeout'
  | 'waiting' | 'owner' | 'orchestrator' | 'redo' | 'message' | 'pr' | 'proof';

export interface WorkflowTimelineItem {
  key: string;
  atMs: number;
  kind: WorkflowTimelineKind;
  tone: 'good' | 'bad' | 'attention' | 'live' | null;
  /** The step it belongs to; clicking the item opens that step. */
  nodeId: string | null;
  attempt: number | null;
  /** The bold line. */
  title: string;
  /** A short muted detail after the time; may be empty. */
  detail: string;
  /** A review's or a command step's result for this round: pass or fail. */
  verdict: string | null;
  findingCount: number | null;
  receipt: AgentMessage['receipt'] | null;
  url: string | null;
  files: string[];
}

const RUN_EVENTS: Record<string, string> = {
  started: 'Run started',
  paused: 'Run paused',
  resumed: 'Run resumed',
  cancelled: 'Run stopped',
  'budget-exhausted': 'Run hit its budget'
};

const DECISIONS: Record<string, string> = { 'another-round': 'another round', accept: 'accept', stop: 'stop' };

const PR_URL = /https:\/\/github\.com\/([^/\s]+)\/([^/\s]+)\/pull\/(\d+)/;

const plural = (count: number, word: string): string => `${count} ${word}${count === 1 ? '' : 's'}`;

export function buildWorkflowTimeline(
  history: WorkflowHistoryRow[],
  messages: AgentMessage[],
  run: WorkflowRunRecord
): WorkflowTimelineItem[] {
  const title = (nodeId: string): string => run.definition.nodes.find((item) => item.id === nodeId)?.title || nodeId;
  const items: WorkflowTimelineItem[] = [];
  const add = (item: Partial<WorkflowTimelineItem> & Pick<WorkflowTimelineItem, 'key' | 'atMs' | 'kind' | 'title'>): void => {
    items.push({ tone: null, nodeId: null, attempt: null, detail: '', verdict: null, findingCount: null, receipt: null, url: null, files: [], ...item });
  };

  // Which step each agent session belongs to, so a message names steps, not session ids.
  const stepOf = new Map<string, string>();
  for (const node of run.nodes) stepOf.set(node.ownedId, node.nodeId);
  for (const entry of history) if (entry.ownedId && entry.nodeId) stepOf.set(entry.ownedId, entry.nodeId);
  // A review round that found something is kept only in the snapshot: `rounds[step][round - 1]` is its findings.
  const rounds = ((run.inputSnapshot as { rounds?: Record<string, unknown[][]> } | null)?.rounds ?? {});
  const checked = new Set(history.filter((entry) => entry.kind === 'workflow.node.commands-finished').map((entry) => `${entry.nodeId}:${entry.attempt}`));
  // A command step is told from the run definition, as the engine does: its `step.run` is present.
  const isCommandStep = (nodeId: string): boolean =>
    Boolean((run.definition.nodes.find((item) => item.id === nodeId) as { step?: { run?: unknown } } | undefined)?.step?.run);
  // A command step with `on_fail` writes no `commands-finished` row for a failed attempt; its last
  // `command-recorded` row holds that attempt's commands so far, which are all of them.
  const recorded = new Map<string, WorkflowHistoryRow['commands']>();
  for (const entry of history) if (entry.kind === 'workflow.node.command-recorded') recorded.set(`${entry.nodeId}:${entry.attempt}`, entry.commands);
  const commandsResult = (step: string, commands: WorkflowHistoryRow['commands'], findingCount: number | null) => {
    const failed = commands.filter((command) => command.exitCode !== 0);
    const total = commands.length;
    return { kind: 'checks' as const, tone: failed.length ? 'bad' as const : 'good' as const,
      title: failed.length ? `${step}: ${failed.length} of ${total} failed` : `${step}: ${total} of ${total} passed`,
      detail: failed[0]?.command ?? '', verdict: failed.length ? 'fail' : 'pass', findingCount };
  };
  const failedRound = (nodeId: string, round: number) => {
    const found = rounds[nodeId]?.[round - 1]?.length ?? 0;
    return { kind: 'review' as const, tone: 'bad' as const, attempt: round, title: `${title(nodeId)} round ${round}: fail`,
      detail: plural(found, 'finding'), verdict: 'fail', findingCount: found };
  };

  for (const entry of history) {
    const key = `history:${entry.sequence}`;
    const at = entry.timestampMs;
    const runEvent = entry.kind.startsWith('workflow.run.') ? RUN_EVENTS[entry.kind.slice('workflow.run.'.length)] : undefined;
    if (runEvent) { add({ key, atMs: at, kind: 'run', title: runEvent }); continue; }
    if (!entry.nodeId) continue;
    const base = { key, atMs: at, nodeId: entry.nodeId, attempt: entry.attempt };
    const step = title(entry.nodeId);
    switch (entry.kind) {
      case 'workflow.node.running':
        add({ ...base, kind: 'start', title: `${step} started` });
        break;
      case 'workflow.node.agent-finished':
        if (entry.verdict) {
          add({ ...base, kind: 'review', tone: entry.verdict === 'pass' ? 'good' : 'bad', title: `${step} round ${entry.attempt ?? 1}: ${entry.verdict}`,
            detail: plural(entry.findingCount ?? 0, 'finding'), verdict: entry.verdict, findingCount: entry.findingCount });
        } else {
          add({ ...base, kind: 'finish', tone: 'good', title: `${step} finished`, detail: entry.files.length ? plural(entry.files.length, 'file') : '' });
        }
        break;
      case 'workflow.node.commands-finished':
        add({ ...base, ...commandsResult(step, entry.commands, entry.findingCount) });
        break;
      case 'workflow.node.failed':
        add({ ...base, kind: 'failed', tone: 'bad', title: `${step} failed` });
        break;
      case 'workflow.node.timed-out':
        add({ ...base, kind: 'timeout', tone: 'attention', title: `${step} timed out` });
        break;
      case 'workflow.node.waiting-owner':
        add({ ...base, kind: 'waiting', tone: 'attention', title: `${step} is waiting for you` });
        break;
      case 'workflow.node.owner-answered': {
        const decision = entry.answer?.decision ?? '';
        const orchestrator = entry.answer?.by === 'orchestrator';
        add({ ...base, kind: orchestrator ? 'orchestrator' : 'owner', title: `${orchestrator ? 'Orchestrator' : 'You'} answered ${step}: ${DECISIONS[decision] ?? decision}`,
          detail: orchestrator ? entry.answer?.why ?? '' : entry.answer?.reply ? `“${entry.answer.reply}”` : '' });
        break;
      }
      case 'workflow.gate.decided':
        add({ ...base, kind: 'owner', title: `${entry.gateApproved ? 'You approved' : 'You declined'} ${step}` });
        break;
      case 'workflow.node.retried':
        add({ ...base, kind: 'owner', title: `You retried ${step}` });
        break;
      case 'workflow.node.findings':
        // The round that found something and stopped for the owner.
        if (isCommandStep(entry.nodeId)) add({ ...base, ...commandsResult(step, entry.commands, entry.findingCount) });
        else add({ ...base, ...failedRound(entry.nodeId, entry.attempt ?? 1) });
        break;
      case 'workflow.node.redo': {
        // The step was reset to its next attempt; the round that sent the work back is the one before.
        const round = (entry.attempt ?? 2) - 1;
        if (checked.has(`${entry.nodeId}:${round}`)) add({ ...base, kind: 'redo', title: `${step} sent the work back` });
        else if (isCommandStep(entry.nodeId)) {
          const commands = recorded.get(`${entry.nodeId}:${round}`) ?? [];
          add({ ...base, attempt: round, ...commandsResult(step, commands, commands.filter((command) => command.exitCode !== 0).length) });
        } else add({ ...base, ...failedRound(entry.nodeId, round) });
        break;
      }
    }
  }

  for (const sent of [...messages].reverse()) {
    const key = `message:${sent.id}`;
    // A step can also be addressed by its run id, `<runId>:<step>:<attempt>`.
    const target = sent.toOwnedId.startsWith(`${run.id}:`) ? sent.toOwnedId.slice(run.id.length + 1).split(':') : null;
    const fromStep = stepOf.get(sent.fromOwnedId);
    const toStep = stepOf.get(sent.toOwnedId) ?? target?.[0];
    const from = fromStep ? title(fromStep) : sent.fromOwnedId === `workflow:${run.id}` ? 'Workflow' : sent.senderName.replace(/ \[[^\]]*\]$/, '');
    add({ key, atMs: sent.createdAtMs, kind: 'message', nodeId: fromStep ?? toStep ?? null,
      title: `${from} → ${toStep ? title(toStep) : sent.receiver}`,
      // The workflow's own notes start with "[TSK-1 state]"; the row already says when and what.
      detail: sent.body.replace(/^\[[^\]]*\]\s*/, '').replace(/\s+/g, ' ').trim().slice(0, 200),
      receipt: sent.receipt });
  }

  // A snapshot item goes right after its step's last ledger row (the record's own finish time can be a little earlier).
  const endOf = (node: WorkflowRunRecord['nodes'][number]): number => Math.max(node.finishedAtMs ?? run.updatedAtMs,
    ...history.filter((entry) => entry.nodeId === node.nodeId).map((entry) => entry.timestampMs));

  const proof = run.nodes.find((node) => node.nodeId === 'proof');
  const proofFiles = (proof?.structuredOutput as { files?: unknown } | null)?.files;
  if (proof && Array.isArray(proofFiles) && proofFiles.length) {
    const cwd = (run.inputSnapshot as { cwd?: string } | null)?.cwd ?? '';
    const files = proofFiles.map(String).map((file) => (file.startsWith('/') || !cwd ? file : `${cwd}/${file}`));
    add({ key: 'proof', atMs: endOf(proof), kind: 'proof', nodeId: 'proof',
      title: `Proof: ${plural(files.length, 'file')}`, files });
  }

  const pr = run.nodes.find((node) => node.nodeId === 'pr');
  const prCommands = (pr?.structuredOutput as { commands?: { output?: unknown }[] } | null)?.commands;
  const match = Array.isArray(prCommands) ? prCommands.map((command) => PR_URL.exec(String(command.output ?? ''))).find(Boolean) : null;
  if (pr && match) {
    add({ key: 'pr', atMs: endOf(pr), kind: 'pr', tone: 'good', nodeId: 'pr',
      title: 'PR opened', detail: `${match[1]}/${match[2]} #${match[3]}`, url: match[0] });
  }

  // Stable: rows with the same time keep ledger, then message, then snapshot order.
  return items.sort((left, right) => left.atMs - right.atMs);
}
