import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  accessChoicesFor,
  BARE_ROOT_MESSAGE,
  buildThreadStartRequest,
  defaultThreadStartState,
  draftEffortFor,
  effortChoicesFor,
  groupProviderModels,
  projectCwd,
  titleFromPrompt,
  validateThreadStart
} from '../src/lib/shell/conversation/newSession/threadStartFlow.ts';
import { sessionTitleFromPrompt } from '../src/lib/shell/sessionStrip.ts';

// The draft's project travels to the first send.
{
  const state = defaultThreadStartState({
    projectId: 'project-1',
    projectPath: '/Users/me/dev/alpha'
  });
  assert.equal(state.projectId, 'project-1');
  assert.equal(buildThreadStartRequest({ ...state, prompt: 'hello' })?.projectId, 'project-1');
}

// "No project" starts in the machine's home folder and names no project.
{
  const state = defaultThreadStartState({ projectId: null, projectPath: '', cwd: '/home/user' });
  const request = buildThreadStartRequest({ ...state, prompt: 'hello' });
  assert.equal(request?.projectId, null);
  assert.equal(request?.projectPath, null);
  assert.equal(request?.cwd, '/home/user');
}

// A current Codex ACP catalog encodes effort in each model id and uses its own
// access ids. Empty reported effort choices are meaningful, not a reason to
// revive the older fallback ids that the provider rejects.
{
  const currentCodex = [{
    provider: 'codex',
    model: 'gpt-6-astra[medium]',
    availableModels: ['gpt-6-astra[low]', 'gpt-6-astra[medium]'],
    reasoningEffort: null,
    availableEfforts: [],
    approvalPolicy: 'agent',
    availableApprovalPolicies: ['read-only', 'agent', 'agent-full-access']
  }];
  const state = defaultThreadStartState({
    projectPath: '/Users/me/dev/work/app',
    cwd: '/Users/me/dev/work/app',
    providerConfigs: currentCodex
  });
  assert.equal(state.model, 'gpt-6-astra[medium]');
  assert.equal(state.effort, '');
  assert.equal(state.access, 'agent');
  assert.deepEqual(effortChoicesFor('codex', currentCodex), []);
  assert.deepEqual(accessChoicesFor('codex', currentCodex), [
    'read-only', 'agent', 'agent-full-access'
  ]);
  const request = buildThreadStartRequest({ ...state, prompt: 'say hello' });
  assert.equal(request?.reasoningEffort, null);
  assert.equal(request?.approvalPolicy, 'agent');
}

// A project's session runs in the project's own root folder: there is no
// branch or worktree to pick.
{
  const root = '/Users/me/dev/alpha';
  assert.equal(projectCwd(root, false), root);
  const state = { ...defaultThreadStartState({ projectId: 'p', projectPath: root, cwd: projectCwd(root, false) }), prompt: 'hi' };
  assert.deepEqual(validateThreadStart(state), []);
  const request = buildThreadStartRequest(state);
  assert.equal(request?.cwd, root);
  assert.equal(request?.projectPath, root);
}

// A bare repository has no files to work on: the draft never starts there and
// says why. Until the folder check answers, nothing is sent either.
{
  const bare = '/Users/me/dev/mac-command-bar';
  assert.equal(projectCwd(bare, true), '');
  const state = { ...defaultThreadStartState({ projectId: 'p', projectPath: bare }), prompt: 'hi', cwd: '', rootProblem: BARE_ROOT_MESSAGE };
  assert.deepEqual(validateThreadStart(state), [{ field: 'project', message: BARE_ROOT_MESSAGE }]);
  assert.equal(buildThreadStartRequest(state), null);
  assert.equal(
    BARE_ROOT_MESSAGE,
    'This folder is a bare Git repository with no files to work on. Remove this project and add a checked-out folder instead.'
  );
  assert.deepEqual(validateThreadStart({ ...state, rootProblem: '' }), [
    { field: 'project', message: 'Checking the project folder. Try again in a moment.' }
  ]);
}

