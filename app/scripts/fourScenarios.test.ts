import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  buildState,
  optionValue,
  currentRunLink,
  parseAvailableGb,
  parseListeningPorts,
  parseListeners,
  portOwnerProblem,
  pickFreeDisplay,
  pickFreePort,
  descendantGroups,
  ownerFilesIn,
  ownsGroup,
  refuseOwnerData,
  refuseWhileRunning
} from './fourScenarios.ts';

test('picks the first display from :140 that is not taken', () => {
  assert.equal(pickFreeDisplay(() => false), 140);
  assert.equal(pickFreeDisplay((n) => n === 140 || n === 141), 142);
  assert.throws(() => pickFreeDisplay(() => true), /No free X display/);
});

test('picks the first port from 5300 that is not listening', () => {
  assert.equal(pickFreePort(new Set()), 5300);
  assert.equal(pickFreePort(new Set([5300, 5301, 5303])), 5302);
});

test('reads listening ports from ss output', () => {
  const ss = 'LISTEN 0 511 127.0.0.1:5300 0.0.0.0:*\nLISTEN 0 4096 [::]:22 [::]:*\n';
  assert.deepEqual([...parseListeningPorts(ss)].sort((a, b) => a - b), [22, 5300]);
});

test('reads the pids listening on a port from ss -p output', () => {
  const ss = 'LISTEN 0 511 127.0.0.1:5301 0.0.0.0:* users:(("node",pid=200,fd=20),("node",pid=201,fd=20))\nLISTEN 0 128 [::1]:5301 [::]:*\n';
  assert.deepEqual(parseListeners(ss), [200, 201, 0], 'a socket whose process this user cannot see counts as pid 0');
  assert.deepEqual(parseListeners(''), []);
});

