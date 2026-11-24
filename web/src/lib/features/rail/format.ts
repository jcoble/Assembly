/**
 * Pure formatting, presence, status, and list-entry helpers for the Left Sessions Rail.
 * Zero Svelte runes, zero DOM/socket dependencies.
 */

import type {
  PresenceKind,
  RailCursor,
  RailEntry,
  RailFilters,
  RailListItem,
  RailSort,
  RailDir,
  RailView,
  SessionMarkState
} from './types';
import { DEFAULT_RAIL_VIEW, EMPTY_RAIL_FILTERS } from './types';

export function isProjectDoneShelf(view: RailView): boolean {
  return view.groupBy.project && !view.groupBy.status;
}

/** Done starts folded in the Project-only view unless explicitly unfolded via `+done`. */
export function isSectionFolded(section: string, folded: readonly string[], view: RailView): boolean {
  if (section === 'done' && isProjectDoneShelf(view)) {
    return !folded.includes('+done');
  }
  return folded.includes(section);
}

/** Maps backend SQL rows (`section` / `project` / `session`) into typed render items. */
export function toRailListItems(
  entries: readonly RailEntry[],
  folded: readonly string[],
  view: RailView
): RailListItem[] {
  return entries.map((entry) => {
    if (entry.kind === 'section') {
      return {
        kind: 'section',
        key: entry.sessionId,
        foldKey: entry.section,
        label: entry.group,
        count: entry.count,
        folded: isSectionFolded(entry.section, folded, view)
      };
    }
    if (entry.kind === 'project') {
      const foldKey = entry.sessionId.startsWith('project:')
        ? entry.sessionId.slice('project:'.length)
        : entry.sessionId;
      return {
        kind: 'project',
        key: entry.sessionId,
        foldKey,
        label: entry.group,
        repoUrl: entry.repoUrl,
        machines: entry.machines ?? [],
        count: entry.count,
        folded: folded.includes(foldKey)
      };
    }
    return {
      kind: 'session',
      key: entry.sessionId,
      entry
    };
  });
}

/** Builds a backend SQL pagination cursor from a held `RailEntry` (matches `rail_page.rs`). */
export function cursorForEntry(entry: RailEntry, view: RailView): RailCursor {
  const inProject = view.groupBy.project && !entry.needsYou && entry.header !== 0;
  return {
    section: entry.sectionIndex,
    group: inProject ? entry.group : '',
    repoUrl: inProject ? entry.repoUrl : '',
    header: entry.header,
    ordering: entry.ordering
  };
}

export function providerDisplayName(agent: string): string {
  return agent
    .trim()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => `${part[0]?.toUpperCase() ?? ''}${part.slice(1)}`)
    .join(' ');
}

export function projectLabel(labelOrUrl: string): string {
  if (!labelOrUrl) return 'No project';
  if (labelOrUrl.startsWith('https://github.com/')) {
    return labelOrUrl.slice('https://github.com/'.length).replace(/\/+$/, '');
  }
  const trimmed = labelOrUrl.replace(/\/+$/, '');
  const parts = trimmed.split('/');
  return parts[parts.length - 1] || labelOrUrl;
}

export function projectInitials(label: string): string {
  const clean = projectLabel(label).trim();
  if (!clean || clean.toLowerCase() === 'no project') return '';
  const words = clean.split(/[-_/\s]+/).filter(Boolean);
  if (words.length >= 2) {
    return `${words[0][0] ?? ''}${words[1][0] ?? ''}`.toUpperCase();
  }
  // Handle CamelCase like ClipNSave -> CS or CN
  const caps = clean.match(/[A-Z]/g);
  if (caps && caps.length >= 2) {
    return `${caps[0]}${caps[caps.length - 1]}`.toUpperCase();
  }
  return clean.slice(0, 2).toUpperCase();
}

export function presenceOf(entry: RailEntry): PresenceKind {
  if (entry.statusKind === 'failed') return 'failed';
  if (entry.needsYou) return 'needs_you';
  if (entry.running) return 'working';
  if (entry.failed) return 'failed';
  if (entry.completedAt !== null && entry.completedAt !== undefined) return 'done';
  if (entry.unread) return 'unread';
  return 'idle';
}

export function markStateOf(entry: RailEntry): SessionMarkState {
  if (entry.section === 'settled') return 'settled';
  return entry.completedAt !== null && entry.completedAt !== undefined ? 'done' : 'working';
}

export function markMovesFor(entry: RailEntry): { label: string; to: SessionMarkState }[] {
  const state = markStateOf(entry);
  if (state === 'working') {
    return [
      { label: 'Mark done', to: 'done' },
      { label: 'Settle', to: 'settled' }
    ];
  }
  if (state === 'done') {
    return [
      { label: 'Settle', to: 'settled' },
      { label: 'Back to working', to: 'working' }
    ];
  }
  return [
    { label: 'Unsettle', to: 'done' },
    { label: 'Back to working', to: 'working' }
  ];
}

