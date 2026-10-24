// A prompt that came from another session (a helper's message to its parent, or any
// session's message) is labelled with that session instead of "You".
import type { Agent, Thread } from '../api';

export function senderLabel(sender: Thread, agents: Agent[]): string {
  const name = agents.find((a) => a.id === sender.agent)?.name ?? sender.agent;
  return `${sender.parentThreadId ? 'Helper' : 'Session'} · ${name}${sender.machine ? ` on ${sender.machine}` : ''}`;
}
