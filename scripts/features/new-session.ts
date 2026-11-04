// A session starts in a project rather than at a folder, so a scenario adds its repo first, as the new session form does.
import { randomUUID } from 'node:crypto';

type Caller = { call(feature: string, method: string, args: object): Promise<any> };

// machine is null for this machine. A project already added comes back as it is.
export async function newSession(owner: Caller, agent: string, repo: string, machine: string | null): Promise<{ sessionId: string }> {
  const added = await owner.call('projects', 'add', { machine: machine ?? 'local', path: repo });
  return owner.call('sessions', 'create', {
    agent, clientId: randomUUID(), projectId: added.project.id, ...(machine === null ? {} : { machine })
  });
}
