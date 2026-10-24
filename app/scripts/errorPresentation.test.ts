import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

import { presentAgentError, presentProbeError } from '../src/lib/shell/errorPresentation.ts';

const mappedCases = [
  {
    name: 'missing rollout history',
    raw: 'acp-error: {"code":-32600,"message":"no rollout found for thread id 019fe7e2"}',
    summary: "This session's history is missing, so it can't be resumed.",
    detail: 'acp-error:\n{\n  "code": -32600,\n  "message": "no rollout found for thread id 019fe7e2"\n}'
  },
  {
    name: 'invalid session response',
    raw: 'invalid-response: ACP session response did not include sessionId',
    summary: 'The agent sent an unexpected reply.'
  },
  {
    name: 'session id omitted without a code prefix',
    raw: 'ACP session response did not include sessionId',
    summary: 'The agent sent an unexpected reply.'
  },
  {
    name: 'session not started',
    raw: 'session-not-started: ACP session has not started',
    summary: "The session hasn't connected yet."
  },
  {
    name: 'transport failure',
    raw: 'transport: pending request map is unavailable',
    summary: 'The connection to the agent was interrupted.'
  },
  {
    name: 'runtime transport event',
    raw: 'acp-transport: connection reset by peer',
    summary: 'The connection to the agent was interrupted.'
  },
  {
    name: 'closed transport',
    raw: 'transport: transport closed before response',
    summary: 'The agent connection closed.'
  },
  {
    name: 'exited agent process',
    raw: 'transport: agent process exited with status 1',
    summary: 'The agent stopped unexpectedly.'
  },
  {
    name: 'Claude pnpm shim parsed as JavaScript',
    raw: `transport: ACP sidecar exited: /Users/example/app/node_modules/.bin/claude-agent-acp:2
| basedir=$(dirname "$(echo "$0" | sed -e 's,\\\\,/,g')")
| ^^^^^^^
|
| SyntaxError: missing ) after argument list
| at wrapSafe (node:internal/modules/cjs/loader:1762:18)
| at Module._compile (node:internal/modules/cjs/loader:1803:20)
|
| Node.js v26.0.0`,
    summary: "Claude's provider adapter couldn't start. Open Settings → Updates, check Provider adapters, then install or update Claude."
  },
  {
    name: 'sidecar spawn failure',
    raw: 'sidecar-spawn: failed to start adapter process',
    summary: "The agent couldn't start."
  },
  {
    name: 'uninitialized adapter',
    raw: 'not-initialized: ACP adapter is not initialized',
    summary: "The agent hasn't finished starting."
  },
  {
    name: 'invalid capabilities',
    raw: 'invalid-capabilities: image prompts require protocol support',
    summary: 'The agent reported unsupported features.'
  },
  {
    name: 'invalid configuration',
    raw: 'invalid-config: unknown model option',
    summary: "The agent couldn't apply that setting."
  },
  {
    name: 'serialization failure',
    raw: 'serialization: unsupported value',
    summary: "The app couldn't prepare the agent request."
  },
  {
    name: 'duplicate inbound consumer',
    raw: 'inbound-already-taken: ACP inbound events already have a consumer',
    summary: 'The agent connection is already in use.'
  },
  {
    name: 'empty response',
    raw: 'empty-response: The active agent returned no text',
    summary: 'The agent replied without any text.'
  },
  {
    name: 'generic ACP request error',
    raw: 'acp-error: permission denied',
    summary: "The agent couldn't complete that request."
  },
  {
    name: 'authentication required',
    raw: 'acp-error: {"code":-32000,"message":"Authentication required"}',
    summary: 'This agent requires sign-in before it can continue.',
    detail: 'acp-error:\n{\n  "code": -32000,\n  "message": "Authentication required"\n}'
  },
  {
    name: 'authentication prompt from the adapter',
    raw: 'acp-error: Please run /login before continuing',
    summary: 'This agent requires sign-in before it can continue.'
  },
  {
    name: 'sign-in instruction from the backend',
    raw: 'Claude needs you to sign in. Sign in with the Claude CLI where this session runs, then send again.',
    summary: 'Claude needs you to sign in. Sign in with the Claude CLI where this session runs, then send again.'
  }
];

