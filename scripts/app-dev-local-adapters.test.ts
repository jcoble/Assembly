import assert from 'node:assert/strict';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

const versions = '{"claude-agent-acp":"1.2.3","codex-acp":"4.5.6"}';
// Stands in for the real packager, which needs the registry: it records what it is asked for.
const packager = `
import { appendFileSync, mkdirSync } from 'node:fs';
const [command, ...args] = process.argv.slice(2);
appendFileSync(process.env.PACKAGER_LOG, [command, ...args].join(' ') + '\\n');
if (command === 'resolve') console.log(process.env.RESOLVE_FAIL ? '' : '${versions}');
if (process.env.RESOLVE_FAIL) process.exit(5);
if (command === 'build') {
  if (process.env.BUILD_FAIL) process.exit(6);
  mkdirSync(args[2], { recursive: true });
}
`;
// Refuses what it fetches itself, as the installer does a release without what this checkout's agents need.
const refusesReleases = '[ -n "$3" ] || { echo "no usable release" >&2; exit 1; }\nprintf "%s" "$3" > "$LOCAL_LOG"\nprintf "[]" > "$2"\n';
const acceptsReleases = 'printf "[]" > "$2"\n';

function checkout(installer: string, extra: NodeJS.ProcessEnv = {}) {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-local-adapters-')));
  for (const folder of ['scripts', 'desktop', 'dotnet/scripts', 'bin']) mkdirSync(join(root, folder), { recursive: true });
  copyFileSync(new URL('./app-dev.ts', import.meta.url), join(root, 'scripts/app-dev.ts'));
    copyFileSync(new URL('./app-dev-remote.ts', import.meta.url), join(root, 'scripts/app-dev-remote.ts'));
  copyFileSync(new URL('./seed-dev.ts', import.meta.url), join(root, 'scripts/seed-dev.ts'));
  writeFileSync(join(root, 'scripts/package-adapters.ts'), packager);
  writeFileSync(join(root, 'scripts/lane-env.sh'), 'DATA="$PWD/data"\nDESKTOP_PORT=18887\nTOKEN_FILE=test\nBOX_PORT=9887\nBOX_TOKEN_FILE=test\n');
  writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), installer);
  writeFileSync(join(root, 'bin/dotnet'), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
  writeFileSync(join(root, 'bin/cargo'), '#!/bin/sh\ntouch "$LAUNCHED"\n', { mode: 0o755 });
  const paths = { packager: join(root, 'packager'), local: join(root, 'local'), launched: join(root, 'launched') };
  const env: NodeJS.ProcessEnv = {
    ...process.env, PATH: `${join(root, 'bin')}:${process.env.PATH}`,
    PACKAGER_LOG: paths.packager, LOCAL_LOG: paths.local, LAUNCHED: paths.launched, ...extra,
  };
  delete env.ADAPTERS;
  const result = spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], { cwd: root, env, encoding: 'utf8' });
  return { root, result, ...paths };
}

test('agents with no usable release are built here, installed, and the app opens with them', () => {
  const { root, result, packager, local, launched } = checkout(refusesReleases);
  try {
    assert.equal(result.status, 0, result.stderr);
    const assets = join(root, 'desktop/target/local-adapters');
    assert.deepEqual(readFileSync(packager, 'utf8').trim().split('\n'), [
      'resolve',
      `build ${process.platform}-${process.arch} ${versions} ${assets}`,
    ]);
    assert.equal(readFileSync(local, 'utf8'), assets);
    assert.equal(readFileSync(join(root, 'data/mac/agents.json'), 'utf8'), '[]');
    assert.ok(existsSync(launched), 'the app was not launched');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('agents that install from a release are not built', () => {
  const { root, result, packager, launched } = checkout(acceptsReleases);
  try {
    assert.equal(result.status, 0, result.stderr);
    assert.ok(!existsSync(packager), 'the packager ran');
    assert.ok(existsSync(launched), 'the app was not launched');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

for (const [name, failure] of [['resolving', 'RESOLVE_FAIL', 5], ['building', 'BUILD_FAIL', 6]] as const) {
  test(`a failure ${name} the agents still opens and reports the failure`, () => {
    const { root, result, local, launched } = checkout(refusesReleases, { [failure]: '1' });
    try {
      assert.equal(result.status, 0, result.stderr);
      assert.match(result.stderr, /agents could not be updated/);
      assert.ok(!existsSync(local), 'something was installed');
      assert.ok(existsSync(launched), 'the app was not launched');
    } finally { rmSync(root, { recursive: true, force: true }); }
  });
}
