import assert from 'node:assert/strict';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

// A checkout the lane settings refuse: no .lane and not on main.
function checkout(): { root: string; home: string; run: (extra?: NodeJS.ProcessEnv) => ReturnType<typeof spawnSync> } {
  const top = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-any-')));
  const root = join(top, 'some-branch');
  const home = join(top, 'user');
  for (const folder of ['scripts', 'desktop', 'dotnet/scripts', 'dotnet/Assembly.Desktop', 'bin']) {
    mkdirSync(join(root, folder), { recursive: true });
  }
  mkdirSync(home);
  for (const script of ['app-dev.ts', 'seed-dev.ts', 'app-dev-remote.ts']) {
    copyFileSync(new URL(`./${script}`, import.meta.url), join(root, 'scripts', script));
  }
  writeFileSync(join(root, 'desktop/Cargo.toml'), '');
  writeFileSync(join(root, 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj'), '');
  writeFileSync(join(root, 'scripts/lane-env.sh'), 'echo "stop: no .lane" >&2\nreturn 1\n');
  writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), '[ -z "$ADAPTERS_FAIL" ] || exit 1\nprintf "%s" "$1" > "$ADAPTER_LOG"\nprintf "[]" > "$2"\n');
  writeFileSync(join(root, 'bin/dotnet'), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
  writeFileSync(join(root, 'bin/cargo'), '#!/bin/sh\ntouch "$ADAPTER_LOG.launched"\n', { mode: 0o755 });
  return {
    root,
    home,
    run: (extra = {}) => spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts')], {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, HOME: home, PATH: `${join(root, 'bin')}:${process.env.PATH}`, ADAPTER_LOG: join(root, 'adapter-path'), ADAPTERS: undefined, ...extra },
    }),
  };
}

test('a checkout with no lane still builds and launches, on data and ports of its own', () => {
  const { root, home, run } = checkout();
  try {
    const result = run();
    assert.equal(result.status, 0, String(result.stderr));
    assert.doesNotMatch(String(result.stderr), /stop:/);
    const config = JSON.parse(readFileSync(join(root, 'desktop/target/dev-backend/lane.json'), 'utf8'));
    assert.ok(config.home.startsWith(join(home, 'assembly-dev/worktrees', `${basename(root)}-`)), config.home);
    assert.equal(config.tokenFile, join(config.home, 'env'));
    assert.equal(readFileSync(join(root, 'adapter-path'), 'utf8'), join(config.home, 'adapters'));
    assert.equal(readFileSync(join(config.home, 'agents.json'), 'utf8'), '[]');
    assert.ok(config.port >= 20000 && config.port < 29000, String(config.port));
    assert.ok(config.remotePort >= 30000 && config.remotePort < 39000, String(config.remotePort));
    assert.notEqual(config.remoteTokenFile, '.config/assembly-dotnet/env');
    assert.ok(!config.remoteTokenFile.startsWith('/'), config.remoteTokenFile);
  } finally { rmSync(join(root, '..'), { recursive: true, force: true }); }
});

test('the same checkout gets the same data and ports every time', () => {
  const { root, run } = checkout();
  try {
    const read = () => { run(); return readFileSync(join(root, 'desktop/target/dev-backend/lane.json'), 'utf8'); };
    assert.equal(read(), read());
  } finally { rmSync(join(root, '..'), { recursive: true, force: true }); }
});

test('the app still opens when its agents cannot be installed, and says so', () => {
  const { root, run } = checkout();
  try {
    const result = run({ ADAPTERS_FAIL: '1' });
    assert.equal(result.status, 0, String(result.stderr));
    assert.ok(existsSync(join(root, 'adapter-path.launched')), 'the app was not launched');
    assert.match(String(result.stderr), /agents/);
  } finally { rmSync(join(root, '..'), { recursive: true, force: true }); }
});
