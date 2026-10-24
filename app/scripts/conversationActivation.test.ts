import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { decideConversationActivation } from '../src/lib/shell/conversation/conversationActivation.ts';

const appSession = {
  agent: 'codex',
  origin: 'app',
  state: 'live',
  executionOwner: 'structured',
  ptySessionId: null,
  nativeSessionId: 'thread-app'
};

const matchingConnection = {
  provider: 'codex',
  state: 'connected',
  nativeSessionId: 'thread-app'
};

assert.deepEqual(
  decideConversationActivation(appSession, matchingConnection),
  { kind: 'view' },
  'a connected app session is only brought into view'
);

assert.deepEqual(
  decideConversationActivation(appSession, null),
  { kind: 'structured', nativeSessionMode: 'resume' },
  'a disconnected app session resumes through ACP'
);

assert.deepEqual(
  decideConversationActivation(
    { ...appSession, state: 'exited', executionOwner: 'stopped' },
    matchingConnection
  ),
  { kind: 'structured', nativeSessionMode: 'resume' },
  'a stopped app session resumes even when its retained frontend snapshot still says connected'
);

const stoppedExternal = {
  agent: 'codex',
  origin: 'external',
  state: 'background',
  executionOwner: 'stopped',
  ptySessionId: null,
  nativeSessionId: 'thread-external'
};

assert.deepEqual(
  decideConversationActivation(stoppedExternal, {
    provider: 'codex',
    state: 'connected',
    nativeSessionId: 'thread-external'
  }),
  { kind: 'structured', nativeSessionMode: 'load' },
  'a stopped external Codex session loads by native id instead of trusting a stale connection'
);

for (const agent of ['codex', 'claude', 'antigravity']) {
  const session = { ...stoppedExternal, agent };
  assert.deepEqual(
    decideConversationActivation(session, null),
    agent === 'antigravity' ? { kind: 'terminal' } : { kind: 'structured', nativeSessionMode: 'load' },
    `a stopped external ${agent} session follows proven adapter load support`
  );
  for (const running of [
    { state: 'live' },
    { executionOwner: 'terminal' },
    { ptySessionId: 'pty-live' }
  ]) {
    assert.deepEqual(
      decideConversationActivation({ ...session, ...running }, null),
      { kind: 'terminal' },
      `an external ${agent} session with a live CLI writer stays terminal-owned`
    );
  }
  assert.deepEqual(decideConversationActivation({ ...session, nativeSessionId: null }, null),
    { kind: 'terminal' }, `${agent} history without a native id stays terminal-owned`);
}

assert.deepEqual(
  decideConversationActivation(
    { ...stoppedExternal, state: 'live', executionOwner: 'terminal', ptySessionId: 'pty-live' },
    null
  ),
  { kind: 'terminal' },
  'a running external session stays terminal-owned'
);

assert.deepEqual(
  decideConversationActivation({ ...stoppedExternal, nativeSessionId: null }, null),
  { kind: 'terminal' },
  'external history without a native id keeps the terminal fallback'
);

