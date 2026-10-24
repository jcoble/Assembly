/**
 * The project registry as the front end holds it: a copy of the SQLite
 * `projects` table, read when a screen that needs it opens. It persists
 * nothing itself and never polls.
 */

import {
  addProjectFromTauri,
  listAgentConversationSessionsFromTauri,
  listProjectsFromTauri,
  type ProjectRecord,
  type ProjectStart
} from '../../tauriSource.ts';
import { updateOwnedSession } from '../stores/sessionRailStore.svelte.ts';

export const projectRegistry = $state<{ projects: ProjectRecord[]; error: string }>({
  projects: [],
  error: ''
});

/** Reads the registry from SQLite once per call. */
export async function hydrateProjects(): Promise<void> {
  try {
    projectRegistry.projects = await listProjectsFromTauri();
    projectRegistry.error = '';
  } catch (error) {
    projectRegistry.error = error instanceof Error ? error.message : String(error);
  }
}

/**
 * Inspects and registers a folder, made first when `create` is set; adding the same folder again returns the same project.
 * A local add also files older local sessions under it in SQL, so the rail takes their new groups.
 */
export async function addProject(machine: string, path: string, create: boolean, start?: ProjectStart): Promise<ProjectRecord> {
  const project = await addProjectFromTauri(machine, path, create, start);
  if (!projectRegistry.projects.some((existing) => existing.id === project.id)) {
    projectRegistry.projects = [...projectRegistry.projects, project];
  }
  if (machine === 'local') await refreshRailProjects();
  return project;
}

/** Gives every rail session the project, group and label the session list now has, in one read. */
export async function refreshRailProjects(): Promise<void> {
  for (const record of (await listAgentConversationSessionsFromTauri()) ?? []) {
    updateOwnedSession(record.ownedId, {
      projectId: record.projectId,
      projectGroupKey: record.projectGroupKey,
      projectGroupLabel: record.projectGroupLabel
    });
  }
}

/** Moves only the desktop record; the agent keeps its working folder. */
export async function moveSessionProject(ownedId: string, projectId: string): Promise<void> {
  const { invoke } = await import('../../workspaceInvoke.ts');
  await invoke('set_session_project', { ownedId, projectId });
  await refreshRailProjects();
}
