import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import ts from 'typescript';

// Execute the production controller with storage and editor I/O replaced.
// These assertions concern persistence and selection, not component structure.
const source = readFileSync(new URL('../src/lib/shell/controllers/editorSessionController.svelte.ts', import.meta.url), 'utf8');
const parsed = ts.createSourceFile('controller.ts', source, ts.ScriptTarget.Latest, true);
let code = source;
for (const statement of [...parsed.statements].reverse()) {
  if (ts.isImportDeclaration(statement)) code = code.slice(0, statement.getStart(parsed)) + code.slice(statement.end);
}
code = stripTypeScriptTypes(code.replace('export class EditorSessionController', 'class EditorSessionController'));
interface Controller {
  restoreEditorWorkspaceForSession(id: string, root: string, available: boolean, signal: AbortSignal): Promise<unknown>;
  rememberWorkspaceState(patch: object): Record<string, unknown> | null;
  persistWorkspaceState(id: string): Promise<void>;
  setPanel(panel: object | null): void;
  dispose(): Promise<void>;
}
const reads: string[] = [];
const writes: string[] = [];
let writeFailure: unknown;
let readFailure: unknown;
let root: string | null = null;
let writeGate: Promise<void> | null = null;
let readGate: Promise<void> | null = null;
let lastWrite: unknown;
const saved = new Map<string, unknown>();
const editorState: { openFiles: { path: string; content: string }[]; activePath: string | null } = { openFiles: [], activePath: null };
const dependencies = {
  rail: { owned: [], remoteConnections: {} },
  parseRemoteWorkspacePath: () => null,
  mapWorkspaceSnapshotPaths: (value: unknown) => value,
  editorState,
  resetEditorState: () => undefined,
  restoreEditorFiles: () => undefined,
  setEditorProjectRoot: (value: string | null) => { root = value; },
  captureWorkspace: ({ openFiles }: { openFiles: { path: string; content: string }[] }) => ({
    openPaths: openFiles.map((file) => file.path),
    activePath: null,
    ...(openFiles.length ? { fileStates: openFiles.map((file) => ({ path: file.path, content: file.content })) } : {}),
  }),
  planWorkspaceRestore: () => ({ openFiles: [], activePath: null }),
  captureConversationWorkspace: () => null,
  readAgentConversationWorkspaceFromTauri: async (id: string) => {
    reads.push(id);
    if (readGate && id === 'beta') await readGate;
    if (readFailure && id === 'other') throw readFailure;
    return { openPaths: [], activePath: null, owner: id };
  },
  writeAgentConversationWorkspaceFromTauri: async (id: string, snapshot: unknown) => {
    writes.push(id);
    lastWrite = snapshot;
    saved.set(id, snapshot);
    if (writeGate) await writeGate;
    if (writeFailure) throw writeFailure;
  },
};
const ControllerClass = new Function(...Object.keys(dependencies), `${code}; return EditorSessionController;`)(...Object.values(dependencies)) as new () => Controller;
const signal = new AbortController().signal;
const missing = 'could not save the workspace because the session does not exist';

for (const failure of [missing, new Error(missing), { code: 'agent-conversation-command-failed', message: missing }]) {
  writeFailure = undefined; readFailure = undefined; reads.length = 0; writes.length = 0;
  const controller = new ControllerClass();
  await controller.restoreEditorWorkspaceForSession('removed', '/old', true, signal);
  writeFailure = failure;
  const restored = await controller.restoreEditorWorkspaceForSession('existing', '/new', true, signal);
  assert.deepEqual(restored, { openPaths: [], activePath: null, owner: 'existing' });
  assert.equal(root, '/new');
  assert.deepEqual(writes, ['removed']);
  writeFailure = undefined;
  await controller.restoreEditorWorkspaceForSession('third', '/third', true, signal);
  await controller.restoreEditorWorkspaceForSession('existing', '/new', true, signal);
  assert.deepEqual(writes, ['removed', 'existing', 'third'], 'subsequent switches checkpoint the correct owner');
  const count = reads.length;
  await controller.restoreEditorWorkspaceForSession('existing', '/new', true, signal);
  assert.equal(reads.length, count, 'same-session selection does not repeat storage work');
}

writeFailure = undefined; readFailure = undefined;
const controller = new ControllerClass();
await controller.restoreEditorWorkspaceForSession('saved', '/saved', true, signal);
const diskError = new Error('disk full');
writeFailure = diskError;
await assert.rejects(controller.restoreEditorWorkspaceForSession('other', '/other', true, signal), (error) => error === diskError);
assert.equal(root, '/saved', 'real save errors retain the outgoing workspace');
writeFailure = undefined;
readFailure = new Error('workspace read failed');
await assert.rejects(controller.restoreEditorWorkspaceForSession('other', '/other', true, signal), (error) => error === readFailure);
readFailure = undefined;
await controller.restoreEditorWorkspaceForSession('other', '/other', true, signal);
assert.equal(root, '/other', 'retry remains possible after storage recovers');
const aborted = AbortSignal.abort();
const count = reads.length;
await controller.restoreEditorWorkspaceForSession('cancelled', '/cancelled', true, aborted);
assert.equal(reads.length, count);

