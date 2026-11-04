import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, rmSync, watch } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { waitForIdle } from './dev-remote-idle.ts';

test('busy sync waits on process exit, then proceeds without another launch or a timer', async () => {
  const root = mkdtempSync(join(tmpdir(), 'remote-idle-'));
  const child = spawn(process.execPath, ['-e', 'process.stdin.resume();console.log("ready")'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = once(child, 'close');
  let alive = true, reads = 0;
  child.once('exit', () => alive = false);
  await once(child.stdout, 'data');
  const watching = watch(root);
  const armed = once(watching, 'change');
  const owner = new AbortController();
  const done = waitForIdle(async () => { reads++; return ['123', ...(alive ? [String(child.pid)] : [])]; }, '123', root, true, owner.signal);
  try {
    await armed;
    watching.close();
    assert.equal(alive, true, 'sync must never end a running session');
    child.stdin.end();
    await exited;
    await done;
    assert.ok(reads <= 3, `expected event-based checks, saw ${reads}`);
  } finally { watching.close(); owner.abort(); child.stdin.end(); await exited; await done.catch(() => {}); rmSync(root, { recursive: true, force: true }); }
});

test('closing the owner cancels the idle wait and leaves the running session alive', async () => {
  const root = mkdtempSync(join(tmpdir(), 'remote-idle-cancel-'));
  const child = spawn(process.execPath, ['-e', 'process.stdin.resume();console.log("ready")'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = once(child, 'close');
  await once(child.stdout, 'data');
  const watching = watch(root);
  const armed = once(watching, 'change');
  const owner = new AbortController();
  const done = waitForIdle(async () => ['123', String(child.pid)], '123', root, true, owner.signal);
  try {
    await armed; watching.close(); owner.abort();
    await assert.rejects(done, /cancelled/);
    assert.equal(child.exitCode, null);
  } finally { watching.close(); child.stdin.end(); await exited; rmSync(root, { recursive: true, force: true }); }
});

test('manual serve refuses a busy host and a replaced host is never restarted', async () => {
  await assert.rejects(waitForIdle(async () => ['123', '456'], '123', '/unused', false, new AbortController().signal), /busy/);
  await assert.rejects(waitForIdle(async () => ['456'], '123', '/unused', true, new AbortController().signal), /changed/);
});