test('only this run\'s Vite may serve its port', () => {
  const vite = { pgid: 100, folder: '/w/a/tauri-svelte-preview' };
  assert.equal(portOwnerProblem(5301, [{ pid: 101, pgid: 100, cwd: '/w/a/tauri-svelte-preview' }], vite), undefined);
  assert.match(portOwnerProblem(5301, [], vite)!, /Nothing is serving port 5301/);
  const other = portOwnerProblem(5301, [{ pid: 300, pgid: 300, cwd: '/w/b/tauri-svelte-preview' }], vite)!;
  assert.match(other, /Port 5301 is served by pid 300 \(process group 300, folder \/w\/b\/tauri-svelte-preview\), not this run's Vite \(process group 100, folder \/w\/a\/tauri-svelte-preview\)/);
  assert.match(portOwnerProblem(5301, [{ pid: 300, pgid: 300, cwd: '/w/a/tauri-svelte-preview' }], vite)!, /not this run's Vite/, 'an older Vite from the same worktree is not ours');
  assert.match(portOwnerProblem(5301, [{ pid: 101, pgid: 100, cwd: '/w/a/tauri-svelte-preview' }, { pid: 0, pgid: 0, cwd: '' }], vite)!, /a process this user cannot see/);
});

test('reads the available column from free -g', () => {
  const free = '               total        used        free      shared  buff/cache   available\nMem:              31          17           2           8          19          13\nSwap:              7           7           0\n';
  assert.equal(parseAvailableGb(free), 13);
});

test('refuses the owner data folder and anything under it', () => {
  assert.throws(() => refuseOwnerData('/home/me/.local/share', '/home/me'), /owner/);
  assert.throws(() => refuseOwnerData('/home/me/.local/share/assembly', '/home/me'), /owner/);
  assert.throws(() => refuseOwnerData('/home/me/.local/share/../share', '/home/me'), /owner/);
  assert.doesNotThrow(() => refuseOwnerData('/home/me/assembly-proof/run/data', '/home/me'));
});

test('refuses a data folder that reaches the owner data through a symlink', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'four-scenarios-home-'));
  try {
    fs.mkdirSync(path.join(home, '.local/share/assembly'), { recursive: true });
    fs.symlinkSync(path.join(home, '.local/share'), path.join(home, 'alias'));
    assert.throws(() => refuseOwnerData(path.join(home, 'alias'), home), /owner/);
    assert.throws(() => refuseOwnerData(path.join(home, 'alias/assembly/new'), home), /owner/);
    assert.doesNotThrow(() => refuseOwnerData(path.join(home, 'proof/data'), home));
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('builds the state file from what start created', () => {
  const state = buildState({
    runDir: '/p/run', display: 140, port: 5300, dataDir: '/p/run/data', worktree: '/w',
    processes: [{ name: 'xvfb', pid: 10, startedAt: 'Mon Oct 19 10:00:00 2026', log: '/p/run/xvfb.log' }]
  });
  assert.equal(state.display, ':140');
  assert.equal(state.port, 5300);
  assert.equal(state.dataDir, '/p/run/data');
  assert.deepEqual(state.processes[0], { name: 'xvfb', pid: 10, pgid: 10, startedAt: 'Mon Oct 19 10:00:00 2026', log: '/p/run/xvfb.log' });
});

test('refuses to start while the previous run still has a live process', () => {
  const state = { runDir: '/proof/old', processes: [{ name: 'xvfb', pid: 10, pgid: 10, startedAt: 'a', log: '' }] };
  assert.throws(() => refuseWhileRunning(state, () => true), /still running in \/proof\/old/);
  assert.doesNotThrow(() => refuseWhileRunning(state, () => false));
  assert.doesNotThrow(() => refuseWhileRunning(undefined, () => true));
});

test('lists only open files under the owner data folders', () => {
  const owner = ['/home/u/.local/share/assembly'];
  const open = ['/home/u/.local/share/assembly/sessions.db', '/home/u/assembly-proof/x/data/assembly/sessions.db', '/home/u/.local/share/assembly-server/x', 'socket:[123]'];
  assert.deepEqual(ownerFilesIn(open, owner), ['/home/u/.local/share/assembly/sessions.db']);
});

test('a live group still belongs to the run after its launcher exits', () => {
  const p = { pid: 10, pgid: 10, startedAt: 'Mon 1' };
  assert.equal(ownsGroup(p, true, 'Mon 1'), true);
  assert.equal(ownsGroup(p, true, ''), true);
  assert.equal(ownsGroup(p, true, 'Tue 2'), false);
  assert.equal(ownsGroup(p, false, 'Mon 1'), false);
});

test('stop reaches descendants that started their own process group', () => {
  const rows = [
    { pid: 10, ppid: 1, pgid: 10 }, // recorded launcher
    { pid: 11, ppid: 10, pgid: 10 }, // the app, same group
    { pid: 12, ppid: 11, pgid: 12 }, // agent sidecar in its own group
    { pid: 13, ppid: 12, pgid: 12 },
    { pid: 14, ppid: 13, pgid: 14 }, // language server, own group, two levels down
    { pid: 20, ppid: 1, pgid: 20 } // unrelated
  ];
  assert.deepEqual(descendantGroups(rows, [10]).sort((a, b) => a - b), [10, 12, 14]);
});


test('path options require a value instead of accepting the next flag', () => {
  for (const flag of ['--worktree', '--data', '--seed']) {
    assert.equal(optionValue([], flag), undefined);
    assert.equal(optionValue([flag, '/task/worktree'], flag), '/task/worktree');
    for (const argv of [[flag], [flag, ''], [flag, '--data', '/proof/data']]) {
      assert.throws(() => optionValue(argv, flag), new RegExp(`${flag} requires a path`));
    }
  }
});

test('two worktrees keep independent run state when one is stopped', () => {
  const linkA = currentRunLink('/proof', '/work/repo-a/review');
  const linkB = currentRunLink('/proof', '/work/repo-b/review');
  assert.notEqual(linkA, linkB, 'equal folder names in different worktrees must stay separate');
  const state = (worktree: string, pid: number) => buildState({
    runDir: `/proof/run-${pid}`, display: 140 + pid, port: 5300 + pid,
    dataDir: `/proof/run-${pid}/data`, worktree,
    processes: [{ name: 'app', pid, startedAt: 'now', log: '' }]
  });
  const runs = new Map([[linkA, state('/work/repo-a/review', 10)], [linkB, state('/work/repo-b/review', 20)]]);
  const live = new Set([10, 20]);
  const isLive = (p: { pid: number }) => live.has(p.pid);
  assert.throws(() => refuseWhileRunning(runs.get(linkA), isLive), /still running/);
  assert.throws(() => refuseWhileRunning(runs.get(linkB), isLive), /still running/);
  for (const p of runs.get(linkA)!.processes) live.delete(p.pid);
  assert.doesNotThrow(() => refuseWhileRunning(runs.get(linkA), isLive));
  assert.throws(() => refuseWhileRunning(runs.get(linkB), isLive), /still running/);
  assert.deepEqual([...live], [20]);
});

// A checkout whose tools are shims: `pnpm dev` is a fake Vite that really listens on its port,
// `pnpm exec tauri` records what the app would get, and `ss` is the real one.
function startFixture() {
  const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'four-scenarios-adapters-'));
  const preview = path.join(fixture, 'checkout/tauri-svelte-preview');
  const adapters = path.join(preview, 'src-tauri/adapters');
  const bin = path.join(fixture, 'bin');
  for (const dir of [adapters, bin, path.join(preview, 'scripts'), path.join(fixture, 'data')]) fs.mkdirSync(dir, { recursive: true });
  fs.copyFileSync(new URL('./dev-app.sh', import.meta.url), path.join(preview, 'scripts/dev-app.sh'));
  fs.writeFileSync(path.join(adapters, 'manifest.json'), '{}');
  for (const name of ['codex-acp', 'claude-agent-acp', 'agy-acp', 'agy_acp_server.par']) fs.writeFileSync(path.join(adapters, name), name, { mode: 0o755 });
  const realSs = execFileSync('sh', ['-c', 'command -v ss'], { encoding: 'utf8' }).trim();
  const shim = `#!${process.execPath}
const fs = require('node:fs'), path = require('node:path'), cp = require('node:child_process'), http = require('node:http');
const args = process.argv.slice(2), name = path.basename(process.argv[1]), receipt = process.env.RECEIPT;
if (name === 'free') console.log('Mem: 31 0 0 0 0 20');
if (name === 'Xvfb') {
  // Like Xvfb: refuse a display whose lock exists, else hold the lock (with this pid) until stopped.
  const lock = '/tmp/.X' + args[0].slice(1) + '-lock';
  if (process.env.TAKEN_DISPLAY === args[0] || fs.existsSync(lock)) { console.error('Server is already active for display ' + args[0].slice(1)); process.exit(1); }
  fs.writeFileSync(lock, String(process.pid).padStart(10) + '\\n');
  fs.writeFileSync(receipt + '.xvfb-' + args[0].slice(1), String(process.pid));
  const drop = () => { try { if (Number(fs.readFileSync(lock, 'utf8')) === process.pid) fs.unlinkSync(lock); } catch {} process.exit(0); };
  process.on('SIGTERM', drop);
  setInterval(() => {}, 1000);
}
if (name === 'xwininfo') console.log(fs.existsSync(receipt) ? '1710x990 "Assembly"' : '1710x990');
if (name === 'assembly-native-ui') fs.writeFileSync(args[1], Buffer.alloc(21000));
if (name === 'ss') {
  // HIDE_PORT is a port another process took after the free-port check (\`ss -H -ltn\`) looked.
  const out = cp.execFileSync(${JSON.stringify(realSs)}, args, { encoding: 'utf8' });
  const picking = args.join(' ') === '-H -ltn';
  process.stdout.write(out.split('\\n').filter((l) => !(picking && process.env.HIDE_PORT && l.includes(':' + process.env.HIDE_PORT + ' '))).join('\\n'));
}
if (name === 'pnpm' && args[0] === 'dev') {
  const port = Number(args[args.indexOf('--port') + 1]);
  if (process.env.FAKE_VITE === 'crash') { console.error('boom'); process.exit(1); }
  const server = http.createServer((q, s) => s.end('fake vite'));
  server.on('error', (e) => { console.error(e.code === 'EADDRINUSE' ? 'Error: Port ' + port + ' is already in use' : e.message); process.exit(1); });
  server.listen(port, '127.0.0.1', () => fs.writeFileSync(receipt + '.vite-' + port, String(process.pid)));
}
if (name === 'pnpm' && args[0] === 'tauri:dev') cp.execFileSync('sh', ['scripts/dev-app.sh', ...args.slice(1)], { stdio: 'inherit' });
if (name === 'pnpm' && (args[0] === 'tauri' || args[0] === 'exec')) {
  const port = Number(/127\\.0\\.0\\.1:(\\d+)/.exec(args.join(' '))[1]);
  if (process.env.TAKE_OVER) {
    // The run's Vite dies and something else serves its port while the app is starting.
    process.kill(Number(fs.readFileSync(receipt + '.vite-' + port, 'utf8')), 'SIGKILL');
    const other = cp.spawn(process.execPath, ['-e', 'require("node:http").createServer((q, s) => s.end("other")).listen(' + port + ', "127.0.0.1")'], { detached: true, stdio: 'ignore' });
    fs.writeFileSync(receipt + '.other', String(other.pid));
    cp.execFileSync('sh', ['-c', 'for i in $(seq 50); do ${realSs} -H -ltn "sport = :' + port + '" | grep -q . && exit 0; sleep 0.1; done; exit 1']);
  }
  fs.writeFileSync(receipt, JSON.stringify({ env: process.env, args, port }));
}
`;
  for (const name of ['pnpm', 'Xvfb', 'free', 'ss', 'xwininfo', 'assembly-native-ui']) fs.writeFileSync(path.join(bin, name), shim, { mode: 0o755 });
  const receipt = path.join(fixture, 'receipt.json');
  const env: NodeJS.ProcessEnv = { ...process.env, HOME: fixture, PATH: `${bin}:${process.env.PATH}`, RECEIPT: receipt, CARGO_TARGET_DIR: path.join(fixture, 'target'), CLAUDECODE: 'parent-session', CODEX_CONFIG: JSON.stringify({ 'mcp_servers.assembly.default_tools_approval_mode': 'never' }) };
  for (const key of Object.keys(env)) if (/^MCB_.*_ACP_(PATH|SHA256)$/.test(key) || key === 'HIDE_PORT' || key === 'TAKEN_DISPLAY' || key === 'FAKE_VITE' || key === 'TAKE_OVER') delete env[key];
  const script = new URL('./fourScenarios.ts', import.meta.url).pathname;
  const checkout = path.join(fixture, 'checkout');
  const start = (extra: NodeJS.ProcessEnv = {}) => {
    try {
      const out = execFileSync(process.execPath, ['--experimental-strip-types', script, 'start', '--worktree', checkout, '--data', path.join(fixture, 'data')], { env: { ...env, ...extra }, encoding: 'utf8', stdio: 'pipe' });
      return { code: 0, out, err: '' };
    } catch (error) {
      const e = error as { status: number; stdout: string; stderr: string };
      return { code: e.status, out: e.stdout, err: e.stderr };
    }
  };
  const readReceipt = () => JSON.parse(fs.readFileSync(receipt, 'utf8'));
  const cleanup = () => {
    try { execFileSync(process.execPath, ['--experimental-strip-types', script, 'stop', '--worktree', checkout], { env, stdio: 'pipe' }); } catch { /* nothing started */ }
    for (const file of fs.readdirSync(fixture).filter((f) => f.startsWith('receipt.json.'))) {
      const pid = Number(fs.readFileSync(path.join(fixture, file), 'utf8'));
      try { process.kill(pid, 'SIGKILL'); } catch { /* already gone */ }
      const lock = `/tmp/.X${file.split('.xvfb-')[1]}-lock`;
      if (file.includes('.xvfb-') && fs.existsSync(lock) && Number(fs.readFileSync(lock, 'utf8')) === pid) fs.rmSync(lock);
    }
    fs.rmSync(fixture, { recursive: true, force: true });
  };
  return { fixture, preview, adapters, receipt, start, readReceipt, cleanup };
}

