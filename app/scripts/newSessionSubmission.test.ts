import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  buildThreadStartRequest,
  validateThreadStart
} from '../src/lib/shell/conversation/newSession/threadStartFlow.ts';

const page = readFileSync(new URL('../src/routes/+page.svelte', import.meta.url), 'utf8');
const controller = readFileSync(
  new URL('../src/lib/shell/controllers/newSessionController.svelte.ts', import.meta.url),
  'utf8'
);
const surface = readFileSync(
  new URL('../src/lib/shell/conversation/newSession/DraftSessionSurface.svelte', import.meta.url),
  'utf8'
);
const conversationSurface = readFileSync(
  new URL('../src/lib/shell/conversation/ConversationSurface.svelte', import.meta.url),
  'utf8'
);
const backend = readFileSync(
  new URL('../src/lib/shell/conversation/newSession/newSessionBackend.ts', import.meta.url),
  'utf8'
);
const nativeSource = readFileSync(
  new URL('../src-tauri/src/main.rs', import.meta.url),
  'utf8'
);
const conversationService = readFileSync(
  new URL('../src/lib/shell/conversation/conversationService.ts', import.meta.url),
  'utf8'
);

function functionSource(source, signature, nextSignature) {
  const start = source.indexOf(signature);
  const end = source.indexOf(nextSignature, start);
  assert.notEqual(start, -1, `${signature} must exist`);
  assert.notEqual(end, -1, `${signature} must have a bounded source block`);
  return source.slice(start, end);
}

const startNewSession = functionSource(
  controller,
  '\tasync start(',
  '\n\tdispose():'
);

// The draft surface is a session, not a form: the ordinary composer, with the
// pickers riding on it.
assert.match(surface, /data-testid="draft-session-surface"/);
assert.match(surface, /<ConversationComposer/);
assert.match(surface, /data-testid="draft-session-project"/);
assert.match(surface, /data-testid="draft-session-provider"/);
assert.match(surface, /data-testid="draft-session-new-project"/);
// The draft asks the project's own machine whether its folder is a bare
// repository, locally and through a remote server.
assert.match(backend, /invokeOn<boolean>\(machine, 'project_root_is_bare', \{ root \}\)/);
assert.match(nativeSource, /project_folders::project_root_is_bare,/);

assert.match(controller, /open\(projectId: string \| null = null, folder: string \| null = null\): void/);
// A project chosen on the Projects screen reaches the draft and wins over its default.
assert.match(page, /projectId=\{selection\.newSession\.projectId\}/);
assert.match(surface, /const presetId = projectId \?\? defaultDraftProjectId\(/);
assert.doesNotMatch(page, /const providerConfigs = \$derived/);

// The first-send request keeps every chosen picker value and runs in the
// project's own folder.
const request = buildThreadStartRequest({
  prompt: 'Build the new thread pane',
  provider: 'codex',
  model: 'gpt-5.6-luna',
  effort: 'max',
  access: 'on-request',
  projectPath: '/Users/me/dev/work/mac-command-bar',
  cwd: '/Users/me/dev/work/mac-command-bar'
});
assert.equal(request?.prompt, 'Build the new thread pane');
assert.equal(request?.cwd, '/Users/me/dev/work/mac-command-bar');
assert.equal(request?.model, 'gpt-5.6-luna');
assert.equal(request?.reasoningEffort, 'max');
assert.equal(request?.approvalPolicy, 'on-request');
assert.deepEqual(validateThreadStart({ ...request, prompt: request.prompt }), []);

// It answers with the new session's id, so a panel that starts a session on
// somebody's behalf can point at the one it just made.
assert.match(startNewSession, /async start\([\s\S]*?request: ThreadStartRequest[\s\S]*?\): Promise<string>/);
assert.match(startNewSession, /return owned\.ownedId;/);
assert.match(startNewSession, /createFreshSession\(\{[\s\S]*?cwd: request\.cwd,[\s\S]*?title: request\.title/);
// The new row sits in its project's rail group before the first reply.
assert.match(startNewSession, /projectGroupKey: project\?\.groupKey \?\? 'none'/);
assert.doesNotMatch(startNewSession, /request\.branch/);
assert.match(startNewSession, /await selectSession\(owned\.ownedId\)/);
assert.doesNotMatch(startNewSession, /setAgentConversationConfig\(/);
// Since 11984b792 a first message with screenshots opens the conversation so the
// images can be saved into it; a text-only start still leaves that to sendStructuredMessage.
assert.match(startNewSession, /if \(images\.length\) \{\s*const connection = await ensureStructuredConversation\(/);
assert.equal(startNewSession.match(/ensureStructuredConversation/g)?.length, 1);
assert.match(startNewSession, /await sendStructuredMessage\(owned\.ownedId, request\.prompt, \{/);
assert.match(startNewSession, /model: request\.model/);
assert.match(startNewSession, /approvalPolicy: request\.approvalPolicy/);
assert.doesNotMatch(
  conversationSurface,
  /if \(active\.source === 'fresh' && active\.runtimeState === 'starting'\) return;/,
  'fresh-session startup still reads persisted model and access settings'
);
assert.match(startNewSession, /throw error;/);
assert.match(startNewSession, /could not start \$\{owned\.agent\} session: \$\{detail\}/);
assert.match(conversationService, /reasoningEffort\?: string \| null;/);
assert.match(conversationService, /nativeSessionMode: input\.nativeSessionMode \?\? 'resume'/);

console.log('newSessionSubmission.test.ts passed');