const page = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');
const selectionController = readFileSync(
  new URL('../src/lib/shell/controllers/sessionSelectionController.svelte.ts', import.meta.url),
  'utf8'
);
const selectionLayers = readFileSync(
  new URL('../src/lib/shell/sessionSelectionLayers.svelte.ts', import.meta.url),
  'utf8'
);
const tauriSource = readFileSync(new URL('../src/lib/tauriSource.ts', import.meta.url), 'utf8');
const remoteConversation = readFileSync(
  new URL('../src-tauri/src/agent_conversation/remote/mod.rs', import.meta.url),
  'utf8'
);
assert.match(
  page,
  /async function selectSession\(ownedId: string\): Promise<void> \{[\s\S]*?await selection\.selectSession\(ownedId\);/,
  'rail selection automatically runs the owned conversation activation'
);
assert.match(
  page,
  /selection\.hasChatProjection && selection\.chatOwnedId[\s\S]*?hidden=\{selection\.chatOwnedId !== selection\.activeOwnedId\}[\s\S]*?aria-hidden=\{selection\.chatOwnedId !== selection\.activeOwnedId \? "true" : undefined\}[\s\S]*?inert=\{selection\.chatOwnedId !== selection\.activeOwnedId\}[\s\S]*?activeOwnedId=\{selection\.chatOwnedId\}/,
  'the previous conversation surface stays mounted but is hidden and inert while the next rail selection loads'
);
assert.match(
  page,
  /\.conversation-surface-shell\[hidden\]\s*\{[\s\S]*?display:\s*none/,
  'inactive conversation surfaces are removed from layout and paint while retained'
);
assert.match(
  page,
  /selection\.activeOwnedId && \(!selection\.hasChatProjection \|\| selection\.chatOwnedId !== selection\.activeOwnedId\)[\s\S]*?Loading conversation/,
  'loading state is shown when the active rail session differs from the mounted chat projection'
);
assert.match(
  page,
  /\.conversation-data-isolation \{[^}]*?inset:\s*0;[^}]*?position:\s*absolute;[^}]*?z-index:\s*1;[^}]*?background:\s*var\(--panel-fade\),\s*var\(--color-surface\);/,
  'the loading state is an opaque overlay that covers the retained conversation surface'
);
assert.doesNotMatch(
  page,
  /conversation-surface-shell[\s\S]{0,180}opacity:\s*0/,
  'inactive conversation surfaces do not add an opacity compositor layer'
);
// 48c353ae6 replaced the center Dockview with one tab pane: the diff mounts
// only while it is in front, and History and Pull requests only while their tab exists.
assert.match(
  page,
  /\{#if topTabs\.activeKind === "diff"\}[\s\S]*?<GitDiffView[\s\S]*?\{\/if\}/,
  'the diff surface only mounts while its tab is in front'
);
for (const [kind, component] of [['git-history', 'GitHistoryView'], ['pull-requests', 'PullRequestWorkspace']]) {
  assert.match(
    page,
    new RegExp(`\\{#if topTabs\\.row\\.includes\\("${kind}"\\)\\}[\\s\\S]*?<${component}[\\s\\S]*?\\{/if\\}`),
    `${kind} only mounts while its tab exists`
  );
}
assert.match(
  tauriSource,
  /listRemoteAgentConversationSessionsFromTauri\([\s\S]*?signal\?\.addEventListener\('abort', cancel, \{ once: true \}\)[\s\S]*?finally \{[\s\S]*?signal\?\.removeEventListener\('abort', cancel\)/,
  'remote session discovery removes its abort listener when the invoke settles'
);
assert.match(
  remoteConversation,
  /pub async fn cancel_request[\s\S]*?try_send\(ClientRequest::Cancel \{ id: request_id \}\)/,
  'remote request cancellation never waits behind a saturated request queue'
);
assert.match(
  remoteConversation,
  /"-T",[\s\S]*?target,[\s\S]*?"cat >\/dev\/null"[\s\S]*?\.stdin\(Stdio::piped\(\)\)/,
  'the SSH tunnel exits on parent pipe EOF even when the app cannot run its shutdown hook'
);
assert.match(
  tauriSource,
  /readAgentConversationCapabilitiesFromTauri\([\s\S]*?requestId[\s\S]*?signal\?\.addEventListener\('abort', cancel, \{ once: true \}\)[\s\S]*?read_agent_conversation_capabilities[\s\S]*?finally \{[\s\S]*?signal\?\.removeEventListener\('abort', cancel\)/,
  'remote capability reads are owned by the surface abort signal'
);
assert.match(
  tauriSource,
  /readAgentConversationWorkspaceExpandedPathsFromTauri\([\s\S]*?requestId[\s\S]*?signal\?\.addEventListener\('abort', cancel, \{ once: true \}\)[\s\S]*?read_agent_conversation_workspace_expanded_paths[\s\S]*?finally \{[\s\S]*?signal\?\.removeEventListener\('abort', cancel\)/,
  'expanded-path reads are owned by the selection abort signal'
);
assert.doesNotMatch(
  selectionController,
  /setActiveOwned\(ownedId\);[\s\S]{0,160}clearChatHistory\(\);[\s\S]{0,160}const session = rail\.owned\.find/,
  'rail selection does not clear the rendered chat before the candidate load settles'
);
// dda06a519 replaced the load/cancel/release helpers with the selected chat
// client (select, ready, dispose) and store eviction of inactive sessions.
assert.match(
  selectionLayers,
  /await selectedConversationChatReady\(session\.ownedId\);\s*if \(!this\.isCurrent\(owner\)\) \{\s*if \(this\.requestedChatOwnedId !== session\.ownedId\) disposeSelectedConversationChat\(session\.ownedId\);\s*return;/,
  'a stale candidate load releases only the stale candidate'
);
assert.match(
  selectionLayers,
  /if \(departingOwnedId && departingOwnedId !== session\.ownedId\) \{\s*disposeSelectedConversationChat\(departingOwnedId\);\s*\}[\s\S]*?await selectedConversationChatReady\(session\.ownedId\);/,
  'departing async work is cancelled before the next conversation snapshot is awaited'
);
assert.match(
  selectionLayers,
  /this\.chatOwnedId = session\.ownedId;[\s\S]*?evictInactiveConversationSessions\(session\.ownedId\);/,
  'departing data is evicted only after the hidden surface has swapped to the new conversation'
);
assert.doesNotMatch(page, /Open conversation|Conversation paused/, 'conversation activation has no manual workaround gate');

console.log('conversationActivation.test.ts passed');