for (const example of mappedCases) {
  test(example.name, () => {
    const presented = presentAgentError(example.raw);
    assert.equal(presented.summary, example.summary);
    assert.equal(presented.detail, example.detail ?? example.raw);
    assert.doesNotMatch(presented.summary, /\{|-?\d{3,}|^[a-z]+(?:-[a-z]+)+:/i);
  });
}

test('a failed settings probe says what failed and where to update the adapter', () => {
  assert.equal(
    presentProbeError('transport: agent process exited with status 1', 'Codex'),
    "The agent stopped unexpectedly. Update the Codex adapter in Settings → Updates."
  );
});

test('a failed settings probe does not repeat an update hint already in the message', () => {
  const raw = `transport: ACP sidecar exited: /a/node_modules/.bin/claude-agent-acp:2\n| SyntaxError: missing )`;
  assert.equal(presentProbeError(raw, 'Claude'), presentAgentError(raw).summary);
});

test('unknown errors use their actual text and retain their detail', () => {
  const raw = 'provider-meltdown: impossible state 47';
  assert.deepEqual(presentAgentError(raw), {
    summary: raw,
    detail: raw
  });
});

test('unknown errors are word-safe trimmed to 160 characters', () => {
  const raw = `${'visible '.repeat(30)}hidden details`;
  const presented = presentAgentError(raw);

  assert.equal(Array.from(presented.summary).length <= 160, true);
  assert.equal(presented.summary.endsWith('…'), true);
  assert.equal(presented.summary.includes('hidden details'), false);
  assert.equal(presented.detail, raw);
});

test('embedded JSON is pretty-printed in detail without leaking into the summary', () => {
  const raw = 'acp-error: {"code":-32600,"message":"no rollout found for thread id 019fe7e2"}';
  const presented = presentAgentError(raw);

  assert.equal(presented.summary, "This session's history is missing, so it can't be resumed.");
  assert.equal(
    presented.detail,
    'acp-error:\n{\n  "code": -32600,\n  "message": "no rollout found for thread id 019fe7e2"\n}'
  );
});

test('an empty error still has a human summary and no empty detail', () => {
  assert.deepEqual(presentAgentError(''), { summary: 'The agent hit an error.' });
});

test('the rail row shows the error summary in its status slot, never the raw text', async () => {
  const worktreeRow = await readFile(
    new URL('../src/lib/shell/components/WorktreeAgentRow.svelte', import.meta.url),
    'utf8'
  );

  assert.match(worktreeRow, /presentAgentError\(session\.lastError\)/);
  assert.doesNotMatch(worktreeRow, /\{session\.lastError\}|title=\{session\.lastError\}/);
  // The row has no disclosure of its own: it renders only the safe summary.
  assert.match(worktreeRow, /presentedError\?\.summary/);
  assert.doesNotMatch(worktreeRow, /presentedError\?\.detail/);
  assert.doesNotMatch(worktreeRow, /<details/);
});

const rejectedModelMessage = "The 'gpt-6.1-sol' model is not supported when using Codex with a ChatGPT account.";
const rejectedModelJson = `{"type":"error","status":400,"error":{"type":"invalid_request_error","message":"The 'gpt-6.1-sol' model is not supported when using Codex with a ChatGPT account."}}`;

for (const [name, raw, summary] of [
  ['provider error body', rejectedModelJson, rejectedModelMessage],
  ['top-level message', '{"message":"Pick another model."}', 'Pick another model.'],
  ['JSON-RPC wrapped provider error', JSON.stringify({ code: -32001, message: rejectedModelJson }), rejectedModelMessage],
  ['nested message takes precedence', '{"error":{"message":"Provider rejected the request."},"message":"Request failed"}', 'Provider rejected the request.'],
  ['plain sentence', 'Pick another model.', 'Pick another model.'],
  ['malformed JSON', '{"error":{"message":"Try again"}', '{"error":{"message":"Try again"}'],
  ['JSON without a message', '{"status":400}', '{"status":400}'],
  ['non-string messages', '{"error":{"message":400},"message":null}', '{"error":{"message":400},"message":null}'],
  ['null JSON', 'null', 'null']
]) {
  test(`${name} keeps readable error copy`, () => {
    assert.equal(presentAgentError(raw).summary, summary);
  });
}

test('a full conversation tells the reader what to do, in the composer too', () => {
  const summary = presentAgentError('{"code":-32603,"message":"Internal error: Prompt is too long"}').summary;
  assert.match(summary, /conversation is full/i);
  assert.match(summary, /\/compact/);
});
