/**
 * One spelling per project, so the same folder written two ways is one entry.
 * Matches the backend's active-root key.
 */
export function workspaceKey(root: string): string {
  const trimmed = root.trim();
  if (trimmed.length <= 1) return trimmed;
  return trimmed.replace(/\/+$/, '');
}
