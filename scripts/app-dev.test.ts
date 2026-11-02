import assert from 'node:assert/strict';
import { copyFileSync, existsSync, mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { test } from 'node:test';

function fixture() {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'assembly-launcher-')));
  const root = join(directory, 'selected worktree');
  const user = join(directory, 'user');
  const bin = join(directory, 'bin');
  const target = join(directory, 'cargo-cache');
  const log = join(directory, 'commands');
  for (const folder of [join(root, 'scripts'), join(root, 'desktop'), join(root, 'dotnet/Assembly.Desktop'), join(root, 'dotnet/scripts'), bin, user]) {
    mkdirSync(folder, { recursive: true });
  }
  copyFileSync(new URL('./app-dev.ts', import.meta.url), join(root, 'scripts/app-dev.ts'));
  writeFileSync(join(root, 'desktop/Cargo.toml'), '[package]\nname="fixture"\nversion="0.1.0"\n');
  writeFileSync(join(root, 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj'), '<Project/>');
  writeFileSync(join(root, 'dotnet/scripts/agents-file.sh'), 'printf "agents\\n" >> "$COMMAND_LOG"\nprintf \'[]\' > "$2"\n');
  writeFileSync(join(bin, 'dotnet'), `#!/bin/sh
printf 'dotnet\\n' >> "$COMMAND_LOG"
test "\${DOTNET_EXIT:-0}" = 0 || exit "$DOTNET_EXIT"
for argument do output=$argument; done
mkdir -p "$output"
touch "$output/Assembly.Desktop"
`, { mode: 0o755 });
  writeFileSync(join(bin, 'cargo'), `#!/bin/sh
printf 'cargo %s\\n' "$1" >> "$COMMAND_LOG"
if [ "$1" = metadata ]; then
  printf '{"target_directory":"%s"}\\n' "$TEST_TARGET_DIR"
else
  test "\${CARGO_EXIT:-0}" = 0 || exit "$CARGO_EXIT"
  mkdir -p "$TEST_TARGET_DIR/debug"
  cat > "$TEST_TARGET_DIR/debug/assembly-desktop" <<'APP'
#!/bin/sh
printf 'native %s remote=%s\\n' "$ASSEMBLY_BACKEND_DIR" "\${ASSEMBLY_REMOTE:-}" >> "$COMMAND_LOG"
if [ -n "\${WAIT_NATIVE:-}" ]; then
  printf 'READY\\n'
  read gate
fi
APP
  chmod +x "$TEST_TARGET_DIR/debug/assembly-desktop"
fi
`, { mode: 0o755 });
  const env = { ...process.env, HOME: user, PATH: `${bin}:${process.env.PATH}`, COMMAND_LOG: log, TEST_TARGET_DIR: target, ASSEMBLY_REMOTE: 'must-not-connect' };
  const launch = (args: string[] = [], extra = {}) => spawnSync(process.execPath, [join(root, 'scripts/app-dev.ts'), ...args], {
    cwd: directory, env: { ...env, ...extra }, encoding: 'utf8',
  });
  const config = (selected = root) => {
    const backend = join(selected, 'desktop/target/dev-backend');
    const current = JSON.parse(readFileSync(join(backend, 'current.json'), 'utf8'));
    return JSON.parse(readFileSync(join(backend, current.build, 'lane.json'), 'utf8'));
  };
  return { directory, root, user, log, env, launch, config, close: () => rmSync(directory, { recursive: true, force: true }) };
}

test('any worktree launches with persistent isolated local data and no implicit remote', () => {
  const f = fixture();
  try {
    const daily = join(f.user, 'assembly-dotnet/mac');
    mkdirSync(daily, { recursive: true });
    writeFileSync(join(daily, 'assembly.db'), 'daily sentinel');
    const first = f.launch();
    assert.equal(first.status, 0, first.stderr);
    const config = f.config();
    assert.ok(config.port > 0);
    assert.ok(config.home.startsWith(join(f.user, 'assembly-dev/worktrees') + '/'));
    assert.notEqual(config.home, daily);
    assert.equal(readFileSync(join(daily, 'assembly.db'), 'utf8'), 'daily sentinel');
    assert.equal(readFileSync(join(config.home, 'agents.json'), 'utf8'), '[]');
    assert.match(first.stdout, /Local database:/);
    assert.match(readFileSync(f.log, 'utf8'), /native .* remote=\n/);
    // Reopening preserves both the database location and user-edited provider configuration.
    writeFileSync(join(config.home, 'agents.json'), '[{"id":"kept"}]');
    writeFileSync(f.log, '');
    const second = f.launch(['--no-build']);
    assert.equal(second.status, 0, second.stderr);
    assert.equal(f.config().home, config.home);
    assert.equal(readFileSync(join(config.home, 'agents.json'), 'utf8'), '[{"id":"kept"}]');
    assert.doesNotMatch(readFileSync(f.log, 'utf8'), /dotnet|cargo|agents/);
  } finally { f.close(); }
});

test('worktree selection changes source and database even when folder names match', () => {
  const f = fixture();
  try {
    assert.equal(f.launch().status, 0);
    const firstHome = f.config().home;
    const other = join(f.directory, 'elsewhere', 'selected worktree');
    for (const folder of ['scripts', 'desktop', 'dotnet/Assembly.Desktop', 'dotnet/scripts']) mkdirSync(join(other, folder), { recursive: true });
    for (const path of ['desktop/Cargo.toml', 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj', 'dotnet/scripts/agents-file.sh']) copyFileSync(join(f.root, path), join(other, path));
    const selected = f.launch(['--worktree', other]);
    assert.equal(selected.status, 0, selected.stderr);
    const config = f.config(other);
    assert.notEqual(config.home, firstHome);
    assert.match(selected.stdout, /elsewhere/);
    assert.match(readFileSync(f.log, 'utf8'), new RegExp(other.replace(/[.*+?^$\{\}()|[\]\\]/g, '\\$&')));
  } finally { f.close(); }
});

test('failed publish never launches an app or publishes a partial lane configuration', () => {
  const f = fixture();
  try {
    const failed = f.launch([], { DOTNET_EXIT: '7' });
    assert.equal(failed.status, 7, failed.stderr);
    assert.doesNotMatch(readFileSync(f.log, 'utf8'), /cargo|native/);
    assert.equal(existsSync(join(f.root, 'desktop/target/dev-backend/current.json')), false);
  } finally { f.close(); }
});

test('failed rebuild keeps the last complete build available for no-build', () => {
  const f = fixture();
  try {
    assert.equal(f.launch().status, 0);
    const receipt = join(f.root, 'desktop/target/dev-backend/current.json');
    const previous = readFileSync(receipt, 'utf8');
    const failure = f.launch([], { CARGO_EXIT: '9' });
    assert.equal(failure.status, 9, failure.stderr);
    assert.equal(readFileSync(receipt, 'utf8'), previous);
    writeFileSync(f.log, '');
    const reopen = f.launch(['--no-build']);
    assert.equal(reopen.status, 0, reopen.stderr);
    assert.match(readFileSync(f.log, 'utf8'), /native/);
    assert.doesNotMatch(readFileSync(f.log, 'utf8'), /dotnet|cargo|agents/);
  } finally { f.close(); }
});

test('a second launch cannot replace config while this worktree app is running', { timeout: 10000 }, async () => {
  const f = fixture();
  let child: ReturnType<typeof spawn> | undefined;
  try {
    child = spawn(process.execPath, [join(f.root, 'scripts/app-dev.ts')], {
      cwd: f.directory, env: { ...f.env, WAIT_NATIVE: '1' }, stdio: ['pipe', 'pipe', 'pipe'],
    });
    const finished = new Promise<number | null>((done, reject) => {
      child!.once('error', reject);
      child!.once('close', done);
    });
    await new Promise<void>((done, reject) => {
      let output = '';
      child!.stdout!.on('data', bytes => { output += bytes; if (output.includes('READY')) done(); });
      child!.once('error', reject);
      child!.once('close', () => reject(new Error('Fixture exited before its ready event')));
    });
    const original = f.config();
    const second = f.launch(['--no-build']);
    assert.notEqual(second.status, 0);
    assert.match(second.stderr, /already launching\/running/);
    assert.deepEqual(f.config(), original);
    child.stdin!.end('done\n');
    assert.equal(await finished, 0);
    assert.equal(existsSync(join(original.home, 'launcher.lock')), false);
  } finally {
    if (child && child.exitCode === null) child.kill('SIGTERM');
    f.close();
  }
});

test('no-build refuses a missing build and invalid worktree without starting tools', () => {
  const f = fixture();
  try {
    const missing = f.launch(['--no-build']);
    assert.notEqual(missing.status, 0);
    assert.match(missing.stderr, /no complete development build/);
    const invalid = f.launch(['--worktree', f.directory]);
    assert.notEqual(invalid.status, 0);
    assert.match(invalid.stderr, /Not an Assembly worktree/);
    assert.equal(existsSync(f.log), false);
  } finally { f.close(); }
});