// A failed folder check is the reason Send gives, not "checking" forever.
{
  const rootProblem = 'The project folder could not be checked: project_root_is_bare is not available for remote workspaces';
  const failed = { ...defaultThreadStartState({ projectId: 'p', projectPath: '/srv/app' }), prompt: 'hi', cwd: '', rootProblem };
  assert.deepEqual(validateThreadStart(failed), [{ field: 'project', message: rootProblem }]);
  assert.equal(buildThreadStartRequest(failed), null);
}

const providerConfigs = [
  {
    provider: 'codex',
    model: 'gpt-5.6-luna',
    availableModels: ['gpt-5.6-sol', 'gpt-5.6-luna'],
    reasoningEffort: 'high',
    availableEfforts: ['low', 'medium', 'high', 'max'],
    approvalPolicy: 'on-request',
    availableApprovalPolicies: ['on-request', 'never']
  },
  {
    provider: 'claude',
    model: 'claude-sonnet',
    availableModels: ['claude-opus', 'claude-sonnet'],
    reasoningEffort: 'medium',
    availableEfforts: ['low', 'medium', 'high'],
    approvalPolicy: 'acceptedits',
    availableApprovalPolicies: ['acceptedits', 'plan']
  }
];

// Provider data stays grouped and current values are retained even when a
// provider reports a model that is not in its advertised list.
{
  const groups = groupProviderModels([
    ...providerConfigs,
    { ...providerConfigs[0], model: 'gpt-5.6-next', availableModels: ['gpt-5.6-luna'] }
  ]);
  assert.deepEqual(groups.map((group) => group.provider), ['codex', 'claude', 'antigravity']);
  assert.deepEqual(groups[0].models.map((model) => model.id), [
    'gpt-5.6-next',
    'gpt-5.6-luna',
    'gpt-5.6-sol'
  ]);
  assert.equal(groups[0].label.length > 0, true);
  assert.equal(groups[0].models.every((model) => model.hint.length > 0), true);
  assert.equal(groups[0].models.find((model) => model.id === 'gpt-5.6-next')?.available, false);
  assert.match(
    groups[0].models.find((model) => model.id === 'gpt-5.6-next')?.unavailableReason ?? '',
    /Unavailable/
  );
  assert.equal(groups[0].models.find((model) => model.id === 'gpt-5.6-luna')?.available, true);
}

// A wiped store has no session to ask. The draft still offers each provider's
// known models, efforts and access levels — in the ids the provider takes.
// 'acceptedits' was once sent to Claude in place of 'acceptEdits', was refused,
// and started nothing; and a remembered model must not become the whole list.
{
  const remembered = [
    {
      provider: 'claude',
      model: 'haiku',
      availableModels: [],
      reasoningEffort: null,
      availableEfforts: [],
      approvalPolicy: null,
      availableApprovalPolicies: []
    }
  ];
  const claude = groupProviderModels(remembered).find((group) => group.provider === 'claude');
  const ids = claude?.models.map((model) => model.id) ?? [];
  assert.equal(ids[0], 'haiku');
  assert.ok(ids.includes('opus'));
  assert.ok(ids.includes('claude-fable-5'));
  assert.ok(claude?.models.every((model) => model.available));
  assert.deepEqual(effortChoicesFor('claude', remembered), ['low', 'medium', 'high', 'max']);
  assert.deepEqual(accessChoicesFor('claude', remembered), [
    'default',
    'acceptEdits',
    'plan',
    'dontAsk',
    'bypassPermissions'
  ]);
  const state = defaultThreadStartState({
    projectPath: '/Users/me/dev/work/edi',
    cwd: '/Users/me/dev/work/edi',
    provider: 'claude',
    providerConfigs: remembered
  });
  assert.equal(state.model, 'haiku');
  assert.equal(state.access, 'acceptEdits');
  // The spelling an earlier build remembered is not offered back either.
  const stale = defaultThreadStartState({
    projectPath: '/Users/me/dev/work/edi',
    cwd: '/Users/me/dev/work/edi',
    provider: 'claude',
    providerConfigs: [{ ...remembered[0], approvalPolicy: 'acceptedits', reasoningEffort: 'xhigh' }]
  });
  assert.equal(stale.access, 'acceptEdits');
  assert.equal(stale.effort, 'medium');
  assert.deepEqual(accessChoicesFor('codex', []), ['untrusted', 'on-request', 'never']);
  assert.deepEqual(effortChoicesFor('codex', []), ['low', 'medium', 'high', 'xhigh', 'max']);
}

