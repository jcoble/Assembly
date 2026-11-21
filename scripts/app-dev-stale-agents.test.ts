import assert from 'node:assert/strict';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

// A checkout that keeps its own data, as the main one does, started with an agents file already in its profile.
function launch(agents: (root: string) => string) {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-stale-agents-')));
  for (const folder of ['scripts', 'desktop', 'dotnet/scripts', 'bin', 'data/mac']) mkdirSync(join(root, folder), { recursive: true });
  for (const script of ['app-dev.ts', 'app-dev-remote.ts', 'seed-dev.ts']) copyFileSync(new URL(`./${script}`, import.meta.url), join(root, 'scripts', script));
  writeFileSync(join(root, 'scripts/lane-env.sh'), 'DATA="$PWD/data"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
  writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), 'printf "installed" > "$2"\n');
  writeFileSync(join(root, 'bin/dotnet'), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
  writeFileSync(join(root, 'bin/cargo'), '#!/bin/sh\ntouch "$LAUNCHED"\n', { mode: 0o755 });
  const file = join(root, 'data/mac/agents.json'), launched = join(root, 'launched');
  const before = agents(root);
  writeFileSync(file, before);
  const env: NodeJS.ProcessEnv = { ...process.env, PATH: `${join(root, 'bin')}:${process.env.PATH}`, LAUNCHED: launched };
  delete env.ADAPTERS;
  const result = spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], { cwd: root, env, encoding: 'utf8' });
  return { root, result, before, after: readFileSync(file, 'utf8'), launched };
}
const agentAt = (command: string, entry: string) => JSON.stringify([{ id: 'one', name: 'One', command, args: [entry], env: {} }]);
const adapterIn = (folder: string) => join(folder, 'node_modules/@agentclientprotocol/one-acp/dist/index.js');
function installedAdapter(root: string) {
  const entry = adapterIn(join(root, 'installed'));
  mkdirSync(join(entry, '..'), { recursive: true });
  writeFileSync(entry, '');
  return entry;
}

test('agents whose adapter is gone are installed again before the app opens', () => {
  const { root, result, after, launched } = launch(root => agentAt(process.execPath, adapterIn(join(root, 'removed'))));
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.equal(after, 'installed');
    assert.ok(existsSync(launched), 'the app was not launched');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('agents whose program is gone are installed again before the app opens', () => {
  const { root, result, after } = launch(root => agentAt(join(root, 'removed/node'), installedAdapter(root)));
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.equal(after, 'installed');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// The installer cannot repair either of these, so installing would only repeat at every start.
test('an agents file that cannot be read is left as it is', () => {
  const { root, result, before, after, launched } = launch(() => '{ not json');
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.equal(after, before);
    assert.ok(existsSync(launched), 'the app was not launched');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('an entry the installer does not provide causes no install when its file is gone', () => {
  const { root, result, before, after } = launch(root => agentAt(process.execPath, join(root, 'removed/custom/index.js')));
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.equal(after, before);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('agents whose adapter is in place are left as they are', () => {
  const { root, result, before, after, launched } = launch(root => agentAt(process.execPath, installedAdapter(root)));
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.equal(after, before);
    assert.ok(existsSync(launched), 'the app was not launched');
  } finally { rmSync(root, { recursive: true, force: true }); }
});
