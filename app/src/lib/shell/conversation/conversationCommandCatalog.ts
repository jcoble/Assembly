import type { AgentCommandDescriptor } from './conversationTypes.ts';

export type ConversationCommandSource = 'provider' | 'skill' | 'assembly';
export type ConversationCommandAction = 'insert' | 'invoke';

export interface ConversationCommand extends AgentCommandDescriptor {
  name: string;
  source: ConversationCommandSource;
  action: ConversationCommandAction;
}

export interface AssemblyConversationCommand {
  id: string;
  label: string;
  description?: string;
  name?: string;
  action?: ConversationCommandAction;
}

/** Local assembly settings and debug rows do not belong in the structured slash menu. */
export const TUI_ONLY_COMMAND_NAMES = new Set([
  'theme',
  'themes',
  'keymap',
  'keys',
  'pet',
  'pets',
  'terminal-title',
  'title',
  'statusline',
  'vim',
  'vim-mode',
  'raw-terminal',
  'handoff',
  'debug',
  'model',
  'effort',
  'thought-level',
  'mode',
  'permissions',
  'approval-policy',
  'fast',
  'service-tier'
]);

export const DEFAULT_ASSEMBLY_CONVERSATION_COMMANDS: readonly AssemblyConversationCommand[] = [
  { id: 'assembly-terminal', name: 'terminal', label: 'Open terminal', description: 'Open the native terminal for this session', action: 'invoke' },
  { id: 'assembly-conversation', name: 'conversation', label: 'Open conversation', description: 'Return to the structured conversation', action: 'invoke' },
  // These rows insert their local action prefix so the existing shell can
  // collect any arguments before an eventual command-bus receipt invokes it.
  { id: 'assembly-new-worktree', name: 'new-worktree', label: 'New worktree', description: 'Create a worktree for the next task', action: 'insert' },
  { id: 'assembly-open-file', name: 'open-file', label: 'Open file', description: 'Open a file in the editor', action: 'insert' },
  { id: 'assembly-rename-session', name: 'rename-session', label: 'Rename session', description: 'Rename this owned session', action: 'insert' },
  { id: 'assembly-archive-session', name: 'archive-session', label: 'Archive session', description: 'Archive this session from active work', action: 'insert' }
];

function commandName(value: string): string {
  const trimmed = value.trim().replace(/^\/+/, '');
  return trimmed.split(/\s+/, 1)[0].toLowerCase();
}

export function isExcludedConversationCommand(value: string): boolean {
  const name = commandName(value);
  return !name || TUI_ONLY_COMMAND_NAMES.has(name);
}

function providerCommand(command: AgentCommandDescriptor, source: 'provider' | 'skill'): ConversationCommand | null {
  const name = commandName(command.id || command.label);
  if (!name) return null;
  return {
    ...command,
    name,
    source,
    action: 'insert'
  };
}

function assemblyCommand(command: AssemblyConversationCommand): ConversationCommand | null {
  const name = commandName(command.name ?? command.id);
  if (isExcludedConversationCommand(name)) return null;
  return {
    id: command.id,
    label: command.label,
    description: command.description,
    name,
    source: 'assembly',
    action: command.action ?? 'invoke'
  };
}

/** Merge provider commands/skills with Assembly actions, preserving source order and ids. */
export function mergeConversationCommandCatalog(
  providerCommands: readonly AgentCommandDescriptor[] = [],
  providerSkills: readonly AgentCommandDescriptor[] = [],
  assemblyCommands: readonly AssemblyConversationCommand[] = DEFAULT_ASSEMBLY_CONVERSATION_COMMANDS
): ConversationCommand[] {
  const merged: ConversationCommand[] = [];
  const seen = new Set<string>();
  const add = (command: ConversationCommand | null): void => {
    if (!command || seen.has(command.name)) return;
    seen.add(command.name);
    merged.push(command);
  };
  providerCommands.forEach((command) => add(providerCommand(command, 'provider')));
  providerSkills.forEach((command) => add(providerCommand(command, 'skill')));
  assemblyCommands.forEach((command) => add(assemblyCommand(command)));
  return merged;
}

export function filterConversationCommandCatalog(
  catalog: readonly ConversationCommand[],
  query: string
): ConversationCommand[] {
  const normalized = query.trim().replace(/^\/+/, '').toLowerCase();
  if (!normalized) return [...catalog];
  return catalog.filter((command) => {
    const haystack = `${command.name} ${command.label} ${command.description ?? ''}`.toLowerCase();
    return haystack.includes(normalized);
  });
}

export function slashName(command: ConversationCommand): string {
  return `/${command.name}`;
}
