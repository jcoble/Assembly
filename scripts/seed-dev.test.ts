import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readdirSync, writeFileSync, symlinkSync, rmSync, existsSync, realpathSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs, profileFor, safePath, seedIfEmpty, seedMarker, takeLauncherLock, waitForOwnedClose } from './seed-dev.ts';

const worktree = (root: string) => {
  mkdirSync(join(root, 'desktop')); mkdirSync(join(root, 'dotnet', 'Assembly.Desktop'), { recursive: true });
  writeFileSync(join(root, 'desktop', 'Cargo.toml'), ''); writeFileSync(join(root, 'dotnet', 'Assembly.Desktop', 'Assembly.Desktop.csproj'), '');
};

test('the command refuses any profile but the checkout\'s own, before starting anything or creating it', () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-refuse-'));
  try {
    worktree(root);
    const script = fileURLToPath(new URL('./seed-dev.ts', import.meta.url));
    for (const other of [join(root, 'assembly-dotnet', 'mac'), join(root, 'assembly-dev'), join(root, 'assembly-dev', 'worktrees', 'other-0123456789abcdef')]) {
      const refused = spawnSync(process.execPath, [script, '--worktree', root, '--profile', other], { encoding: 'utf8', env: { ...process.env, HOME: root, PATH: '/nonexistent' } });
      assert.equal(refused.status, 1, refused.stderr);
      assert.match(refused.stderr, /only writes to/);
      assert.equal(existsSync(other), false);
    }
    assert.equal(existsSync(join(root, 'assembly-dev')), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('a profile already decided on starts no build', async () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-decided-'));
  const path = process.env.PATH, home = process.env.HOME;
  try {
    worktree(root);
    process.env.HOME = root; process.env.PATH = '/nonexistent';
    const profile = profileFor(root).home;
    mkdirSync(profile, { recursive: true }); writeFileSync(seedMarker(profile), '{}');
    await seedIfEmpty(root);
    assert.deepEqual(readdirSync(profile), ['dev-seed.json']);
  } finally { process.env.PATH = path; process.env.HOME = home; rmSync(root, { recursive: true, force: true }); }
});

test('arguments are explicit and complete', () => {
  assert.deepEqual(parseArgs(['--worktree', '/w', '--profile', '/p', '--if-empty']), { worktree: '/w', profile: '/p', ifEmpty: true });
  assert.equal(parseArgs([]).ifEmpty, false);
  for (const args of [['--worktree'], ['--profile'], ['--wat'], ['--worktree', '/w', '--worktree', '/x'], ['--worktree', '--if-empty'], ['--manifest', '/m'], ['--dry-run']]) assert.throws(() => parseArgs(args));
});
test('profile follows exact launcher identity and rejects symlink/traversal', () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-node-'));
  try {
    mkdirSync(join(root, 'desktop')); mkdirSync(join(root, 'dotnet', 'Assembly.Desktop'), { recursive: true });
    writeFileSync(join(root, 'desktop', 'Cargo.toml'), ''); writeFileSync(join(root, 'dotnet', 'Assembly.Desktop', 'Assembly.Desktop.csproj'), '');
    const value = profileFor(root, root);
    assert.equal(value.home, join(root, 'assembly-dev', 'worktrees', `${root.split('/').at(-1)}-${createHash('sha256').update(root).digest('hex').slice(0, 16)}`));
    symlinkSync(root, join(root, 'alias'));
    assert.throws(() => profileFor(join(root, 'alias'), root), /Symlink/);
    assert.throws(() => safePath(root + '/../x', false), /traversal/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('launcher lock refuses live/stale owner and releases only owned inode', () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-lock-'));
  try {
    const release = takeLauncherLock(root); assert.throws(() => takeLauncherLock(root), /launcher.lock/);
    release(); assert.equal(existsSync(join(root, 'launcher.lock')), false);
    const releaseOther = takeLauncherLock(root); rmSync(join(root, 'launcher.lock')); writeFileSync(join(root, 'launcher.lock'), 'other');
    releaseOther(); assert.equal(existsSync(join(root, 'launcher.lock')), true);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('owned stage lifecycle covers cancellation, success and failure before lock release', { timeout: 15000 }, async () => {
  const { spawn } = await import('node:child_process');
  const { connect } = await import('node:net');
  const { chmodSync, renameSync } = await import('node:fs');
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-lifecycle-'));
  const ipc = mkdtempSync(join(realpathSync('/tmp'), 'seed-ipc-'));
  const stopped = (child: ReturnType<typeof spawn>) => new Promise<number | null>(done => child.once('close', code => done(code)));
  try {
    mkdirSync(join(root, 'desktop')); mkdirSync(join(root, 'dotnet', 'Assembly.Desktop'), { recursive: true }); mkdirSync(join(root, 'tools'));
    writeFileSync(join(root, 'desktop', 'Cargo.toml'), ''); writeFileSync(join(root, 'dotnet', 'Assembly.Desktop', 'Assembly.Desktop.csproj'), '');
    const tool = join(root, 'tools', 'dotnet');
    writeFileSync(tool, `#!${process.execPath}
const { createServer } = require('node:net');
const phase = process.argv[2] === 'build' ? 'build' : 'companion';
const socket = process.env.SEED_FAKE_SOCKET + '.' + phase;
const server = createServer(client => client.on('data', data => { client.end(); server.close(() => process.exit(data.toString() === 'fail' ? 7 : 0)); }));
server.listen(socket, () => console.log(phase + '-ready:' + process.pid));
if (phase === 'companion') process.on('SIGTERM', () => console.log('companion-stopping:' + process.pid));
`);
    chmodSync(tool, 0o700);
    for (const phase of ['build', 'companion', 'success', 'build-failure', 'build-spawn-failure', 'apply-spawn-failure']) {
      const base = join(ipc, phase);
      if (phase === 'build-spawn-failure') renameSync(tool, tool + '.saved');
      const child = spawn(process.execPath, [fileURLToPath(new URL('./seed-dev.ts', import.meta.url)), '--worktree', root], {
        env: { ...process.env, HOME: root, PATH: join(root, 'tools'), SEED_FAKE_SOCKET: base }, stdio: ['ignore', 'pipe', 'pipe'],
      });
      const exited = stopped(child);
      let output = ''; let errors = ''; let closed: string | undefined; let failed: Error | undefined;
      const listeners = new Set<() => void>();
      const changed = () => { for (const listener of listeners) listener(); };
      child.stdout!.on('data', data => { output += data.toString(); changed(); });
      child.stderr!.on('data', data => { errors = (errors + data.toString()).slice(-2000); changed(); });
      child.once('error', error => { failed = error; changed(); });
      child.once('close', (code, signal) => { closed = `${code ?? signal}`; changed(); });
      const when = (text: string) => new Promise<void>((done, reject) => {
        const listener = () => {
          if (output.includes(text) || errors.includes(text)) { listeners.delete(listener); done(); }
          else if (failed || closed !== undefined) {
            listeners.delete(listener);
            reject(new Error(`Lifecycle ${phase}, awaiting '${text}': ${failed?.message ?? 'child closed ' + closed}; last stdout=${output.slice(-1000)}; stderr=${errors}`));
          }
        };
        listeners.add(listener); listener();
      });
      const complete = (name: string, action = 'complete') => new Promise<void>((done, reject) => {
        const client = connect(base + '.' + name); client.once('error', reject); client.once('connect', () => client.end(action)); client.once('close', () => done());
      });
      const lock = join(profileFor(root, root).home, 'launcher.lock');
      const unrelated = spawn(process.execPath, ['-e', "process.stdout.write('ready'); process.stdin.resume();"], { stdio: ['pipe', 'pipe', 'ignore'] });
      const unrelatedExit = stopped(unrelated);
      const unrelatedReady = new Promise<void>((done, reject) => { unrelated.stdout!.once('data', () => done()); unrelated.once('error', reject); });
      try {
        await unrelatedReady;
        if (phase === 'build-spawn-failure') {
          assert.equal(await exited, 1);
          assert.equal(existsSync(lock), false);
          continue;
        }
        await when('build-ready:');
        if (phase === 'build-failure') {
          await complete('build', 'fail');
          assert.equal(await exited, 1);
          assert.equal(output.includes('companion-ready:'), false);
          assert.equal(existsSync(lock), false);
          continue;
        }
        if (phase === 'apply-spawn-failure') {
          renameSync(tool, tool + '.saved');
          await complete('build');
          assert.equal(await exited, 1);
          assert.equal(existsSync(lock), false);
          continue;
        }
        if (phase !== 'build') {
          await complete('build'); await when('companion-ready:');
          const buildPid = Number(output.match(/build-ready:(\d+)/)![1]);
          assert.throws(() => process.kill(buildPid, 0), (error: NodeJS.ErrnoException) => error.code === 'ESRCH');
        }
        if (phase !== 'success') {
          child.kill(phase === 'build' ? 'SIGINT' : 'SIGTERM');
          if (phase === 'companion') await when('companion-stopping:');
          else await when('Cancellation requested; waiting for the owned helper build to close.');
        }
        assert.equal(existsSync(lock), true);
        await complete(phase === 'build' ? 'build' : 'companion');
        assert.equal(await exited, phase === 'build' ? 130 : phase === 'companion' ? 143 : 0);
        assert.equal(existsSync(lock), false);
        assert.doesNotThrow(() => process.kill(unrelated.pid!, 0));
        if (phase === 'build') assert.equal(output.includes('companion-ready:'), false);
        for (const match of output.matchAll(/(?:build|companion)-ready:(\d+)/g)) {
          assert.throws(() => process.kill(Number(match[1]), 0), (error: NodeJS.ErrnoException) => error.code === 'ESRCH');
        }
      } finally {
        unrelated.stdin!.end(); await unrelatedExit;
        if (existsSync(tool + '.saved')) renameSync(tool + '.saved', tool);
        if (child.exitCode === null && child.signalCode === null) {
          for (const name of ['build', 'companion']) await complete(name).catch(() => {});
          child.kill('SIGTERM'); await exited;
        }
      }
    }
  } finally { rmSync(root, { recursive: true, force: true }); rmSync(ipc, { recursive: true, force: true }); }
});


test('a child error retains its owned lock until the child close event', async () => {
  const { EventEmitter } = await import('node:events');
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'seed-error-'));
  const child = new EventEmitter();
  const release = takeLauncherLock(root);
  let settled = false;
  const result = waitForOwnedClose(child as unknown as import('node:child_process').ChildProcess)
    .finally(() => { settled = true; release(); });
  const rejected = assert.rejects(result, /owned signal failed/);
  try {
    child.emit('error', new Error('owned signal failed'));
    await Promise.resolve();
    assert.equal(settled, false);
    assert.equal(existsSync(join(root, 'launcher.lock')), true);
    child.emit('close', 1);
    await rejected;
    assert.equal(settled, true);
    assert.equal(existsSync(join(root, 'launcher.lock')), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
