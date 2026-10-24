/**
 * offlineRemoteSelection.test.ts — selecting a saved remote session while its
 * machine has gone away still opens the chat from the Mac's copy (TSK-1332).
 *
 * The app can still think the machine is connected for a moment after it
 * stops answering. The folder check, the saved workspace read and the draft
 * read then fail; before this change any one of them cleared the chat. The
 * modules that need Tauri or the whole shell are replaced with stubs below.
 */
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

const state = <T>(value: T): T => value;
(globalThis as { $state?: unknown }).$state = Object.assign(state, { raw: state });
// Saved workspace records by session; a session without one reads as unreachable.
const workspaces: Record<string, unknown> = {};
(globalThis as { workspaces?: unknown }).workspaces = workspaces;

const offline = 'throw new Error("Remote machine box is not connected")';
const stubs: Record<string, string> = {
  '/lib/tauriSource.ts': `
    export async function validateProjectRootFromTauri() { ${offline}; }
    export async function readAgentConversationWorkspaceFromTauri(id) {
      if (id in globalThis.workspaces) return globalThis.workspaces[id];
      ${offline};
    }
    export async function writeAgentConversationWorkspaceFromTauri(id, snapshot) { globalThis.workspaces[id] = snapshot; }
    export async function readAgentConversationWorkspaceExpandedPathsFromTauri() { return []; }
    export async function writeAgentConversationWorkspaceExpandedPathsFromTauri() {}
    export async function writeAssemblySettingFromTauri() {}
    export async function deleteAgentConversationSessionFromTauri() {}
    export async function readRemoteAssemblyEnvironmentFromTauri() {}
    export async function connectRemoteAssemblyFromTauri() {}`,
  '/conversation/conversationService.ts': `
    export async function loadConversationSessionDraft() { ${offline}; }
    export async function changeStructuredConversationCheckout() {}
    export async function flushConversationSessionDraft() {}`,
  '/conversation/conversationConfig.ts': 'export async function warmAgentConversationConfig() { return null; }',
  '/conversation/conversationConnection.ts': `
    export function selectConversationChat() {}
    export async function selectedConversationChatReady() {}
    export function disposeSelectedConversationChat() {}`,
  '/conversation/conversationStore.svelte.ts': `
    const sessions = {};
    export function ensureConversationSession(ownedId) { return sessions[ownedId] ??= { generation: 1 }; }
    export function getConversationSession(ownedId) { return sessions[ownedId]; }
    export function evictInactiveConversationSessions() {}
    export function setConversationMode() {}
    export function setConversationAgentConfigState() {}
    export function setConversationAgentConfigError() {}
    export function captureConversationWorkspace() { return null; }
    export function restoreConversationAttachmentIds() {}`,
  '/projects/projectRegistry.svelte.ts': 'export const projectRegistry = { projects: [] };',
  '/devInvokeCounter.svelte.ts': 'export function countInvoke() {}',
  '/controllers/newSessionController.svelte.ts': 'export class NewSessionController { abandonDraftForSessionSwitch() {} }',
  '/shell/shellPanels.ts': 'export const shellPanels = { sessionPicked() {} };',
  '/files/explorer/explorerStore.svelte.ts': 'export function canonicalPath(path) { return path; }',
  '/files/explorer/explorerService.ts': 'export function refresh() {}',
  '/files/editor/editorStore.svelte.ts': `
    export const editorState = { openFiles: [], activePath: null };
    export function resetEditorState() {}
    export function restoreEditorFiles() {}
    export function setEditorProjectRoot() {}`
};

registerHooks({
  resolve(specifier, context, nextResolve) {
    const withExtension = specifier.startsWith('.') && !/\.[cm]?[jt]s$/.test(specifier) ? `${specifier}.ts` : specifier;
    const resolved = nextResolve(withExtension, context);
    const stub = Object.keys(stubs).find((suffix) => resolved.url.endsWith(suffix));
    return stub ? { url: `data:text/javascript,${encodeURIComponent(stubs[stub])}`, shortCircuit: true } : resolved;
  }
});

