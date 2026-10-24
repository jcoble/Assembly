export type WorkspaceFileChange = {
  ownedId: string;
  path: string;
  recursive?: boolean;
};

type WorkspaceFileChangeListener = (change: WorkspaceFileChange) => void;

const listeners = new Set<WorkspaceFileChangeListener>();

export function publishWorkspaceFileChange(change: WorkspaceFileChange): void {
  if (!change.ownedId.trim() || !change.path.trim()) return;
  for (const listener of listeners) listener(change);
}

export function onWorkspaceFileChange(listener: WorkspaceFileChangeListener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