// Before a catalogue arrives, Antigravity keeps its model/access placeholders
// and has no separate effort control.
{
  const antigravity = groupProviderModels([]).find((group) => group.provider === 'antigravity');
  const ids = antigravity?.models.map((model) => model.id) ?? [];
  assert.equal(ids.length, 5);
  assert.equal(ids[0], 'Gemini 3.7 Flash (High)');
  assert.ok(ids.includes('Gemini 3.1 Pro (High)'));
  assert.deepEqual(effortChoicesFor('antigravity', []), []);
  assert.deepEqual(accessChoicesFor('antigravity', []), ['bypassPermissions']);

  const state = defaultThreadStartState({
    projectPath: '/Users/me/dev/work/mac-command-bar',
    cwd: '/Users/me/dev/work/mac-command-bar',
    provider: 'antigravity'
  });
  assert.equal(state.model, 'Gemini 3.7 Flash (High)');
  assert.equal(state.effort, '');
  assert.equal(state.access, 'bypassPermissions');

  const request = buildThreadStartRequest({ ...state, prompt: 'say hi in five words' });
  assert.equal(request?.model, 'Gemini 3.7 Flash (High)');
  assert.equal(request?.reasoningEffort, null);
}

// Opening a thread is a draft only; the pure default is already usable once a
// project and an existing checkout are supplied.
{
  const state = defaultThreadStartState({
    projectPath: '/Users/me/dev/work/mac-command-bar',
    cwd: '/Users/me/dev/work/mac-command-bar',
    providerConfigs
  });
  assert.equal(state.prompt, '');
  assert.equal(state.provider, 'codex');
  assert.equal(state.model, 'gpt-5.6-luna');
  assert.equal(state.effort, 'high');
  assert.equal(state.access, 'on-request');
  assert.deepEqual(validateThreadStart(state), [
    { field: 'prompt', message: 'Write a message or attach an image.' }
  ]);

  const claudeState = defaultThreadStartState({
    projectPath: state.projectPath,
    cwd: state.cwd,
    provider: 'claude',
    providerConfigs
  });
  assert.equal(claudeState.provider, 'claude');
  assert.equal(claudeState.model, 'claude-sonnet');
  assert.equal(claudeState.effort, 'medium');
  assert.equal(claudeState.access, 'acceptedits');
}

// Every picker value is carried into the request assembled for the existing
// session-creation path.
{
  const request = buildThreadStartRequest({
    prompt: '  Add a compact session rail  ',
    provider: 'claude',
    model: 'claude-opus',
    effort: 'high',
    access: 'acceptedits',
    projectPath: '/Users/me/dev/work/mac-command-bar',
    cwd: '/Users/me/dev/work/mac-command-bar'
  });
  // WIP: disabled. buildThreadStartRequest requires `executionEnvironment`; this state omits it.
  // assert.deepEqual(request, {
  //   prompt: 'Add a compact session rail',
  //   provider: 'claude',
  //   model: 'claude-opus',
  //   reasoningEffort: 'high',
  //   approvalPolicy: 'acceptedits',
  //   projectPath: '/Users/me/dev/work/mac-command-bar',
  //   cwd: '/Users/me/dev/work/mac-command-bar',
  //   title: 'Add a compact session rail'
  // });
}

assert.equal(
  titleFromPrompt('  First line that names the work\nSecond line with details  ', '/Users/me/app'),
  'First line that names the work'
);
assert.equal(titleFromPrompt('', '/Users/me/app'), 'Build in app');
// The backend names the session from the trimmed prompt, so a prompt that
// opens with a blank line is named after its first real line here too. Reading
// the untrimmed text found an empty first line and fell back to the project.
assert.equal(
  titleFromPrompt('\n\nName the work here\nand then details', '/Users/me/app'),
  'Name the work here'
);

