/**
 * Pure domain types shared across Assembly/web.
 * No Svelte runes, no WebSockets, no DOM dependencies.
 */

export interface SessionKey {
  machine: string;
  id: string;
}

export function sessionKeyId(key: SessionKey): string {
  return `${key.machine}::${key.id}`;
}

export type EngineTopic =
  | 'runs'
  | 'sessions'
  | 'needs_me'
  | 'messages'
  | 'prs'
  | 'usage'
  | 'providers'
  | 'mirror';

export interface EngineKey {
  machine: string;
  kind: string;
  id: string;
}

export function engineKeyId(key: EngineKey): string {
  return `${key.machine}::${key.kind}::${key.id}`;
}

export interface EngineScope {
  machine: string;
  topic: EngineTopic;
  key: string | null;
}

export function engineScopeId(scope: EngineScope): string {
  return `${scope.machine}::${scope.topic}::${scope.key ?? '*'}`;
}

export interface CursorAt {
  kind: string;
  id: string;
  version: number;
}

export interface EngineCursor {
  through: number;
  at: CursorAt | null;
}

export interface EngineItem<T = Record<string, unknown>> {
  key: EngineKey;
  version: number;
  value: T;
}

export interface EngineEvent {
  machine: string;
  seq: number;
  entityKind: string;
  entityId: string;
  version: number;
  [field: string]: unknown;
}

export interface SessionSummary {
  id: string;
  title?: string | null;
  root?: string | null;
  project?: string | null;
  machine?: string | null;
  provider?: string | null;
  status?: string | null;
  updatedAt?: string | number | null;
  branch?: string | null;
}

export interface MachineSummary {
  name: string;
  engine?: string | null;
  status?: string | null;
  connected?: boolean;
  local?: boolean;
  version?: string | null;
}

export interface SlotRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export type SecondaryTabKind = 'browser' | 'changes' | 'source_history' | 'prs' | 'editor';

export interface SecondaryTab {
  id: string;
  kind: SecondaryTabKind;
  title: string;
  filePath?: string;
}

export type RightCoreTabId = 'files' | 'git' | 'agents' | 'tasks';
export type RightOverflowTabId = 'worktrees' | 'terminal' | 'run' | 'context' | 'history';
export type RightPanelTabId = RightCoreTabId | RightOverflowTabId;

export const RIGHT_CORE_TABS: readonly RightCoreTabId[] = [
  'files',
  'git',
  'agents',
  'tasks'
] as const;

export const RIGHT_OVERFLOW_TABS: readonly RightOverflowTabId[] = [
  'worktrees',
  'terminal',
  'run',
  'context',
  'history'
] as const;

export type ThemeMode = 'glass' | 'assembly' | 'houston';
