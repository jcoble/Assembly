/**
 * One glyph per kind of agent, and the name to put beside it.
 *
 * The collapsed session strip and the rail rows both mark a session with the
 * same small icon, so the mapping lives here rather than in either component.
 * The name is the provider id read back as words, so adding a provider needs
 * no new string here.
 */
import type { Component } from 'svelte';

import Gem from '@lucide/svelte/icons/gem';
import SquareTerminal from '@lucide/svelte/icons/square-terminal';
import Terminal from '@lucide/svelte/icons/terminal';

import AntigravityMark from './icons/AntigravityMark.svelte';
import ClaudeMark from './icons/ClaudeMark.svelte';
import CodexMark from './icons/CodexMark.svelte';
import type { AgentKind } from './ownedSessions.ts';

/** What every glyph here accepts, Lucide's and the brand marks' alike. */
type AgentIcon = Component<{
  class?: string;
  'aria-hidden'?: boolean | 'true' | 'false';
  'aria-label'?: string;
}>;

/**
 * The three providers a person actually runs wear their own marks, taken from
 * each vendor's published asset — see the note at the top of each component
 * for where it came from. The rest keep a Lucide glyph: no official mark was
 * needed for them, and a stand-in is honest where a real one is not available.
 */
export const AGENT_ICONS: Record<AgentKind, AgentIcon> = {
  claude: ClaudeMark as AgentIcon,
  codex: CodexMark as AgentIcon,
  antigravity: AntigravityMark as AgentIcon,
  gemini: Gem as AgentIcon,
  opencode: SquareTerminal as AgentIcon,
  other: Terminal as AgentIcon
};

/** What to call this kind of session in a tooltip or a detail line. */
export function agentDisplayName(agent: AgentKind, viaCmux = false): string {
  const name = agent
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => `${part[0]?.toUpperCase() ?? ''}${part.slice(1)}`)
    .join(' ');
  const base = agent === 'other' ? 'Terminal session' : name;
  return viaCmux ? `${base} (multiplexed)` : base;
}
