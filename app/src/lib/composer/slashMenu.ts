// The "/" menu: the agent's own slash commands, as it last listed them (ACP
// availableCommands). A pick only fills the draft; it is sent as a normal prompt.
// Assembly's slashCommandQuery and slashMenuState (composerSlashCommands.ts). Plain functions, for node tests.

export type SlashCommand = { name: string; description: string; input?: { hint?: string } | null };

// The command word being typed, or null once the draft isn't one: it must start
// with "/", or "$" for Codex's skills (named "$skill"), and the menu closes when
// arguments begin. A "$" stays in the query, so it matches those names from their start.
export function slashQuery(draft: string): string | null {
  if (!draft.startsWith('/') && !draft.startsWith('$')) return null;
  const rest = draft.startsWith('/') ? draft.slice(1) : draft;
  return /\s/.test(rest) ? null : rest;
}

// Names starting with the query first, then names containing it; "__" names are the agent's own.
export function slashMatches(commands: SlashCommand[], query: string): SlashCommand[] {
  const q = query.toLowerCase();
  const shown = commands.filter((command) => !command.name.startsWith('__'));
  const starts = shown.filter((command) => command.name.toLowerCase().startsWith(q));
  const contains = shown.filter((command) => !command.name.toLowerCase().startsWith(q) && command.name.toLowerCase().includes(q));
  return [...starts, ...contains];
}

// The draft after a pick, and the part to select: the input hint, so typing replaces it.
// A "$" name is a Codex skill mention and goes in as is, with no slash.
export function slashPick(command: SlashCommand): { draft: string; select: [number, number] } {
  const head = `${command.name.startsWith('$') ? '' : '/'}${command.name} `;
  const hint = command.input?.hint ?? '';
  return { draft: head + hint, select: [head.length, head.length + hint.length] };
}
