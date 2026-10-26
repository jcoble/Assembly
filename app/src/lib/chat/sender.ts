// A prompt that came from another session (a helper's message to its parent, or any
// session's message) is labelled with that session instead of "You".
import type { Agent, Session } from '../api';

export function senderLabel(sender: Session, agents: Agent[]): string {
  const name = agents.find((a) => a.id === sender.agent)?.name ?? sender.agent;
  return `${sender.parentSessionId ? 'Helper' : 'Session'} · ${name}${sender.machine ? ` on ${sender.machine}` : ''}`;
}
