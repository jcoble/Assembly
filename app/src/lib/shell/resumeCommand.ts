/**
 * The terminal command that resumes a session in its provider's own CLI, using
 * the id the provider knows it by. Null when there is no id, or when the agent
 * has no command we know of.
 */
export function resumeCommand(agent: string, nativeSessionId: string | null, cwd: string): string | null {
  const id = nativeSessionId?.trim();
  const command = !id ? null : agent === 'claude' ? `claude --resume ${id}` : agent === 'codex' ? `codex resume ${id}` : agent === 'antigravity' ? `agy --conversation ${id}` : null;
  if (!command || !cwd.trim()) return command;
  // Quote the folder only when the shell would otherwise split or expand it.
  const folder = /^[\w@%+=:,./-]+$/.test(cwd) ? cwd : `'${cwd.replaceAll("'", "'\\''")}'`;
  return `cd ${folder} && ${command}`;
}