export function statusLineOf(entry: RailEntry): string {
  if (entry.statusKind === 'need') return entry.statusText?.trim() ?? '';
  if (entry.statusKind === 'step') {
    const name = entry.statusText?.trim();
    const reason = entry.statusReason?.trim().replace(/_/g, ' ');
    if (name && reason) return `${name}: ${reason}`;
    return name || reason || '';
  }
  if (entry.statusKind === 'failed') {
    return entry.lastError ? `Stopped: ${entry.lastError}` : 'Stopped';
  }
  if (entry.statusKind === 'subagents') {
    const total = entry.subagentCount ?? 0;
    const done = entry.subagentDone ?? 0;
    return `Coordinating ${total} ${total === 1 ? 'sub-agent' : 'sub-agents'}, ${done} done`;
  }
  return '';
}

export function sessionDetailsLine(entry: RailEntry, offline: boolean): string {
  const parts: string[] = [];
  if (offline) parts.push('offline');
  if (entry.handle) parts.push(entry.handle);
  parts.push(entry.machine ?? 'This Mac');
  const loc = entry.branch?.trim() || entry.worktree?.trim();
  if (loc) parts.push(loc);
  return parts.join(' · ');
}

function toEpochMs(raw: number): number {
  return raw > 0 && raw < 1e12 ? raw * 1000 : raw;
}

export function formatRelativeAge(nowMs: number, updatedAt: number): string {
  const diffSec = Math.max(0, Math.floor((nowMs - toEpochMs(updatedAt)) / 1000));
  if (diffSec < 60) return 'now';
  const mins = Math.floor(diffSec / 60);
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 365) return `${days}d`;
  return `${Math.floor(days / 365)}y`;
}

export function formatElapsed(nowMs: number, startedAt: number): string {
  const diffSec = Math.max(0, Math.floor((nowMs - toEpochMs(startedAt)) / 1000));
  if (diffSec < 60) return `${diffSec}s`;
  const mins = Math.floor(diffSec / 60);
  const secs = diffSec % 60;
  if (mins < 60) return `${mins}m ${secs}s`;
  const hours = Math.floor(mins / 60);
  const remMins = mins % 60;
  return `${hours}h ${remMins}m`;
}

export function sessionDeepLink(sessionId: string, machine?: string | null): string {
  const base = `assembly://session/${encodeURIComponent(sessionId)}`;
  return machine ? `${base}?machine=${encodeURIComponent(machine)}` : base;
}

export function naturalDirForSort(sort: RailSort): RailDir {
  return sort === 'name' ? 'asc' : 'desc';
}

export function sortDirectionLabel(sort: RailSort, dir: RailDir): string {
  if (sort === 'name') return dir === 'asc' ? 'A to Z' : 'Z to A';
  return dir === 'desc' ? 'Newest first' : 'Oldest first';
}

export function normalizeRailView(raw: unknown): RailView {
  if (!raw || typeof raw !== 'object') return { ...DEFAULT_RAIL_VIEW, groupBy: { ...DEFAULT_RAIL_VIEW.groupBy } };
  const obj = raw as Record<string, unknown>;
  const gb = (obj.groupBy && typeof obj.groupBy === 'object' ? obj.groupBy : {}) as Record<string, unknown>;
  const sort: RailSort = obj.sort === 'name' ? 'name' : 'recent';
  const dir: RailDir = obj.dir === 'asc' || obj.dir === 'desc' ? obj.dir : naturalDirForSort(sort);
  return {
    groupBy: {
      status: typeof gb.status === 'boolean' ? gb.status : DEFAULT_RAIL_VIEW.groupBy.status,
      project: typeof gb.project === 'boolean' ? gb.project : DEFAULT_RAIL_VIEW.groupBy.project
    },
    sort,
    dir
  };
}

export function normalizeRailFilters(raw: unknown): RailFilters {
  if (!raw || typeof raw !== 'object') return { provider: [], status: [], location: [] };
  const obj = raw as Record<string, unknown>;
  const provider = Array.isArray(obj.provider)
    ? obj.provider.filter((v): v is string => typeof v === 'string' && v.length > 0)
    : [];
  const status = Array.isArray(obj.status)
    ? obj.status.filter((v): v is SessionMarkState => v === 'working' || v === 'done' || v === 'settled')
    : [];
  const location = Array.isArray(obj.location)
    ? obj.location.filter((v): v is 'local' | 'remote' => v === 'local' || v === 'remote')
    : [];
  return {
    provider: [...new Set(provider)],
    status: [...new Set(status)],
    location: [...new Set(location)]
  };
}

export function hasActiveFilters(filters: RailFilters): boolean {
  return filters.provider.length > 0 || filters.status.length > 0 || filters.location.length > 0;
}

export { EMPTY_RAIL_FILTERS };