// A workspace change made while a checkpoint write is pending survives that write.
const racing = new ControllerClass();
await racing.restoreEditorWorkspaceForSession('race', '/race', true, signal);
let releaseWrite!: () => void;
writeGate = new Promise((resolve) => { releaseWrite = resolve; });
const firstSave = racing.persistWorkspaceState('race');
await new Promise((resolve) => setTimeout(resolve, 0));
racing.rememberWorkspaceState({ browser: { url: 'https://newer.example' } });
const secondSave = racing.persistWorkspaceState('race');
writeGate = null;
releaseWrite();
await firstSave;
await secondSave;
assert.deepEqual(racing.rememberWorkspaceState({})?.browser, { url: 'https://newer.example' }, 'newer state stays in memory');
assert.deepEqual((lastWrite as { browser?: unknown }).browser, { url: 'https://newer.example' }, 'the next save writes the newer state');

// TSK-1441: a switch aborted after the outgoing editor was released (while the
// incoming read is in flight) must not let the next switch save that empty
// editor over the outgoing session's open files and unsaved text.
const switching = new ControllerClass();
switching.setPanel({
  captureViewStates: () => ({}),
  workspaceOwnedPaths: () => editorState.openFiles.map((file) => file.path),
  restoreViewStates: () => undefined,
  releaseSessionResources: () => { editorState.openFiles = []; },
  refreshOpenFiles: () => undefined,
});
await switching.restoreEditorWorkspaceForSession('alpha', '/alpha', true, signal);
editorState.openFiles = [{ path: '/alpha/notes.md', content: 'unsaved alpha text' }];
let releaseRead!: () => void;
readGate = new Promise((resolve) => { releaseRead = resolve; });
const abortedSwitch = new AbortController();
const toBeta = switching.restoreEditorWorkspaceForSession('beta', '/beta', true, abortedSwitch.signal);
while (!reads.includes('beta')) await new Promise((resolve) => setTimeout(resolve, 0));
const alphaSaved = saved.get('alpha');
assert.deepEqual((alphaSaved as { fileStates?: unknown }).fileStates, [{ path: '/alpha/notes.md', content: 'unsaved alpha text' }]);
abortedSwitch.abort();
readGate = null;
releaseRead();
assert.equal(await toBeta, null);
await switching.restoreEditorWorkspaceForSession('gamma', '/gamma', true, signal);
assert.equal(saved.get('alpha'), alphaSaved, "the aborted switch leaves alpha's saved files and unsaved text unchanged");

// TSK-1440: closing the app (dispose) saves the open files and unsaved text
// changed since the last periodic save.
const closing = new ControllerClass();
await closing.restoreEditorWorkspaceForSession('closing', '/closing', true, signal);
editorState.openFiles = [{ path: '/closing/a.md', content: 'unsaved on close' }];
writes.length = 0;
await closing.dispose();
assert.deepEqual(writes, ['closing'], 'the final save writes the closing session');
assert.deepEqual((saved.get('closing') as { fileStates?: unknown }).fileStates, [{ path: '/closing/a.md', content: 'unsaved on close' }]);
// TSK-1545: a remote session opened before its machine connects reads its saved
// workspace once the machine is connected, instead of keeping the default view
// it was opened with, and the editor shown while offline is not saved over it.
const reconnecting = new ControllerClass();
(dependencies.rail.owned as object[]).push({ ownedId: 'remote', executionEnvironment: 'remote', remoteProfileId: 'box' });
assert.equal(await reconnecting.restoreEditorWorkspaceForSession('remote', '', false, signal), null);
(dependencies.rail.remoteConnections as Record<string, string>).box = 'connected';
reads.length = 0;
writes.length = 0;
reconnecting.rememberWorkspaceState({ rightTab: 'files' });
const reconnected = await reconnecting.restoreEditorWorkspaceForSession('remote', '/remote', true, signal);
assert.deepEqual(reads, ['remote'], 'the saved workspace is read once the machine connects');
assert.deepEqual(reconnected, { openPaths: [], activePath: null, owner: 'remote' });
assert.deepEqual(writes, [], 'nothing is saved over the remote workspace');
console.log('editorWorkspaceRecovery: deleted outgoing session, repeat switching, save/read failures, cancellation, changes during a save, aborted switches, the save on close and the read after a remote machine connects passed');
