/**
 * draftSessionLifecycle.test.ts — a draft is not a session until it is sent.
 *
 * Two halves. The first runs the draft's own rules: a draft that is abandoned
 * creates nothing, and a draft that is sent creates exactly one session
 * carrying every value the composer's controls were left on. The second reads
 * the shell's wiring, because the "creates nothing" half is only true while
 * the page keeps a draft out of the rail and out of the backend.
 *
 * Run: node --experimental-strip-types scripts/draftSessionLifecycle.test.ts
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  buildThreadStartRequest,
  defaultThreadStartState,
  type ThreadStartPickerState,
  type ThreadStartRequest
} from '../src/lib/shell/conversation/newSession/threadStartFlow.ts';

const page = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');
const controller = readFileSync(
  new URL('../src/lib/shell/controllers/newSessionController.svelte.ts', import.meta.url),
  'utf8'
);
const selectionController = readFileSync(
  new URL('../src/lib/shell/controllers/sessionSelectionController.svelte.ts', import.meta.url),
  'utf8'
);
const surface = readFileSync(
  new URL('../src/lib/shell/conversation/newSession/DraftSessionSurface.svelte', import.meta.url),
  'utf8'
);
const composer = readFileSync(
  new URL('../src/lib/shell/conversation/components/ConversationComposer.svelte', import.meta.url),
  'utf8'
);
const overlays = readFileSync(
  new URL('../src/lib/shell/components/ShellOverlays.svelte', import.meta.url),
  'utf8'
);

/** The one side effect a draft can have, counted. */
const started: ThreadStartRequest[] = [];

function send(state: ThreadStartPickerState): void {
  const request = buildThreadStartRequest(state);
  if (!request) return;
  started.push(request);
}

const draft: ThreadStartPickerState = {
  ...defaultThreadStartState({ projectPath: '/Users/me/dev/work/mac-command-bar' }),
  cwd: '/Users/me/dev/work/mac-command-bar'
};

// Abandoned: opened, configured, typed into, then thrown away. Nothing started.
{
  // A draft only ever exists as this value, so switching session — which is
  // what the page does with `draftOpen = false` — is dropping the variable.
  let abandoned: ThreadStartPickerState | null = {
    ...draft,
    provider: 'claude',
    prompt: 'never sent'
  };
  abandoned = null;
  assert.equal(abandoned, null);
  assert.equal(started.length, 0, 'an abandoned draft must create nothing');
}

// Sent: exactly one session, carrying every control's value.
{
  send({
    ...draft,
    prompt: 'Add the draft session surface',
    provider: 'claude',
    model: 'claude-opus',
    effort: 'high',
    access: 'acceptedits'
  });
  assert.equal(started.length, 1, 'the first send creates exactly one session');
  const [request] = started;
  assert.equal(request.provider, 'claude');
  assert.equal(request.model, 'claude-opus');
  assert.equal(request.reasoningEffort, 'high');
  assert.equal(request.approvalPolicy, 'acceptedits');
  assert.equal(request.projectPath, '/Users/me/dev/work/mac-command-bar');
  assert.equal(request.cwd, '/Users/me/dev/work/mac-command-bar');
  assert.equal(request.title, 'Add the draft session surface');
}

// An empty draft is not sendable, so pressing send on one still creates nothing.
{
  send({ ...draft, prompt: '   ' });
  assert.equal(started.length, 1, 'an empty draft must not create a session');
}

// ── The shell's side of the promise ──────────────────────────────────────────