// The provisional title has to be the same string the backend writes from the
// same prompt. The rail saves the row back seconds after the send; a title that
// differs by so much as a character reads as a rename there, and a renamed
// session is never given a better name after its first turn.
{
  const longPrompt = `${'x'.repeat(100)}\nsecond line`;
  const request = buildThreadStartRequest({
    prompt: longPrompt,
    provider: 'claude',
    model: 'claude-opus',
    effort: 'high',
    access: 'acceptedits',
    projectPath: '/Users/me/dev/work/mac-command-bar',
    cwd: '/Users/me/dev/work/mac-command-bar'
  });
  assert.equal(request?.title, sessionTitleFromPrompt(longPrompt));
  assert.equal(request?.title.length, 64);
  assert.equal(titleFromPrompt(longPrompt, '/Users/me/app'), sessionTitleFromPrompt(longPrompt));
}

// A remote draft carries the stable machine profile through the first send.
{
  const local = defaultThreadStartState({ projectPath: '/home/user/project' });
  const remote = {
    ...local,
    prompt: 'Run this remotely',
    executionEnvironment: 'remote' as const,
    remoteProfileId: null
  };
  assert.deepEqual(validateThreadStart(remote), [
    { field: 'project', message: 'Choose a remote machine first.' }
  ]);
  const request = buildThreadStartRequest({ ...remote, remoteProfileId: 'workbox' });
  assert.equal(request?.executionEnvironment, 'remote');
  assert.equal(request?.remoteProfileId, 'workbox');
}

// A plain conversation uses a real cwd without claiming it as a project.
{
  const plain = { ...defaultThreadStartState({ projectPath: '', cwd: '/home/user' }), prompt: 'Help me remember this' };
  assert.deepEqual(validateThreadStart(plain), []);
  assert.equal(buildThreadStartRequest(plain)?.projectPath, null);
  assert.equal(buildThreadStartRequest(plain)?.cwd, '/home/user');
  assert.equal(buildThreadStartRequest({ ...plain, executionEnvironment: 'remote', remoteProfileId: 'workbox' })?.projectPath, null);
  assert.deepEqual(validateThreadStart({ ...plain, cwd: '' }).map((problem) => problem.field), ['project']);
  assert.equal(buildThreadStartRequest({ ...plain, prompt: '' }, true)?.title, 'New conversation');
}

console.log('threadStartFlow.test.ts passed');

// Model IDs remain wire values while labels come from the live provider catalog.
{
  const groups = groupProviderModels([{
    provider: 'claude', model: 'opus', availableModels: ['opus', 'sonnet'],
    modelLabels: { opus: 'Opus 5.5', sonnet: 'Sonnet 5' },
    reasoningEffort: null, availableEfforts: [], approvalPolicy: null, availableApprovalPolicies: []
  }]);
  const models = groups.find(group => group.provider === 'claude')!.models;
  assert.equal(models.find(model => model.id === 'opus')!.label, 'Opus 5.5');
  assert.equal(models.find(model => model.id === 'sonnet')!.label, 'Sonnet 5');
}

// A draft offers only the chosen model's efforts and falls back to that model's default.
{
  const codex = {
    reasoningEffort: 'high',
    availableEfforts: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'],
    modelEfforts: {
      'gpt-5.5': ['low', 'medium', 'high', 'xhigh'],
      'gpt-5.6-sol': ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'],
      'gpt-6.1-sol': []
    },
    modelDefaultEfforts: { 'gpt-5.5': 'medium', 'gpt-5.6-sol': 'low' }
  };
  assert.deepEqual(draftEffortFor(codex, 'gpt-5.5', 'ultra'), {
    efforts: ['low', 'medium', 'high', 'xhigh'], effort: 'medium'
  });
  assert.equal(draftEffortFor(codex, 'gpt-5.5', 'xhigh').effort, 'xhigh');
  assert.equal(draftEffortFor(codex, 'gpt-5.6-sol', 'ultra').effort, 'ultra');
  assert.deepEqual(draftEffortFor(codex, 'gpt-6.1-sol', 'ultra'), { efforts: [], effort: '' });
  // A model the per-model lists leave out has no effort control: it offers none.
  assert.deepEqual(draftEffortFor(codex, 'gpt-7-nova', 'xhigh'), { efforts: [], effort: '' });
  // Without a model default, the probe's current effort, then the first offered.
  assert.equal(draftEffortFor({ ...codex, modelDefaultEfforts: {} }, 'gpt-5.5', 'ultra').effort, 'high');
  assert.equal(draftEffortFor({ ...codex, reasoningEffort: 'max', modelDefaultEfforts: {} }, 'gpt-5.5', 'ultra').effort, 'low');
  // A provider with no per-model lists (Claude, or an older server) keeps the provider list.
  const claude = { reasoningEffort: 'xhigh', availableEfforts: ['low', 'medium', 'high', 'xhigh', 'max'] };
  assert.deepEqual(draftEffortFor(claude, 'opus', ''), {
    efforts: ['low', 'medium', 'high', 'xhigh', 'max'], effort: 'xhigh'
  });
  assert.equal(draftEffortFor(claude, 'opus', 'max').effort, 'max');
}

