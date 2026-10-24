/** Lowercase letters only: `mac-command-bar` and `MacCommandBar` both become `maccommandbar`. */
function letters(value: string): string {
  return value.toLowerCase().replace(/[^a-z]/g, '');
}

/**
 * The Notion project a session's folder belongs to, or '' for every project.
 * A linked git worktree (`…/worktrees/<repo>/<branch>`) belongs to the repo
 * folder it was cut from, not its lane folder. An exact letters-only match
 * wins; otherwise the project whose name starts with the folder's first word
 * (`rental-management` → `Rental Command`).
 */
export function matchNotionProject(root: string, projects: readonly string[]): string {
  const parts = root.split('/').filter(Boolean);
  const worktrees = parts.lastIndexOf('worktrees');
  const folder = (worktrees >= 0 && parts[worktrees + 2] ? parts[worktrees + 1] : parts.at(-1)) ?? '';
  const whole = letters(folder);
  if (!whole) return '';
  const exact = projects.find((project) => letters(project) === whole);
  if (exact) return exact;
  const firstWord = letters(folder.split(/[-_]|(?<=[a-z])(?=[A-Z])/)[0] ?? '');
  if (!firstWord) return '';
  return projects.find((project) => letters(project).startsWith(firstWord)) ?? '';
}
