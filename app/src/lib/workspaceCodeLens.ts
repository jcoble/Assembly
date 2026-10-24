/**
 * The fixed .NET actions CodeMirror exposes above a C# file.
 *
 * This is intentionally a tiny, pure module. The editor decides which lens was
 * clicked; the shell turns the returned request into an ordinary owned terminal
 * session. No arbitrary command crosses the editor/backend boundary.
 */

export type DotnetWorkspaceAction = 'build' | 'test';

export interface WorkspaceCommandSessionRequest {
  id: string;
  cwd: string;
  script: string;
  title: string;
}

export const dotnetWorkspaceCommandIds: Record<DotnetWorkspaceAction, string> = {
  build: 'mcb.source.dotnetBuild',
  test: 'mcb.source.dotnetTest'
};

export const dotnetWorkspaceLensTitles: Record<DotnetWorkspaceAction, string> = {
  build: 'Build workspace',
  test: 'Test workspace'
};

const dotnetWorkspaceScripts: Record<DotnetWorkspaceAction, string> = {
  build: 'dotnet build --nologo',
  test: 'dotnet test --nologo'
};

function workspaceName(root: string): string {
  const normalized = root.replaceAll('\\', '/').replace(/\/+$/, '');
  return normalized.split('/').filter(Boolean).at(-1) ?? 'workspace';
}

/**
 * Describe one fixed command as the session rail expects it.
 *
 * The project path affects only the working directory and label. It is never
 * interpolated into the shell command, so spaces and shell metacharacters in a
 * folder name cannot change what runs.
 */
export function dotnetWorkspaceSessionRequest(
  action: DotnetWorkspaceAction,
  projectRoot: string
): WorkspaceCommandSessionRequest {
  const cwd = projectRoot.trim();
  if (!cwd) {
    throw new Error('A .NET workspace needs a project folder.');
  }

  const name = workspaceName(cwd);
  return {
    id: `dotnet-${action}:${cwd}`,
    cwd,
    script: dotnetWorkspaceScripts[action],
    title: `${action === 'build' ? 'Build' : 'Test'} ${name}`
  };
}