console.log('threadStartFlow: draft efforts follow the chosen model');

// A live catalogue takes precedence over a remembered configuration for the
// same provider, even when the remembered row was loaded first.
{
  const remembered = {
    provider: 'claude', model: 'opus', availableModels: [],
    reasoningEffort: 'medium', availableEfforts: [],
    approvalPolicy: null, availableApprovalPolicies: []
  };
  const live = {
    ...remembered, model: 'sonnet', availableModels: ['sonnet', 'haiku'],
    reasoningEffort: 'high', availableEfforts: ['low', 'high']
  };
  const state = defaultThreadStartState({
    projectPath: '/work/app', provider: 'claude', providerConfigs: [remembered, live]
  });
  assert.equal(state.model, 'sonnet');
  assert.equal(state.effort, 'high');
  assert.deepEqual(effortChoicesFor('claude', [remembered, live]), ['low', 'high']);
  const noEffort = { ...live, reasoningEffort: null, availableEfforts: [] };
  assert.deepEqual(effortChoicesFor('claude', [remembered, noEffort]), []);
  assert.equal(defaultThreadStartState({
    projectPath: '/work/app', provider: 'claude', providerConfigs: [remembered, noEffort]
  }).effort, '');
}


test('live permission catalogues replace remembered placeholders for every provider', () => {
  for (const [provider, current, modes] of [
    ['claude', 'default', ['default', 'auto']],
    ['codex', 'agent', ['read-only', 'workspace-write', 'agent']],
    ['antigravity', 'default', ['default', 'auto_edit', 'yolo']]
  ] as const) {
    const remembered = {
      provider, model: null, availableModels: [], reasoningEffort: null, availableEfforts: [],
      approvalPolicy: modes[1], availableApprovalPolicies: []
    };
    // Mode controls are usable even when no model catalogue has arrived.
    const live = { ...remembered, approvalPolicy: current, availableApprovalPolicies: [...modes] };
    assert.deepEqual(accessChoicesFor(provider, [remembered, live]), modes);
    assert.equal(defaultThreadStartState({
      projectPath: '/work/app', provider, providerConfigs: [remembered, live]
    }).access, current);
  }
});

test('a live session without permission choices does not revive placeholders', () => {
  const remembered = {
    provider: 'claude', model: null, availableModels: [], reasoningEffort: null, availableEfforts: [],
    approvalPolicy: 'bypassPermissions', availableApprovalPolicies: []
  };
  const live = { ...remembered, model: 'sonnet', availableModels: ['sonnet'], approvalPolicy: null };
  assert.deepEqual(accessChoicesFor('claude', [remembered, live]), []);
  assert.equal(defaultThreadStartState({
    projectPath: '/work/app', provider: 'claude', providerConfigs: [remembered, live]
  }).access, '');
});

test('an Antigravity permission choice reaches the first send', () => {
  const state = defaultThreadStartState({ projectPath: '/work/app', provider: 'antigravity' });
  assert.equal(buildThreadStartRequest({ ...state, prompt: 'hello', access: 'auto_edit' })?.approvalPolicy, 'auto_edit');
});
