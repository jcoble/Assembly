/**
 * drawerYield.test.ts — a wide tab opened from the `+` menu closes the right
 * drawer until the user leaves that tab (TSK-1490).
 *
 * Owner rules: a `+` tab closes the drawer; leaving it (the chat, another tab,
 * closing it, a session switch) brings the drawer back if it was open; an
 * editor tab never yields; the drawer toggle wins over the remembered state.
 *
 * Same loader as `gitRootSessionSwitch.test.ts`: `$state` is a stand-in and the
 * modules that need a browser or the whole shell are stubs.
 */
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

(globalThis as { $state?: unknown }).$state = <T>(value: T): T => value;

const stubs: Record<string, string> = {
  '/layout/topTabs.svelte.ts': 'export const topTabs = { activeKind: "diff", reset() {}, restore() {}, capture() { return null; } };',
  '/shell/shellPanels.ts': 'const noop = () => {}; export const shellPanels = { panelShown: noop, sourceControlVisible: noop, filesVisible: noop, worktreesVisible: noop, stacksVisible: noop };',
  '/browser/browserStore.svelte.ts': 'export function captureBrowserState() { return null; } export function releaseBrowserWorkspace() {} export function restoreBrowserState() {}',
  '/lib/shell/settings/settingsStore.svelte.ts': 'export const settings = { panels: { problemsLocation: "bottom" } };',
  '/layout/frame.ts': 'export const CENTER_MIN_WIDTH = 0, DOCK_HEIGHT = 0, SESSIONS_MAX_WIDTH = 0, SESSIONS_MIN_WIDTH = 0, SESSIONS_STRIP_WIDTH = 0, SESSIONS_WIDTH = 0, TOOLS_MAX_WIDTH = 0, TOOLS_MIN_WIDTH = 0;'
};

registerHooks({
  resolve(specifier, context, nextResolve) {
    const withExtension = specifier.startsWith('.') && !/\.[cm]?[jt]s$/.test(specifier) ? `${specifier}.ts` : specifier;
    const resolved = nextResolve(withExtension, context);
    const stub = Object.keys(stubs).find((suffix) => resolved.url.endsWith(suffix));
    return stub ? { url: `data:text/javascript,${encodeURIComponent(stubs[stub])}`, shortCircuit: true } : resolved;
  }
});

const { WorkbenchController } = await import('../src/lib/shell/controllers/workbenchController.svelte.ts');

function fresh(open: boolean) {
  const workbench = new WorkbenchController();
  workbench.setRightPanelOpen(open);
  return workbench;
}

// Rules 1 and 3: a `+` tab closes the drawer; closing that tab brings it back.
let w = fresh(true);
w.yieldDrawerTo('diff');
assert.equal(w.rightPanelOpen, false, 'Changes from + closes the drawer');
w.paneShown('diff');
assert.equal(w.rightPanelOpen, false, 'the yielded tab in front keeps it closed');
w.paneShown(null);
assert.equal(w.rightPanelOpen, true, 'closing the tab brings the drawer back');

// Rule 3: choosing the chat brings it back.
w = fresh(true);
w.yieldDrawerTo('git-history');
w.showChat();
assert.equal(w.rightPanelOpen, true, 'the chat brings the drawer back');

// Rule 3: another tab brings it back.
w = fresh(true);
w.yieldDrawerTo('pull-requests');
w.paneShown('editor:/a.txt');
assert.equal(w.rightPanelOpen, true, 'another tab brings the drawer back');

// A second `+` tab while yielded moves the memory instead of dropping it.
w = fresh(true);
w.yieldDrawerTo('diff');
w.yieldDrawerTo('browser:tab-1');
assert.equal(w.rightPanelOpen, false, 'a second + tab keeps the drawer closed');
w.paneShown('browser:tab-1');
assert.equal(w.rightPanelOpen, false, 'the second tab in front keeps it closed');
w.paneShown(null);
assert.equal(w.rightPanelOpen, true, 'leaving the second tab brings the drawer back');

// Rule 5: the toggle wins over the remembered state.
w = fresh(true);
w.yieldDrawerTo('diff');
w.toggleRightPanel();
assert.equal(w.rightPanelOpen, true, 'the toggle reopens the drawer on the wide tab');
w.paneShown('git-history');
assert.equal(w.rightPanelOpen, true, 'and it stays open on another tab');
w.toggleRightPanel();
w.paneShown(null);
assert.equal(w.rightPanelOpen, false, 'a drawer the user closed stays closed');

// A drawer that was closed stays closed.
w = fresh(false);
w.yieldDrawerTo('diff');
assert.equal(w.rightPanelOpen, false);
w.paneShown(null);
assert.equal(w.rightPanelOpen, false, 'closing the tab does not open a drawer that was closed');
w.showChat();
assert.equal(w.rightPanelOpen, false, 'the chat does not open a drawer that was closed');

// Rule 4: an editor tab never yields.
w = fresh(true);
w.yieldDrawerTo('editor:/a.txt');
assert.equal(w.rightPanelOpen, true, 'an editor tab leaves the drawer alone');

// A session switch gives a yielded drawer back.
w = fresh(true);
w.yieldDrawerTo('diff');
w.beginSessionSwitch();
assert.equal(w.rightPanelOpen, true, 'the next session gets the drawer back');

console.log('drawerYield: ok');
