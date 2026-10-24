import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { openBotThreads, reviewState } from './prBotReview.ts';

const summary = (sha: string, status: string) => `<!-- codex-pull-request-review-summary -->
<!-- codex-review-state:v1 {"code_review":{"commit_sha":"${sha}","has_any_findings":true,"has_unresolved_findings":true,"status":"${status}"},"security_review":{"commit_sha":null,"has_any_findings":null,"has_unresolved_findings":null,"status":null}} -->

## Codex Review Summary`;

test('reads the commit the bot last reviewed from its newest summary comment', () => {
  assert.deepEqual(reviewState(['Some other comment', summary('aaa', 'completed'), summary('bbb', 'running')]),
    { commit_sha: 'bbb', has_any_findings: true, has_unresolved_findings: true, status: 'running' });
  assert.equal(reviewState(['no marker here']), null);
});

test('keeps only unresolved threads the bot started', () => {
  const thread = (id: string, __typename: string, login: string, isResolved: boolean) => ({ id, isResolved,
    comments: { nodes: [{ databaseId: 1, author: { __typename, login }, path: 'a.rs', line: 3, originalLine: 3, body: 'x' }] } });
  const open = openBotThreads([thread('1', 'Bot', 'chatgpt-codex-connector', false), thread('2', 'Bot', 'chatgpt-codex-connector', true),
    thread('3', 'User', 'jcoble', false), thread('4', 'User', 'chatgpt-codex-connector1', false)]);
  assert.deepEqual(open.map((item) => item.id), ['1']);
});

// A fake gh: the bot stays "running" on the head until someone posts "@codex review",
// then reports "completed" (or keeps running when FAKE_STUCK is set). A no-op sleep keeps the waits instant.
const fakeGh = `#!/bin/bash
dir=$(dirname "$0")
state=running
if [ -f "$dir/asked" ] && [ -z "$FAKE_STUCK" ]; then state=completed; fi
case "$1 $2" in
  "repo view") echo o/r ;;
  "pr view") echo '{"number":7,"headRefOid":"abc123def"}' ;;
  "pr comment") echo "$5" >> "$dir/asked" ;;
  "api graphql") echo '[{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}]' ;;
  "api --paginate") echo '[[{"user":{"login":"'"$FAKE_LOGIN"'"},"body":"<!-- codex-review-state:v1 {\\"code_review\\":{\\"commit_sha\\":\\"abc123def\\",\\"status\\":\\"'$state'\\"}} -->"}]]' ;;
esac
`;

function runWithStalledBot(stuck: boolean, login = 'chatgpt-codex-connector[bot]'): { status: number | null; asks: string[] } {
  const dir = mkdtempSync(join(tmpdir(), 'pr-bot-review-'));
  writeFileSync(join(dir, 'gh'), fakeGh);
  writeFileSync(join(dir, 'sleep'), '#!/bin/sh\n');
  chmodSync(join(dir, 'gh'), 0o755);
  chmodSync(join(dir, 'sleep'), 0o755);
  const run = spawnSync(process.execPath, ['--experimental-strip-types', join(import.meta.dirname, 'prBotReview.ts')],
    { env: { ...process.env, PATH: `${dir}:${process.env.PATH}`, FAKE_STUCK: stuck ? '1' : '', FAKE_LOGIN: login }, encoding: 'utf8' });
  let asks: string[] = [];
  try { asks = readFileSync(join(dir, 'asked'), 'utf8').trim().split('\n'); } catch { /* never asked */ }
  return { status: run.status, asks };
}

test('asks the bot once more when it stalls on the head, and passes once it finishes', () => {
  assert.deepEqual(runWithStalledBot(false), { status: 0, asks: ['@codex review'] });
});

test('asks only once and exits 2 when the bot is still stuck after the second wait', () => {
  assert.deepEqual(runWithStalledBot(true), { status: 2, asks: ['@codex review'] });
});

test('ignores a review-state marker posted by an account whose login only starts with the bot name', () => {
  assert.equal(runWithStalledBot(false, 'chatgpt-codex-connector1').status, 2);
});
