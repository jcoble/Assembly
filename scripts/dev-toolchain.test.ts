import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { ensureNpm, ensureTools, packages, type ToolVersions } from './dev-toolchain.ts';

function fixture() {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'dev-toolchain-'));
  const write = (path: string, text: string) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text); };
  write(join(root, 'scripts/adapters/claude-launcher.ts'), 'launcher');
  return { root, write, close: () => rmSync(root, { recursive: true, force: true }) };
}
const fakeNpm = async () => '/fixture/npm/bin';
test('dependency check skips all install/build work on the second launch, and keeps current adapters on a CLI-only update', async () => {
  const f = fixture(), adapters = join(f.root, 'adapters'), descriptor = join(f.root, 'profile/agents.json');
  const versions = Object.fromEntries(packages.map(name => [name, '1.2.3'])) as ToolVersions;
  const calls: string[][] = [];
  let desired = versions;
  const run = async (tool: string, args: string[], options: any) => {
    calls.push([tool, ...args]);
    if (tool === 'npm') {
      const prefix = args[args.indexOf('--prefix') + 1];
      for (const name of packages.slice(2)) f.write(join(prefix, 'node_modules', name, 'package.json'), JSON.stringify({ version: desired[name] }));
      for (const id of ['claude', 'codex']) f.write(join(prefix, 'node_modules/.bin', id), 'cli');
    } else if (tool === process.execPath) {
      f.write(join(args.at(-1)!, 'assembly-adapters-linux-x64.json'), '{}');
    } else if (tool === 'bash') {
      const bundle = join(adapters, 'bundles', 'validated');
      const bin = options.env.PATH.split(':')[0];
      f.write(join(bundle, 'claude-launcher.ts'), 'launcher');
      f.write(join(bundle, 'node_modules/@agentclientprotocol/codex-acp/dist/index.js'), 'adapter');
      for (const name of packages.slice(0, 2)) f.write(join(bundle, 'node_modules', name, 'package.json'), JSON.stringify({ version: desired[name] }));
      f.write(descriptor, JSON.stringify([{ id: 'claude', cli: join(bin, 'claude'), args: [join(bundle, 'claude-launcher.ts')] }, { id: 'codex', cli: join(bin, 'codex'), args: [join(bundle, 'node_modules/@agentclientprotocol/codex-acp/dist/index.js')] }]));
    }
    return '';
  };
  try {
    assert.equal((await ensureTools(f.root, adapters, descriptor, versions, new AbortController().signal, run, 'linux-x64', fakeNpm)).changed, true);
    calls.length = 0;
    assert.equal((await ensureTools(f.root, adapters, descriptor, versions, new AbortController().signal, run, 'linux-x64', fakeNpm)).changed, false);
    assert.equal(calls.length, 0);
    desired = { ...versions, '@openai/codex': '1.2.4' };
    await ensureTools(f.root, adapters, descriptor, desired, new AbortController().signal, run, 'linux-x64', fakeNpm);
    assert.equal(calls.filter(call => call[0] === 'npm').length, 1);
    assert.equal(calls.filter(call => call[0] === process.execPath).length, 0, 'current ACP adapters must not rebuild for a CLI update');
  } finally { f.close(); }
});
test('a registry that goes backwards never puts an older adapter or CLI over a newer one', async () => {
  const f = fixture(), adapters = join(f.root, 'adapters'), descriptor = join(f.root, 'profile/agents.json');
  const installed = Object.fromEntries(packages.map(name => [name, '2.0.0'])) as ToolVersions;
  const asked: string[] = [];
  let bundled = '{}';
  const run = async (tool: string, args: string[], options?: { env?: NodeJS.ProcessEnv }) => {
    if (tool === 'npm') {
      const prefix = args[args.indexOf('--prefix') + 1];
      asked.push(...args.filter(arg => arg.includes('@', 1)));
      for (const arg of args.filter(arg => arg.includes('@', 1) && !arg.startsWith('--'))) { const at = arg.lastIndexOf('@'); f.write(join(prefix, 'node_modules', arg.slice(0, at), 'package.json'), JSON.stringify({ version: arg.slice(at + 1) })); }
    } else if (tool === process.execPath) {
      const [wanted, stage] = args.slice(-2);
      asked.push('bundle ' + wanted);
      bundled = wanted;
      f.write(join(stage, 'assembly-adapters-linux-x64.json'), '{}');
    } else if (tool === 'bash') {
      const bundle = join(adapters, 'bundles/one');
      f.write(join(bundle, 'claude-launcher.ts'), 'launcher');
      const bin = (options?.env?.PATH ?? '').split(':')[0];
      for (const [name, version] of Object.entries(JSON.parse(bundled) as Record<string, string>)) f.write(join(bundle, 'node_modules/@agentclientprotocol', name, 'package.json'), JSON.stringify({ version }));
      f.write(join(bundle, 'node_modules/@agentclientprotocol/codex-acp/dist/index.js'), 'codex');
      for (const id of ['claude', 'codex']) f.write(join(bin, id), id);
      f.write(descriptor, JSON.stringify([{ id: 'claude', cli: join(bin, 'claude'), args: [join(bundle, 'claude-launcher.ts')] }, { id: 'codex', cli: join(bin, 'codex'), args: [join(bundle, 'node_modules/@agentclientprotocol/codex-acp/dist/index.js')] }]));
    }
    return '';
  };
  const ensure = (versions: ToolVersions) => ensureTools(f.root, adapters, descriptor, versions, new AbortController().signal, run as any, 'linux-x64', fakeNpm);
  try {
    const first = await ensure(installed);
    asked.length = 0;
    const older = Object.fromEntries(packages.map(name => [name, '1.9.0'])) as ToolVersions;
    const again = await ensure(older);
    assert.equal(again.changed, false, 'an older ask changes nothing');
    assert.equal(again.key, first.key);
    assert.deepEqual(asked, []);
    // One moved forward: that one updates, and the others stay.
    const mixed = await ensure({ ...older, '@openai/codex': '2.1.0' });
    assert.equal(mixed.changed, true);
    assert.ok(asked.includes('@openai/codex@2.1.0'), asked.join());
    assert.ok(asked.includes('@anthropic-ai/claude-code@2.0.0'), asked.join());
    assert.equal(asked.some(line => line.includes('1.9.0')), false, asked.join());
  } finally { f.close(); }
});
test('a failed adapter build leaves nothing the next launch would mistake for a finished one', async () => {
  const f = fixture(), adapters = join(f.root, 'adapters'), descriptor = join(f.root, 'profile/agents.json');
  const versions = Object.fromEntries(packages.map(name => [name, '1.2.3'])) as ToolVersions;
  let builds = 0;
  const run = async (tool: string, args: string[]) => {
    if (tool === 'npm') {
      const prefix = args[args.indexOf('--prefix') + 1];
      for (const name of packages.slice(2)) f.write(join(prefix, 'node_modules', name, 'package.json'), JSON.stringify({ version: '1.2.3' }));
    } else if (tool === process.execPath) {
      builds++;
      f.write(join(args.at(-1)!, 'assembly-adapters-linux-x64.json'), '{"unproven":true}');
      if (builds === 1) throw Error('smoke failed');
    } else if (tool === 'bash') throw Error('stop after the build');
    return '';
  };
  try {
    const ensure = () => ensureTools(f.root, adapters, descriptor, versions, new AbortController().signal, run as any, 'linux-x64', fakeNpm);
    await assert.rejects(ensure(), /smoke failed/);
    await assert.rejects(ensure(), /stop after the build/);
    assert.equal(builds, 2, 'the second launch must rebuild, not reuse the failed output');
  } finally { f.close(); }
});
// Workbox Test has no npm; a fresh lane there must install one rather than fail.
function fakeNpmArchive(root: string) {
  const source = join(root, 'source/package/bin');
  mkdirSync(source, { recursive: true });
  writeFileSync(join(source, 'npm-cli.js'), '#!/bin/sh\necho pinned-npm\n', { mode: 0o755 });
  const archive = join(root, 'npm.tgz');
  execFileSync('tar', ['-czf', archive, '-C', join(root, 'source'), 'package']);
  return { archive, pin: { version: '1.2.3', integrity: 'sha512-' + createHash('sha512').update(readFileSync(archive)).digest('base64') } };
}
const realTools = (archive: string, calls: string[]) => async (tool: string, args: string[]) => {
  calls.push(tool);
  if (tool === 'curl') { execFileSync('cp', [archive, args[args.indexOf('-o') + 1]]); return ''; }
  return execFileSync(tool, args, { encoding: 'utf8' });
};
test('a machine with no npm gets the pinned one, usable from PATH, and only downloads it once', async () => {
  const f = fixture();
  try {
    const { archive, pin } = fakeNpmArchive(f.root), calls: string[] = [], adapters = join(f.root, 'adapters');
    const bin = await ensureNpm(adapters, new AbortController().signal, realTools(archive, calls) as any, pin, '');
    assert.equal(execFileSync(join(bin, 'npm'), { encoding: 'utf8' }).trim(), 'pinned-npm');
    assert.equal(await ensureNpm(adapters, new AbortController().signal, realTools(archive, calls) as any, pin, ''), bin);
    assert.deepEqual(calls, ['curl', 'tar']);
  } finally { f.close(); }
});
test('a machine that already has npm downloads nothing', async () => {
  const f = fixture();
  try {
    const own = join(f.root, 'own-bin'), calls: string[] = [];
    f.write(join(own, 'npm'), '#!/bin/sh\n'); execFileSync('chmod', ['+x', join(own, 'npm')]);
    assert.equal(await ensureNpm(join(f.root, 'adapters'), new AbortController().signal, realTools('', calls) as any, undefined, own), own);
    assert.deepEqual(calls, []);
  } finally { f.close(); }
});
test('an npm archive that differs from the pin is refused and leaves nothing installed', async () => {
  const f = fixture();
  try {
    const { archive, pin } = fakeNpmArchive(f.root), adapters = join(f.root, 'adapters'), calls: string[] = [];
    await assert.rejects(ensureNpm(adapters, new AbortController().signal, realTools(archive, calls) as any, { ...pin, integrity: 'sha512-wrong' }, ''), /pinned integrity/);
    assert.deepEqual(calls, ['curl']);
    assert.equal(existsSync(join(adapters, 'npm', pin.version)), false);
  } finally { f.close(); }
});
