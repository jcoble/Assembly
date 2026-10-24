/**
 * gitAmend.ts — the Amend switch's message box. Turning Amend on fills an empty
 * box with the last commit's first line, read from the history the panel has
 * already loaded; turning it off takes back only that text. Sent untouched, the
 * line goes as an empty message so git keeps the whole original message, body
 * included.
 */
import type { GitPanelState } from './gitPanelStore.svelte.ts';

export function createGitAmend(state: GitPanelState) {
  /** What Amend put in the box, and the repository it came from; '' when it put nothing. */
  let prefill = '';
  let prefillRoot: string | null = null;
  const putByAmend = () => (state.root === prefillRoot ? prefill : '');
  return {
    set(on: boolean): void {
      const put = putByAmend();
      prefill = '';
      state.amend = on;
      if (!on) {
        if (put !== '' && state.commitMessage === put) state.commitMessage = '';
        return;
      }
      // A file's history has a different newest commit; an unloaded history leaves the box empty,
      // and an empty amend still works.
      const subject = state.historyPath === '' ? (state.history[0]?.subject ?? '') : '';
      if (state.commitMessage.trim() !== '' || subject === '') return;
      state.commitMessage = prefill = subject;
      prefillRoot = state.root;
    },
    /** The message to hand git for this amend. */
    messageFor(text: string): string {
      return text === putByAmend() ? '' : text;
    },
    /** The amend happened: the box is empty again, and the next commit is a new one, not a second amend. */
    finish(): void {
      prefill = '';
      state.commitMessage = '';
      state.amend = false;
    }
  };
}
