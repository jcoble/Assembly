/**
 * The stage close-up of a review group or the proof step.
 *
 * A review group is every review step that sends work back to the same step
 * (`review.of`). Each reviewer's rounds come from the run's stored review
 * reports (`report_review`, one per attempt, read with the `reviews`
 * operation); the cap decisions from the answers in the run's snapshot. The
 * proof step shows what it reported: its summary and its files.
 */
import { remoteWorkspacePath } from '../../workspacePaths.ts';
import type { WorkflowNodeRunRecord, WorkflowRunRecord } from './workflowTypes.ts';

/** One stored `report_review` call; `attemptId` is `<run id>:<step>:<attempt>`. */
export interface WorkflowReviewReport {
  attemptId: string;
  report: unknown;
  atMs: number;
}

export interface CloseupFinding {
  /** The reviewer's own tag, e.g. "P1" from "[P1] …"; null when it wrote none. */
  severity: string | null;
  location: string | null;
  text: string;
}

export interface CloseupRound {
  round: number;
  /** pass or fail; null while the reviewer has not reported this round. */
  verdict: string | null;
  findings: CloseupFinding[];
  atMs: number | null;
}

export interface CloseupReviewer {
  nodeId: string;
  title: string;
  rounds: CloseupRound[];
}

export interface CloseupDecision {
  step: string;
  decision: string;
  by: string;
  why: string | null;
  reply: string | null;
}

type StepDefinition = { id: string; title?: string; step?: Record<string, unknown> };

const definitions = (run: WorkflowRunRecord): StepDefinition[] => run.definition.nodes as StepDefinition[];
const reviewOf = (definition: StepDefinition | undefined): string | null => {
  const of = (definition?.step?.review as { of?: unknown } | undefined)?.of;
  return typeof of === 'string' ? of : null;
};

export function closeupKind(run: WorkflowRunRecord, nodeId: string): 'review' | 'proof' | null {
  const definition = definitions(run).find((item) => item.id === nodeId);
  if (!definition) return null;
  if (reviewOf(definition)) return 'review';
  return nodeId === 'proof' ? 'proof' : null;
}

/** The review steps that send work back to the same step as `nodeId`, in the workflow's order. */
export function reviewGroup(run: WorkflowRunRecord, nodeId: string): string[] {
  const target = reviewOf(definitions(run).find((item) => item.id === nodeId));
  return target ? definitions(run).filter((item) => reviewOf(item) === target).map((item) => item.id) : [];
}

/** "[P1] src/a.ts (why): text" or "src/a.ts:3 — text" → its tag, its location (a leading path, with a note in brackets) and the rest. */
export function parseFinding(raw: unknown): CloseupFinding {
  let text = typeof raw === 'string' ? raw : JSON.stringify(raw);
  const tag = /^\[([^\]]+)\]\s*/.exec(text);
  if (tag) text = text.slice(tag[0].length);
  const place = /^(\S*[/.]\S*?(?: \([^)]*\))?)(?::| —)\s+/.exec(text);
  return { severity: tag?.[1] ?? null, location: place?.[1] ?? null, text: place ? text.slice(place[0].length) : text };
}

/** Each reviewer's rounds, oldest first; the round in progress shows with no verdict until it reports. */
export function reviewRounds(run: WorkflowRunRecord, nodeIds: string[], reports: WorkflowReviewReport[]): CloseupReviewer[] {
  return nodeIds.map((nodeId) => {
    const prefix = `${run.id}:${nodeId}:`;
    const rounds: CloseupRound[] = reports.filter((item) => item.attemptId.startsWith(prefix)).map((item) => {
      const report = (item.report ?? {}) as { verdict?: unknown; findings?: unknown };
      return {
        round: Number(item.attemptId.slice(prefix.length)),
        verdict: typeof report.verdict === 'string' ? report.verdict : null,
        findings: Array.isArray(report.findings) ? report.findings.map(parseFinding) : [],
        atMs: item.atMs
      };
    });
    const node = run.nodes.find((item) => item.nodeId === nodeId);
    if (node?.startedAtMs && !rounds.some((round) => round.round === node.attempt)) {
      rounds.push({ round: node.attempt, verdict: null, findings: [], atMs: null });
    }
    return { nodeId, title: definitions(run).find((item) => item.id === nodeId)?.title || nodeId, rounds };
  });
}

/** Every answer to a waiting step of the group, oldest first, with who gave it and why. */
export function capDecisions(run: WorkflowRunRecord, nodeIds: string[]): CloseupDecision[] {
  const answers = (run.inputSnapshot as { answers?: Partial<CloseupDecision>[] } | null)?.answers ?? [];
  return answers.filter((answer) => nodeIds.includes(answer.step ?? '')).map((answer) => ({
    step: answer.step ?? '', decision: answer.decision ?? '', by: answer.by ?? 'owner', why: answer.why ?? null, reply: answer.reply ?? null
  }));
}

/** What the proof step reported: its summary and its files, as absolute paths. */
export function proofEvidence(run: WorkflowRunRecord, node: WorkflowNodeRunRecord): { summary: string; files: string[] } {
  const output = (node.structuredOutput ?? {}) as { summary?: unknown; files?: unknown };
  const cwd = (run.inputSnapshot as { cwd?: string } | null)?.cwd ?? '';
  const files = Array.isArray(output.files) ? output.files.map(String) : [];
  return {
    summary: typeof output.summary === 'string' ? output.summary : '',
    files: files.map((file) => (file.startsWith('/') || !cwd ? file : `${cwd}/${file}`))
  };
}

/** The proof images to read, each as a path on the machine that ran the run (`machine`; null for this Mac). */
export function proofImagePaths(files: string[], machine: string | null): string[] {
  const images = files.filter((file) => /\.(png|jpe?g|gif|webp)$/i.test(file) && file.startsWith('/'));
  return machine ? images.map((file) => remoteWorkspacePath(machine, file)) : images;
}