const firstFreePort = () => pickFreePort(parseListeningPorts(execFileSync('ss', ['-H', '-ltn'], { encoding: 'utf8' })));

test('lane startup supplies the checkout adapters, its own Vite and the build lock to Tauri', () => {
  const f = startFixture();
  try {
    const result = f.start();
    assert.equal(result.code, 0, result.err);
    const actual = f.readReceipt();
    for (const [prefix, name] of [['MCB_CODEX_ACP', 'codex-acp'], ['MCB_CLAUDE_AGENT_ACP', 'claude-agent-acp'], ['MCB_AGY_ACP', 'agy-acp']]) {
      assert.equal(path.resolve(actual.env[`${prefix}_PATH`]), path.join(f.adapters, name));
      assert.equal(actual.env[`${prefix}_SHA256`], createHash('sha256').update(name).digest('hex'));
    }
    assert.equal(actual.env.CLAUDECODE, undefined);
    assert.equal(actual.env.CODEX_CONFIG, undefined);
    assert.deepEqual(actual.args.slice(0, 5), ['exec', 'tauri', 'dev', '--config', 'src-tauri/tauri.dev.attach.conf.json']);
    assert.ok(fs.existsSync(`${f.receipt}.vite-${actual.port}`), 'the app was pointed at the port its own Vite listens on');
    assert.equal(actual.env.CARGO_TARGET_DIR, path.join(f.fixture, 'target'));
    const runner = actual.args[actual.args.indexOf('--runner') + 1];
    assert.match(fs.readFileSync(runner, 'utf8'), /build-lock\.sh/, 'tauri compiles through the build lock');
  } finally {
    f.cleanup();
  }
});

