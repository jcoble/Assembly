import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const page = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');
const selection = readFileSync(
  new URL('../src/lib/shell/controllers/sessionSelectionController.svelte.ts', import.meta.url),
  'utf8'
);
const editor = readFileSync(
  new URL('../src/lib/shell/controllers/editorSessionController.svelte.ts', import.meta.url),
  'utf8'
);
const panel = readFileSync(
  new URL('../src/lib/shell/files/components/EditorPanel.svelte', import.meta.url),
  'utf8'
);
const draft = readFileSync(
  new URL('../src/lib/shell/conversation/newSession/DraftSessionSurface.svelte', import.meta.url),
  'utf8'
);
const overlays = readFileSync(
  new URL('../src/lib/shell/components/ShellOverlays.svelte', import.meta.url),
  'utf8'
);
const startup = readFileSync(
  new URL('../src/lib/shell/controllers/shellStartup.ts', import.meta.url),
  'utf8'
);

assert.match(page, /<EditorPanel[\s\S]*?bind:this=\{editorPanel\}/);
assert.match(page, /rootAvailable=\{selection\.controlledEditorRootAvailable\}/);
assert.match(page, /\$effect\(\(\) => \{\s*selection\.setEditorPanel\(editorPanel\);\s*\}\);/);
assert.doesNotMatch(page, /onMount\(\(\) => \{\s*selection\.setEditorPanel\(editorPanel\)/);
// 19112edf3: disposeRoute now runs disposeRouteOnce once and shares its promise.
assert.match(page, /async function disposeRouteOnce\(\): Promise<void>[\s\S]*?await selectionDisposal/);
// TSK-1424 D: disposeRoute is async and still awaits the one shared disposal.
assert.match(page, /async function disposeRoute\(\): Promise<void> \{\s*routeDisposal \?\?= disposeRouteOnce\(\);\s*await routeDisposal;\s*\}/);
assert.match(selection, /async dispose\(\): Promise<void>[\s\S]*?await editorDisposal/);
assert.doesNotMatch(selection, /void this\.editorSessions\.dispose\(\)/);
assert.match(selection, /await this\.editorSessions\.restoreEditorWorkspaceForSession\([\s\S]*?owner\.signal/);
assert.match(editor, /async restoreEditorWorkspaceForSession\([\s\S]*?stopSignal: AbortSignal/);
// d00de3f79 queued the checkpoint and 2d5702688 wrapped it in try; 725896a73 and 1c889045f wrapped the read for remote sessions.
// TSK-1424 D: the queue is enqueue/runAfter. Each save waits for the earlier one (ignoring its error) before it
// runs, and a switch releases the outgoing editor only after its own checkpoint has finished.
assert.match(editor, /try \{\s*await this\.enqueue\(\(\) => this\.checkpointActiveWorkspace\(stopSignal\)\);\s*\} catch \(error\) \{[\s\S]*?\}\s*if \(stopSignal\.aborted\) return null;\s*this\.releaseActiveEditorResources\(\);/);
assert.match(editor, /private async enqueue\(work: \(\) => Promise<void>\): Promise<void> \{\s*const run = this\.runAfter\(this\.checkpointQueue, work\);\s*this\.checkpointQueue = run;\s*await run;\s*\}/);
assert.match(editor, /private async runAfter\(previous: Promise<void>, work: \(\) => Promise<void>\): Promise<void> \{\s*try \{\s*await previous;\s*\} catch \{[^}]*\}\s*await work\(\);\s*\}/);
// Queued saves use the controller's own signal, which dispose() aborts after starting its final checkpoint.
assert.match(editor, /async persistWorkspaceState\([^)]*\): Promise<void> \{\s*const stopSignal = this\.stop\.signal;\s*await this\.enqueue\(async \(\) => \{[\s\S]*?this\.checkpointActiveWorkspace\(stopSignal, attachmentIds\)/);
assert.match(editor, /const checkpoint = this\.checkpointActiveWorkspace\(stopSignal\);\s*this\.stop\.abort\(\);/);
// TSK-1441: ownership is released with the editor, before any later abort or error can return.
assert.match(editor, /this\.releaseActiveEditorResources\(\);\s*(?:\/\/[^\n]*\s*)*this\.releaseOwnership\(\);\s*if \(stopSignal\.aborted\) return null;[\s\S]*?await readAgentConversationWorkspaceFromTauri\(ownedId\);\s*\} catch \(error\) \{\s*if \(stopSignal\.aborted\) return null;[\s\S]*?const snapshot = [^;]+;\s*if \(stopSignal\.aborted\) return null;/);
assert.match(editor, /planWorkspaceRestore\(snapshot\)/);
assert.match(editor, /restoreEditorFiles\(plan\.openFiles, plan\.activePath\)/);
assert.match(editor, /await writeAgentConversationWorkspaceFromTauri\(ownedId, snapshot, machineOf\(ownedId\)\);\s*if \(stopSignal\.aborted\) return;/);
assert.doesNotMatch(editor, /\.then\(|\.catch\(|\.finally\(/);
assert.doesNotMatch(page, /\{#key\s+selection\.activeOwnedId/);
// TSK-1440: closing the window or quitting destroys the webview without tearing down the route,
// so the exit path runs the route's disposal (and the editor's final save) before letting the exit go.
assert.match(page, /<ShellOverlays[\s\S]*?onBeforeExit=\{disposeRoute\}/);
assert.match(overlays, /async function exitApp\(kind: AppExitKind\): Promise<void> \{\s*try \{\s*await onBeforeExit\(\);[\s\S]*?\}\s*await continueAppExit\(kind\);\s*\}/);
assert.doesNotMatch(overlays, /void continueAppExit\(/);

assert.match(panel, /let sessionStopController = new AbortController\(\);/);
assert.match(panel, /export function releaseSessionResources[\s\S]*?sessionStopController\.abort\(\);[\s\S]*?sessionStopController = new AbortController\(\);/);
assert.match(panel, /destroyed = true;\s*sessionStopController\.abort\(\);/);
// 41df05d99: images skip the text read, so the read is now one arm of a ternary.
assert.match(panel, /const preview = [^;]*?await readSourceFromTauri\(record\);\s*if \(stopSignal\.aborted\) return;/);
assert.match(panel, /const saved = await writeSourceToTauri[\s\S]*?if \(stopSignal\.aborted\) return false;/);
assert.match(panel, /await warmSourceLspForRootFromTauri\(projectRoot\);\s*if \(stopSignal\.aborted\) return;/);

// 00215d7a7 replaced remote deployment with connecting (deployRemoteAssemblyFromTauri is gone).
// 5a5aca827 moved pickProjectFolder() into AddProjectDialog, dropped loadRefs() from send, renamed
// hydrate() into a Promise.all, and made onSend the last await of its try (the catch checks the signal).
// The guard before a call may now be any earlier abort check with no await in between.
for (const call of [
  'projectRootIsBare(machine, projectPath)',
  'onSend(request, stagedImages.map((item) => item.file))',
  'Promise.all([hydrateProjects(), hydrateRemoteAssembly(owner)])',
  'readRemoteAssemblyEnvironmentFromTauri()'
]) {
  const escaped = call.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  assert.match(
    draft,
    new RegExp(`if \\(stopSignal\\.aborted(?: \\|\\| [^)]+)?\\) return;(?:(?!await )[\\s\\S])*?(?:[^;\\n]+ = )?await ${escaped};\\s*(?:if \\(stopSignal\\.aborted(?: \\|\\| [^)]+)?\\) return;|\\} catch \\(error\\) \\{\\s*if \\(!stopSignal\\.aborted\\))`),
    `${call} must be guarded before and after`
  );
}

assert.match(startup, /onSelectInitial\(ownedId: string, stopSignal: AbortSignal\): Promise<void>/);
// 8fee02322 opens session loads between the check and the select; a0b9ca6d0 lists remote sessions together with local ones.
assert.match(startup, /if \(!shellActive\(generation, controller\.signal\)\) return;\s*shellPanels\.allowSessionLoads\(\);\s*await options\.onSelectInitial\(initial\.ownedId, controller\.signal\);\s*if \(!shellActive\(generation, controller\.signal\)\) return;/);
assert.match(startup, /readRemoteSessions\(controller\.signal\)/);
assert.match(startup, /await listRemoteAgentConversationSessionsFromTauri\(stopSignal\);/);

console.log('editorSessionLifecycle.test.ts passed');