const { rail } = await import('../src/lib/shell/stores/sessionRailStore.svelte.ts');
const { SessionSelectionLayers } = await import('../src/lib/shell/sessionSelectionLayers.svelte.ts');
const { EditorSessionController } = await import('../src/lib/shell/controllers/editorSessionController.svelte.ts');

const session = {
  ownedId: 'saved-remote',
  agent: 'claude',
  cwd: '/home/me/repo',
  projectId: null,
  executionEnvironment: 'remote',
  remoteProfileId: 'box'
} as never;
rail.owned = [session];
// The app has not yet heard that the machine went away.
rail.remoteConnections.box = 'connected';

const layers = new SessionSelectionLayers();
await layers.selectSession(session, null, { generation: 0, signal: new AbortController().signal });
assert.equal(layers.chatOwnedId, 'saved-remote', 'the saved chat opens');
assert.equal(layers.hasChatProjection, true);
assert.equal(layers.treeRoot, '', 'the file tree waits for the machine');

const editor = new EditorSessionController();
const snapshot = await editor.restoreEditorWorkspaceForSession('saved-remote', layers.treeRoot, false, new AbortController().signal);
assert.equal(snapshot, null, 'the saved workspace read fails quietly');

// a_failed_remote_folder_check_keeps_the_saved_editor_tabs: the folder check
// fails but the saved workspace read succeeds. The cached chat opens without
// the editor taking the session over, so leaving it cannot save an empty
// editor over the tabs it had.
{
  const { SessionSelectionController } = await import('../src/lib/shell/controllers/sessionSelectionController.svelte.ts');
  const saved = { openPaths: ['/home/me/repo/a.ts'], activePath: '/home/me/repo/a.ts' };
  workspaces['saved-remote'] = saved;
  workspaces.other = null;
  const controller = new SessionSelectionController();
  await controller.selectSession('saved-remote');
  assert.equal(controller.chatOwnedId, 'saved-remote', 'the cached chat opens');
  assert.equal(controller.selectionError, null);
  await controller.editorSessions.restoreEditorWorkspaceForSession('other', '/other', true, new AbortController().signal);
  assert.equal(workspaces['saved-remote'], saved, 'switching away keeps the saved tabs');
}

// TSK-1545: a remote session opened before its machine connects. The page's
// Connect (from the row or from Settings) calls refreshRemoteConnection and
// shows the snapshot it returns, or keeps the default view when it returns
// false; leaving the session saves whatever view is on screen.
{
  const { SessionSelectionController } = await import('../src/lib/shell/controllers/sessionSelectionController.svelte.ts');
  const later = { ...(session as object), ownedId: 'later-remote', remoteProfileId: 'later-box' } as never;
  const local = { ownedId: 'local-other', agent: 'claude', cwd: '/home/me/other', projectId: null, executionEnvironment: 'local' } as never;
  rail.owned = [later, local];
  rail.remoteConnections['later-box'] = 'disconnected';
  workspaces['later-remote'] = { openPaths: [], activePath: null, rightTab: 'source-control' };
  workspaces['local-other'] = null;
  const controller = new SessionSelectionController();
  await controller.selectSession('later-remote');
  assert.equal(controller.activeWorkspaceSnapshot, null, 'offline, the saved view is not read yet');

  rail.remoteConnections['later-box'] = 'connected';
  const refreshed = await controller.refreshRemoteConnection('later-box');
  assert.equal(refreshed, true, 'the connect hands the page a saved view to show');
  assert.equal(controller.activeWorkspaceSnapshot?.rightTab, 'source-control', 'the saved side panel is shown after the connect');

  const onScreen = refreshed ? controller.activeWorkspaceSnapshot?.rightTab : 'files';
  controller.rememberWorkspaceState({ rightTab: onScreen });
  await controller.selectSession('local-other');
  assert.equal((workspaces['later-remote'] as { rightTab?: string }).rightTab, 'source-control', 'leaving keeps the saved view');
  await controller.selectSession('later-remote');
  assert.equal(controller.activeWorkspaceSnapshot?.rightTab, 'source-control', 'coming back shows it again');
}

console.log('offlineRemoteSelection: ok');