test('a port taken after the free-port check is not used; start moves to the next one', async () => {
  const f = startFixture();
  const taken = firstFreePort();
  const other = http.createServer((q, s) => s.end('other lane'));
  await new Promise<void>((resolve) => other.listen(taken, '127.0.0.1', resolve));
  try {
    const result = f.start({ HIDE_PORT: String(taken) });
    assert.equal(result.code, 0, result.err);
    assert.match(result.out, new RegExp(`Port ${taken} was taken before Vite could listen on it; trying the next port`));
    const actual = f.readReceipt();
    assert.notEqual(actual.port, taken);
    assert.ok(fs.existsSync(`${f.receipt}.vite-${actual.port}`), 'the app was pointed at the port its own Vite listens on');
  } finally {
    other.close();
    f.cleanup();
  }
});

test('a display taken after the free-display check is not used; start moves to the next one', () => {
  const f = startFixture();
  const taken = pickFreeDisplay((n) => fs.existsSync(`/tmp/.X${n}-lock`) || fs.existsSync(`/tmp/.X11-unix/X${n}`));
  try {
    const result = f.start({ TAKEN_DISPLAY: `:${taken}` });
    assert.equal(result.code, 0, result.err);
    assert.match(result.out, new RegExp(`Display :${taken} was taken before Xvfb could start on it; trying the next display`));
    assert.match(result.out, /Display: :(\d+)/);
    assert.notEqual(Number(/Display: :(\d+)/.exec(result.out)![1]), taken);
  } finally {
    f.cleanup();
  }
});

test('refuses and stops everything when its Vite exits without serving the port', () => {
  const f = startFixture();
  try {
    const result = f.start({ FAKE_VITE: 'crash' });
    assert.notEqual(result.code, 0);
    assert.match(result.err, /Vite exited before serving port \d+/);
    assert.match(result.err, /Stopped everything this run started/);
    assert.equal(fs.existsSync(f.receipt), false, 'the app was never launched');
  } finally {
    f.cleanup();
  }
});

test('refuses and stops everything when something else serves the port while the app starts', () => {
  const f = startFixture();
  try {
    const result = f.start({ TAKE_OVER: '1' });
    assert.notEqual(result.code, 0);
    assert.match(result.err, /Port \d+ is served by pid \d+ .*not this run's Vite/);
    assert.match(result.err, /Stopped everything this run started/);
  } finally {
    f.cleanup();
  }
});
