import assert from 'node:assert/strict';
import test from 'node:test';

import { createPanelActivation } from '../src/lib/shell/panelActivation.ts';

test('files_panel_hidden_skips_explorer_on_session_pick', () => {
  let root = '/repo/one';
  const explorerCalls: string[] = [];
  const panels = createPanelActivation(
    {
      editor: () => {},
      git: () => {},
      browser: () => {},
      explorer: (selectedRoot) => explorerCalls.push(selectedRoot),
      worktrees: () => {},
      stacks: () => {},
      problems: () => {}
    },
    () => ({ root, projects: [] })
  );

  panels.allowSessionLoads();
  panels.sessionPicked();
  assert.deepEqual(explorerCalls, [], 'a hidden Files panel costs nothing on a session pick');

  panels.filesVisible(true);
  assert.deepEqual(explorerCalls, ['/repo/one'], 'showing Files loads the selected session root');

  panels.sessionPicked();
  assert.deepEqual(explorerCalls, ['/repo/one'], 'the same root is not loaded twice');

  root = '/repo/two';
  panels.sessionPicked();
  assert.deepEqual(explorerCalls, ['/repo/one'], 'rail traversal does not rebuild visible Files');

  panels.filesVisible(false);
  panels.filesVisible(true);
  assert.deepEqual(explorerCalls, ['/repo/one', '/repo/two'], 'reopening Files loads the final root');
});

test('hidden_editor_does_not_follow_later_session_roots', () => {
  let root = '/repo/one';
  const editorCalls: Array<string | null> = [];
  const panels = createPanelActivation(
    {
      editor: (selectedRoot) => editorCalls.push(selectedRoot),
      git: () => {},
      browser: () => {},
      explorer: () => {},
      worktrees: () => {},
      stacks: () => {},
      problems: () => {}
    },
    () => ({ root, projects: [] })
  );

  panels.allowPanelLoads();
  panels.allowSessionLoads();
  panels.panelShown('editor');
  assert.deepEqual(editorCalls, ['/repo/one']);

  panels.panelShown('session');
  root = '/repo/two';
  panels.sessionPicked();
  assert.deepEqual(editorCalls, ['/repo/one'], 'a hidden editor keeps no session-root work');

  panels.panelShown('editor');
  assert.deepEqual(editorCalls, ['/repo/one', '/repo/two']);
});
