// Desktop notifications for the Build Board: one for each card that enters Needs you and each new or re-asked question.
// The body is what the owner must do: the card's first step, else its summary or note, or the question.

import type { SessionNotification } from '../conversation/sessionNotifications.ts';
import type { MachineBoard } from './boardSource.ts';

const notification = (title: string, body: string): SessionNotification =>
  ({ title: `Build Board · ${title}`, body: [...body].slice(0, 120).join('') });

/** What changed from `previous` to `next` that the owner should hear about. A machine missing from
 *  `previous` (the first read, or one that just connected) only sets the baseline, so nothing repeats. */
export function boardNotifications(previous: MachineBoard[] | null, next: MachineBoard[]): SessionNotification[] {
  const sent: SessionNotification[] = [];
  for (const { machine, value } of next) {
    const before = previous?.find((board) => board.machine === machine)?.value;
    if (!before) continue;
    for (const card of value.cards) {
      if (card.state !== 'needs_you') continue;
      if (before.cards.some((old) => old.key === card.key && old.state === 'needs_you')) continue;
      sent.push(notification(card.title, card.steps[0] || card.summary || (card.note ?? '')));
    }
    for (const question of value.questions ?? []) {
      if (question.answered_at_ms !== null) continue;
      if ((before.questions ?? []).some((old) => old.id === question.id && old.asked_at_ms === question.asked_at_ms)) continue;
      sent.push(notification(question.title, question.body));
    }
  }
  return sent;
}
