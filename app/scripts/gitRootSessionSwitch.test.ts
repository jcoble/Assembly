/**
 * gitRootSessionSwitch.test.ts — a session switch lets go of the outgoing
 * repository, so the Changes tab follows the session that arrives (TSK-1439).
 *
 * The Changes tab (`GitDiffView.svelte`) points git at its session's folder
 * only while nothing else has pointed it anywhere (`gitPanel.root` is null).
 * The switch is the ownership boundary, so `beginSessionSwitch` must clear the
 * root; otherwise the remounted tab keeps showing the previous repository.
 *
 * The controller's runes are `$state` class fields only, so the same stand-in
 * as `gitPanelStore.test.ts` runs it as plain TypeScript. The modules that need
 * a browser or the whole shell are replaced with stubs below.
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
const { gitService } = await import('../src/lib/shell/git/gitService.ts');
const { gitPanel } = await import('../src/lib/shell/git/gitPanelStore.svelte.ts');

/** What the Changes tab does when it mounts for a session (GitDiffView.svelte). */
function mountChangesTab(sessionRoot: string): void {
  if (!gitPanel.root) gitService.activate(sessionRoot);
}

const workbench = new WorkbenchController();

// Only the Changes tab is open, on session A's repository.
mountChangesTab('/repos/a');
assert.equal(gitPanel.root, '/repos/a');

// Switch to session B, in another project: the tab remounts for B.
workbench.beginSessionSwitch();
mountChangesTab('/repos/b');
assert.equal(gitPanel.root, '/repos/b', 'the Changes tab shows the new session\'s repository');

// Switch to another session of the same project: the tab still has a repository.
workbench.beginSessionSwitch();
mountChangesTab('/repos/b');
assert.equal(gitPanel.root, '/repos/b', 'a same-project switch keeps the Changes tab on its repository');

console.log('gitRootSessionSwitch: ok');
