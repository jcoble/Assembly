import { getCurrentWebview } from '@tauri-apps/api/webview';

export type Drop = { type: 'enter' | 'leave' | 'drop'; paths: string[] };

// The shell's window events. 'drop': files and folders dragged over and dropped on the window, by path (the page
// gets no HTML5 drop events for them). Resolves to the unsubscribe. Outside the shell (vite dev in a browser) there
// are none.
export function on(event: 'drop', handler: (drop: Drop) => void): Promise<() => void> {
  if (!('__TAURI_INTERNALS__' in globalThis)) return Promise.resolve(() => {});
  return getCurrentWebview().onDragDropEvent(({ payload }) => {
    if (payload.type === 'over') return;
    handler({ type: payload.type, paths: payload.type === 'leave' ? [] : payload.paths });
  });
}
