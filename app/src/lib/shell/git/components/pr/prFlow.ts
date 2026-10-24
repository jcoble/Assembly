export type PullRequestFlowState = 'idle' | 'generating' | 'ready' | 'pushing' | 'created' | 'failed';

export type PullRequestChecks = 'none' | 'pending' | 'passing' | 'failing' | string;

export interface PullRequestFlowModel {
  state: PullRequestFlowState;
  branch: string;
  base: string;
  title: string;
  description: string;
  draft: boolean;
  number: number | null;
  url: string;
  checks: PullRequestChecks;
  checkSummary: string;
  error: string;
}

export function createPullRequestFlowModel(): PullRequestFlowModel {
  return {
    state: 'idle',
    branch: '',
    base: 'main',
    title: '',
    description: '',
    draft: false,
    number: null,
    url: '',
    checks: 'none',
    checkSummary: '',
    error: ''
  };
}

export function setPullRequestContext(
  model: PullRequestFlowModel,
  input: { branch: string; base: string }
): PullRequestFlowModel {
  return { ...model, branch: input.branch, base: input.base || 'main', error: '' };
}

export function beginPullRequestGeneration(model: PullRequestFlowModel): PullRequestFlowModel {
  return { ...model, state: 'generating', error: '' };
}

export function finishPullRequestGeneration(
  model: PullRequestFlowModel,
  details: { title: string; description: string }
): PullRequestFlowModel {
  return {
    ...model,
    state: 'ready',
    title: details.title.trim(),
    description: details.description.trim(),
    error: ''
  };
}

export function beginPullRequestPush(model: PullRequestFlowModel): PullRequestFlowModel {
  return { ...model, state: 'pushing', error: '' };
}

export function finishPullRequestCreation(
  model: PullRequestFlowModel,
  created: { number: number; url: string }
): PullRequestFlowModel {
  return {
    ...model,
    state: 'created',
    number: created.number,
    url: created.url,
    error: ''
  };
}

export function setPullRequestChecks(
  model: PullRequestFlowModel,
  status: { number: number; url: string; state: string; checks: PullRequestChecks; checkSummary: string }
): PullRequestFlowModel {
  return {
    ...model,
    state: 'created',
    number: status.number,
    url: status.url || model.url,
    checks: status.checks,
    checkSummary: status.checkSummary,
    error: ''
  };
}

export function failPullRequest(model: PullRequestFlowModel, error: unknown): PullRequestFlowModel {
  const message = error instanceof Error ? error.message : String(error ?? 'Pull request action failed');
  return { ...model, state: 'failed', error: message.trim() || 'Pull request action failed' };
}

export function pullRequestActionLabel(model: PullRequestFlowModel): string {
  if (model.state === 'generating') return 'Generating title & description...';
  if (model.state === 'pushing') return 'Pushing & creating…';
  if (model.state === 'created') return `PR #${model.number ?? ''}`.trim();
  return 'Push & Create PR';
}

export function canCreatePullRequest(model: PullRequestFlowModel, canWrite: boolean): boolean {
  return (
    canWrite &&
    model.state === 'ready' &&
    model.branch.trim() !== '' &&
    model.base.trim() !== '' &&
    model.title.trim() !== ''
  );
}

export function noAgentMessage(): string {
  return 'No active agent session is running. Start an agent conversation to generate this text.';
}

export function noGhMessage(): string {
  return 'GitHub CLI (`gh`) is not installed or not on PATH';
}