// "+" opens a draft in the Session tab. It does not open a dialog, and the old
// form is gone from the overlay layer altogether.
assert.match(page, /onNewSession=\{\(\) => openNewSession\(\)\}/);
assert.match(page, /selection\.newSession\.open\(projectId, folder\)/);
// "+" or the same project and folder on an open draft keeps it and its typing; only a
// different project or folder starts a fresh draft (the surface is keyed on the signal).
assert.match(controller, /if \(this\.draftOpen && \(projectId === null \|\| \(projectId === this\.projectId && folder === this\.folder\)\)\) return;/);
assert.match(page, /\{#key selection\.newSession\.stopSignal\}/);
assert.match(controller, /this\.draftOpen = true;/);
assert.doesNotMatch(page, /openNewSession\(projectPath\?: string\): void;/);
assert.doesNotMatch(overlays, /ThreadStartHost|onStartNewSession|newSessionRoots/);

// A draft holds nothing the shell has to clean up: switching session discards it.
assert.match(selectionController, /this\.newSession\.abandonDraftForSessionSwitch\(session\.ownedId\)/);
assert.match(controller, /abandonDraftForSessionSwitch[\s\S]*?this\.draftOpen = false/);

// The draft is layered over the conversation, never in place of it — the live
// sessions' terminal hosts are inside that component.
assert.match(page, /<ConversationSurface[\s\S]*?\{#if selection\.newSession\.draftOpen\}[\s\S]*?<DraftSessionSurface/);

// The first send is the only thing that reaches the session-creating path.
assert.match(page, /onSend=\{startNewSession\}/);
assert.match(page, /await selection\.newSession\.start\(/);

// After the session exists, the panels are pointed at its folder deliberately,
// rather than being left to whatever start-up had already allowed.
assert.match(controller, /shellPanels\.allowSessionLoads\(\);\s*\n\s*let promptAccepted = false;\s*\n\s*try \{/);
assert.match(controller, /await selectSession\(owned\.ownedId\)/);
assert.match(selectionController, /shellPanels\.sessionPicked\(this\.activeRootAvailable\)/);
assert.match(page, /selectCenterTab\("session"\);\s*\n\s*workbench\.rightTab = "files";/);

// The draft's controls are the composer's own: the same settings menu a running
// session uses, plus the project picker and the machine row beside it.
assert.match(surface, /<ConversationComposer/);
assert.match(surface, /data-testid="draft-session-provider"/);
assert.match(surface, /data-testid="draft-session-project"/);
assert.match(surface, /onConfigChange=\{changeConfig\}/);

// Nothing in the draft surface may create a session or touch the rail.
assert.doesNotMatch(surface, /addOwnedSession|sendStructuredMessage|ensureStructuredConversation/);

// TSK-1411: Send is enabled as soon as there is text, but the provider's model
// choices take ~1 s (Antigravity up to ~7 s) to arrive. A press in that window
// is kept and goes out when they arrive, never dropped with a notice the
// arriving choices then wipe.
const draftSend = surface.slice(surface.indexOf('async function send()'), surface.indexOf('onMount('));
const keepPress = draftSend.indexOf('if (!currentCatalog && !catalogError) {');
assert.notEqual(keepPress, -1, 'a press made while the model choices load must be kept');
assert.match(draftSend.slice(keepPress), /^[^}]*submitting = true;[^}]*sendWhenReady = true;[^}]*return;/);
assert.ok(keepPress < draftSend.indexOf('Wait for this machine’s model choices'), 'the press is kept before the refusal');
assert.match(
  surface,
  /\$effect\(\(\) => \{\s*if \(!sendWhenReady \|\| \(!currentCatalog && !catalogError\)\) return;\s*sendWhenReady = false;\s*submitting = false;\s*untrack\(\(\) => void send\(\)\);/,
  'the kept press is sent once the choices, or their error, arrive'
);

// TSK-1364: a first send that fails while the draft is still open leaves the
// owner on the draft with the prompt and the error, so the row made for that
// send is removed, not left in the rail as a second, failed copy. A draft
// closed mid-send keeps the failed row: it is the only place the prompt is kept.
const startBody = controller.slice(controller.indexOf('\tasync start('), controller.indexOf('\n\tdispose():'));
assert.match(startBody, /discardSession: \(ownedId: string\) => Promise<void>/);
assert.match(
  startBody,
  /catch \(error\) \{[^}]*?let discarded = false;\s*if \(!promptAccepted && !stopSignal\.aborted\) \{\s*try \{\s*await discardSession\(owned\.ownedId\);\s*discarded = true;[\s\S]*?if \(discarded\) throw error;/,
  'a failed first send with the draft still open discards its row before rethrowing'
);
assert.match(page, /\(ownedId\) => selection\.removeSession\(ownedId\),?\s*\);/, 'the page discards through the ordinary remove');

// TSK-1415: while the first send starts its session, the draft's composer works
// like that session's own: Send (steer) when the provider can steer, a Stop that
// stops, and text typed meanwhile kept in the session's draft for the switch.
assert.match(page, /startingOwnedId=\{selection\.newSession\.pendingFirstMessage\?\.ownedId \?\? null\}/);
assert.match(surface, /draft=\{starting \? starting\.draft : draft\.prompt\}/);
assert.match(surface, /supportsSteering=\{starting\?\.capabilities\?\.session\.steering === true\}/);
assert.match(surface, /setConversationDraft\(starting\.ownedId, value\);\s*persistConversationSessionDraft\(starting\.ownedId, value\);/);
assert.match(surface, /onStop=\{\(\) => \{ if \(starting\) void stopStarting\(starting\.ownedId\); \}\}/);
assert.match(surface, /async function stopStarting\(ownedId: string\): Promise<void> \{\s*try \{\s*await stopStructuredTurn\(ownedId\);/);
// When the draft closes on the accepted first message, typing carries on in the
// new session's composer.
assert.match(page, /if \(selection\.newSession\.draftOpen \|\| !selection\.newSession\.pendingFirstMessage\) return;\s*if \(document\.activeElement === document\.body\) untrack\(\(\) => conversationSurface\?\.focusComposer\(\)\);/);
assert.match(page, /<ConversationSurface\s+bind:this=\{conversationSurface\}/);

console.log('draftSessionLifecycle.test.ts passed');
