import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, cpSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync, execFileSync } from 'node:child_process';

const source = readFileSync(new URL('../dotnet/scripts/workbox.sh', import.meta.url), 'utf8');
const identity = { ...process.env, GIT_AUTHOR_NAME: 'Fixture', GIT_AUTHOR_EMAIL: 'fixture@example.test',
  GIT_COMMITTER_NAME: 'Fixture', GIT_COMMITTER_EMAIL: 'fixture@example.test' };
function git(cwd: string, ...args: string[]) {
  return execFileSync('git', args, { cwd, env: identity, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
function setup() {
  const directory = mkdtempSync(join(tmpdir(), 'assembly-row-dispatch-'));
  const local = join(directory, 'local');
  const remoteHome = join(directory, 'remote-home');
  const bin = join(directory, 'bin');
  const branch = 'lane/row-log;echo${IFS}unexpected';
  const capture = join(directory, 'node-argv');
  const sshCapture = join(directory, 'ssh-argv');
  mkdirSync(join(local, 'dotnet/scripts'), { recursive: true });
  mkdirSync(join(local, 'scripts/features'), { recursive: true });
  mkdirSync(join(remoteHome, '.local/bin'), { recursive: true });
  mkdirSync(bin);
  writeFileSync(join(local, 'dotnet/scripts/workbox.sh'), source, { mode: 0o755 });
  // Source-only fixture: this stub prevents any lane data or service selection.
  writeFileSync(join(local, 'scripts/lane-env.sh'), 'LANE_NAME=fixture\nBOX_PORT=19999\nBOX_UNIT=fixture\nBOX_DATA=fixture\n');
  for (const name of ['dotnet/scripts/row-log.ts', 'scripts/features/live-rows.ts', 'scripts/features/socket.ts'])
    writeFileSync(join(local, name), '// fixture source\n');
  git(local, 'init', '-q', '-b', branch);
  git(local, 'add', '.'); git(local, 'commit', '-q', '-m', 'fixture');
  const remote = join(remoteHome, 'assembly', branch);
  mkdirSync(join(remote, '..'), { recursive: true });
  cpSync(local, remote, { recursive: true });
  writeFileSync(join(remoteHome, '.local/bin/node'), '#!/usr/bin/env bash\nprintf "%s\\0" "$@" > "$NODE_CAPTURE"\nprintf "cwd=%s\\n" "$PWD"\n', { mode: 0o755 });
  writeFileSync(join(bin, 'ssh'), `#!/usr/bin/env bash
set -euo pipefail
printf '%s\\0' "$@" > "$SSH_CAPTURE"
[[ "$#" == 2 && "$1" == workbox-test ]] || { echo 'unexpected SSH host or command' >&2; exit 98; }
exec env HOME="$REMOTE_TEST_HOME" bash -c "$2"
`, { mode: 0o755 });
  return {
    directory, local, remote, remoteHome, capture, sshCapture, branch,
    run(args: string[]) { return spawnSync('bash', ['dotnet/scripts/workbox.sh', ...args], {
      cwd: local, env: { ...identity, PATH: `${bin}:${process.env.PATH}`, REMOTE_TEST_HOME: remoteHome,
        NODE_CAPTURE: capture, SSH_CAPTURE: sshCapture }, encoding: 'utf8', timeout: 4000
    }); },
    close() { rmSync(directory, { recursive: true }); }
  };
}

function args(file: string) { return readFileSync(file, 'utf8').split('\0').slice(0, -1); }

test('row-log executes a matching checkout file and preserves hostile session/option arguments literally', () => {
  const f = setup();
  try {
    const sentinel = join(f.directory, 'injected');
    const target = `session ' ; $(touch '${sentinel}')`;
    const envPath = join(f.directory, "env file ' with spaces");
    const checked = f.run(['row-log', target, '--follow', '2', '--env', envPath, '--port', '19999']);
    assert.equal(checked.status, 0, checked.stderr);
    assert.deepEqual(args(f.capture), ['dotnet/scripts/row-log.ts', target, '--follow', '2', '--env', envPath, '--port', '19999']);
    assert.equal(args(f.sshCapture)[0], 'workbox-test');
    assert.equal(args(f.sshCapture).length, 2);
    assert.ok(checked.stdout.includes(`cwd=${f.remote}`));
    assert.equal(existsSync(sentinel), false);
    assert.ok(!existsSync(join(f.local, 'unexpected')));
  } finally { f.close(); }
});

for (const mode of ['absent', 'stale', 'dirty-remote', 'missing-source', 'dirty-local', 'detached'] as const)
  test(`row-log fails honestly for ${mode} checkout without running Node or deploying`, () => {
    const f = setup();
    try {
      if (mode === 'absent') rmSync(f.remote, { recursive: true });
      if (mode === 'stale') git(f.remote, 'commit', '--allow-empty', '-q', '-m', 'stale');
      if (mode === 'dirty-remote') writeFileSync(join(f.remote, 'scripts/features/socket.ts'), '// changed\n');
      if (mode === 'missing-source') rmSync(join(f.remote, 'scripts/features/live-rows.ts'));
      if (mode === 'dirty-local') writeFileSync(join(f.local, 'dotnet/scripts/row-log.ts'), '// changed\n');
      if (mode === 'detached') git(f.local, 'checkout', '--detach', '-q');
      const checked = f.run(['row-log', 'latest']);
      assert.equal(checked.status, 1, checked.stderr);
      assert.match(checked.stderr, /absent|stale|sources differ|named checkout/);
      assert.equal(existsSync(f.capture), false);
      if (mode === 'dirty-local' || mode === 'detached') assert.equal(existsSync(f.sshCapture), false);
    } finally { f.close(); }
  });

test('retired smoke mode remains rejected before any SSH dispatch', () => {
  const f = setup();
  try {
    const checked = f.run(['smoke']);
    assert.equal(checked.status, 2); assert.match(checked.stderr, /unknown mode: smoke/);
    assert.equal(existsSync(f.sshCapture), false); assert.equal(existsSync(f.capture), false);
  } finally { f.close(); }
});
