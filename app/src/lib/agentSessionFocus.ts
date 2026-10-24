import type { WorkspaceSnapshotRestoreReadiness } from './workspaceSnapshot.ts';

export type AgentSessionFocusSession = {
  provider: string;
  id: string;
  title: string;
  model: string | null;
  projectPath: string | null;
  lastActivity: string | null;
  resumeCommands: string[];
};

export type AgentSessionFocusAction =
  | 'reattach'
  | 'repair'
  | 'restore'
  | 'resume'
  | 'files-only'
  | 'save-workspace';

export type AgentSessionFocusTone = 'ready' | 'warning' | 'blocked' | 'neutral';

export type AgentSessionFocusLane = {
  action: AgentSessionFocusAction;
  label: string;
  tone: AgentSessionFocusTone;
  title: string; 
  detail: string;
};

export function agentSessionFocusLane(
  session: AgentSessionFocusSession,
  readiness: WorkspaceSnapshotRestoreReadiness | null
): AgentSessionFocusLane {
  if (readiness?.kind === 'live-terminal') {
    return {
      action: 'reattach',
      label: 'Reattach',
      tone: 'ready',
      title: 'Reattach saved terminal',
      detail: readiness.detail
    };
  }

  if (readiness?.kind === 'missing-worktree') {
    return {
      action: 'repair',
      label: 'Repair',
      tone: 'blocked',
      title: readiness.repairLabel ?? 'Copy repair plan',
      detail: readiness.detail
    };
  }

  if (readiness?.kind === 'ready') {
    return {
      action: 'restore',
      label: 'Restore',
      tone: 'ready',
      title: 'Restore saved workspace',
      detail: readiness.detail
    };
  }

  if (readiness?.kind === 'needs-terminal') {
    return {
      action: 'resume',
      label: 'Resume',
      tone: 'warning',
      title: 'Start terminal and resume',
      detail: readiness.detail
    };
  }

  if (readiness?.kind === 'files-only') {
    return {
      action: 'files-only',
      label: 'Files',
      tone: 'neutral',
      title: 'Restore files only',
      detail: readiness.detail
    };
  }

  if (agentSessionHasResumeCommand(session)) {
    return {
      action: 'resume',
      label: 'Resume',
      tone: 'warning',
      title: 'Resume session without saved workspace',
      detail: session.projectPath?.trim() || 'No saved workspace'
    };
  }

  return {
    action: 'save-workspace',
    label: 'Save',
    tone: 'neutral',
    title: 'Save workspace before switching',
    detail: 'No saved workspace or resume command'
  };
}

function agentSessionHasResumeCommand(session: AgentSessionFocusSession) {
  return session.resumeCommands.some((command) => command.trim().length > 0);
}
