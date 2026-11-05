import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

import { REMOTE_BACKEND_INSTALL_SCRIPT, writeRemoteBackendPackage } from './remote-backend-release.ts';

const execFileAsync = promisify(execFile);
const repository = fileURLToPath(new URL('../', import.meta.url));
const commit = '0123456789abcdef0123456789abcdef01234567';

async function build(root: string): Promise<string> {
  const input = path.join(root, 'input');
  await mkdir(input, { recursive: true });
  await writeFile(path.join(input, 'server'), 'server-bytes');
  return writeRemoteBackendPackage({
    binaryPath: path.join(input, 'server'), outputDirectory: path.join(root, 'output'),
    sourceDirectory: repository, version: '1.2.3', commit
  });
}

test('builds the old package contract with the new backend and the adapter installer', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'assembly-remote-package-test-'));
  try {
    const archive = await build(root);
    assert.equal(path.basename(archive), 'assembly-remote-backend-1.2.3-x86_64-unknown-linux-gnu.tar.gz');
    const archiveBytes = await readFile(archive);
    const record = JSON.parse(await readFile(`${archive}.download.json`, 'utf8'));
    const protocol = await readFile(path.join(repository, 'dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs'), 'utf8');
    assert.match(protocol, new RegExp(`const int Protocol = ${record.protocolVersion};`));
    assert.deepEqual(record, {
      schemaVersion: 1, version: '1.2.3', protocolVersion: record.protocolVersion,
      target: 'x86_64-unknown-linux-gnu', archiveFile: path.basename(archive),
      bytes: archiveBytes.length, sha256: createHash('sha256').update(archiveBytes).digest('hex')
    });
    const extracted = path.join(root, 'extracted');
    await mkdir(extracted);
    await execFileAsync('tar', ['-xzf', archive, '-C', extracted]);
    const manifest = JSON.parse(await readFile(path.join(extracted, 'manifest.json'), 'utf8'));
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.packageType, 'assembly-remote-backend');
    assert.equal(manifest.target, 'x86_64-unknown-linux-gnu');
    assert.equal(manifest.commit, commit);
    assert.deepEqual(manifest.files.map((file: { path: string }) => file.path).sort(), [
      'install.sh', 'payload/assembly-remote-server', 'payload/dotnet/scripts/agents-file.sh', 'payload/scripts/package-adapters.ts'
    ]);
    for (const file of manifest.files) {
      const digest = createHash('sha256').update(await readFile(path.join(extracted, file.path))).digest('hex');
      assert.equal(digest, file.sha256);
    }
    assert.equal(await readFile(path.join(extracted, 'install.sh'), 'utf8'), REMOTE_BACKEND_INSTALL_SCRIPT);
    await execFileAsync('sha256sum', ['-c', 'SHA256SUMS'], { cwd: extracted });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('rejects invalid release identity before writing', async () => {
  await assert.rejects(writeRemoteBackendPackage({
    binaryPath: '/missing', outputDirectory: '/missing', sourceDirectory: '/missing', version: '../bad', commit: 'not-a-commit'
  }), /Invalid backend version/);
  await assert.rejects(writeRemoteBackendPackage({
    binaryPath: '/missing', outputDirectory: '/missing', sourceDirectory: '/missing', version: '1.2.3', commit: 'not-a-commit'
  }), /Invalid Git commit/);
});

test('remote download verifier rejects changed, truncated, and unexpected archives', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'assembly-remote-download-test-'));
  try {
    const archive = await build(root);
    const csharp = await readFile(path.join(repository, 'dotnet/Assembly.Infrastructure/Remote/Install/RemoteReleaseScript.cs'), 'utf8');
    const script = csharp.match(/Download = """\n([\s\S]*?)\n""";/)?.[1];
    assert.ok(script, 'remote verifier script exists');
    const url = 'https://github.com/jcoble/Assembly/releases/download/test/package.tar.gz';
    const run = async (source: string, bytes: number, digest: string) => {
      const destination = path.join(root, `stage-${randomUUID()}`);
      await mkdir(destination);
      const wrapper = `import os, urllib.request\nclass Source:\n def __init__(self, path):\n  self.file=open(path,'rb'); self.headers={'Content-Length':str(os.path.getsize(path))}\n def __enter__(self): return self\n def __exit__(self,*args): self.file.close()\n def geturl(self): return '${url}'\n def read(self,n): return self.file.read(n)\nurllib.request.urlopen=lambda request,timeout:Source(${JSON.stringify(source)})\n`;
      try {
        const { stdout } = await execFileAsync('python3', ['-c', wrapper + script, url, path.join(destination, 'package.tar.gz'), String(bytes), digest, '1.2.3']);
        return stdout;
      } finally {
        await rm(destination, { recursive: true, force: true });
      }
    };
    const archiveBytes = await readFile(archive);
    const digest = createHash('sha256').update(archiveBytes).digest('hex');
    assert.match(await run(archive, archiveBytes.length, digest), /V\t1\.2\.3\t/);
    await assert.rejects(run(archive, archiveBytes.length, '0'.repeat(64)), /signed digest/);
    await assert.rejects(run(archive, archiveBytes.length + 1, digest), /signed record/);
    const unpacked = path.join(root, 'unpacked');
    await mkdir(unpacked);
    await execFileAsync('tar', ['-xzf', archive, '-C', unpacked]);
    await writeFile(path.join(unpacked, 'unexpected'), 'extra');
    const unexpected = path.join(root, 'unexpected.tar.gz');
    await execFileAsync('tar', ['-czf', unexpected, '-C', unpacked, '.']);
    const unexpectedBytes = await readFile(unexpected);
    await assert.rejects(run(unexpected, unexpectedBytes.length, createHash('sha256').update(unexpectedBytes).digest('hex')), /Unexpected backend archive entry/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

// Runs the install script's restart-and-wait tail against stand-in systemctl, ss and sleep.
// Each `sleep` call is one second, so `seconds` is how long the script waited.
async function runStartupWait(scenario: { readyAt?: number; address?: string; exitAt?: number; restartAt?: number }) {
  const home = await mkdtemp(path.join(tmpdir(), 'assembly-remote-wait-test-'));
  try {
    const bin = path.join(home, 'bin');
    await mkdir(bin);
    await mkdir(path.join(home, 'assembly-host', 'serve'), { recursive: true });
    await writeFile(path.join(home, 'manifest.json'), '{}\n');
    await writeFile(path.join(home, 'seconds'), '0');
    await writeFile(path.join(bin, 'sleep'), '#!/bin/sh\necho $(( $(cat "$HOME/seconds") + 1 )) >"$HOME/seconds"\n', { mode: 0o755 });
    await writeFile(path.join(bin, 'systemctl'), `#!/bin/sh
t=$(cat "$HOME/seconds")
case "$*" in
  *is-active*)
    if [ -n "\${EXIT_AT:-}" ] && [ "$t" -ge "$EXIT_AT" ]; then echo failed; exit 3; fi
    if [ -n "\${RESTART_AT:-}" ] && [ "$t" -eq "$RESTART_AT" ]; then echo activating; exit 3; fi
    case "$*" in *--quiet*) ;; *) echo active ;; esac
    exit 0 ;;
esac
exit 0
`, { mode: 0o755 });
    await writeFile(path.join(bin, 'ss'), `#!/bin/sh
t=$(cat "$HOME/seconds")
if [ -n "\${READY_AT:-}" ] && [ "$t" -ge "$READY_AT" ] && { [ -z "\${RESTART_AT:-}" ] || [ "$t" -ne "$RESTART_AT" ]; }; then
  printf 'LISTEN 0 128 %s 0.0.0.0:*\\n' "$LISTEN_ADDRESS"
fi
`, { mode: 0o755 });
    const script = REMOTE_BACKEND_INSTALL_SCRIPT;
    const listening = script.slice(script.indexOf('listening() {'), script.indexOf('# Any way out from here'));
    const start = script.lastIndexOf('systemctl --user restart assembly-host.service');
    assert.ok(start > 0, 'restart step exists');
    const env: Record<string, string> = { HOME: home, PATH: `${bin}:/usr/bin:/bin`, LISTEN_ADDRESS: scenario.address ?? '127.0.0.1:9797' };
    if (scenario.readyAt !== undefined) env.READY_AT = String(scenario.readyAt);
    if (scenario.exitAt !== undefined) env.EXIT_AT = String(scenario.exitAt);
    if (scenario.restartAt !== undefined) env.RESTART_AT = String(scenario.restartAt);
    let code = 0;
    let stderr = '';
    try {
      await execFileAsync('sh', ['-c', `set -eu\nmode=install\nport=9797\nroot=$HOME/assembly-host\nbefore=$root/before-update\n${listening}${script.slice(start)}`], { cwd: home, env });
    } catch (error) {
      const failure = error as { code: number; stderr: string };
      code = failure.code;
      stderr = failure.stderr;
    }
    const manifest = await stat(path.join(home, 'assembly-host', 'serve', 'backend-manifest.json')).then(() => true, () => false);
    return { code, stderr, seconds: Number(await readFile(path.join(home, 'seconds'), 'utf8')), manifest };
  } finally {
    await rm(home, { recursive: true, force: true });
  }
}

test('install waits for a backend that is still starting', async () => {
  const result = await runStartupWait({ readyAt: 22 });
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.manifest, true);
  assert.equal(result.seconds, 22);
});

test('install fails at once when the backend exits while starting', async () => {
  const result = await runStartupWait({ exitAt: 3 });
  assert.equal(result.code, 1);
  assert.equal(result.manifest, false);
  assert.equal(result.seconds, 3);
  assert.match(result.stderr, /stopped after 3 seconds/);
});

test('install gives up after the long wait when the backend never listens, saying how long it waited', async () => {
  const result = await runStartupWait({});
  assert.equal(result.code, 1);
  assert.equal(result.manifest, false);
  assert.equal(result.seconds, 120);
  assert.match(result.stderr, /within 120 seconds/);
});

test('install still refuses a backend listening on a non-loopback address', async () => {
  const result = await runStartupWait({ readyAt: 1, address: '0.0.0.0:9797' });
  assert.equal(result.code, 1);
  assert.equal(result.manifest, false);
  assert.match(result.stderr, /within 120 seconds/);
});

test('install keeps waiting through one restart while the backend starts', async () => {
  const result = await runStartupWait({ readyAt: 20, restartAt: 18 });
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.manifest, true);
  assert.equal(result.seconds, 20);
});

// The whole script against stand-ins that behave like the real thing: the service runs whatever binary is in place, and only
// a binary whose contents say good ever listens, on the port its unit names.
async function installed(home: string) {
  const root = path.join(home, 'assembly-host');
  const read = (file: string) => readFile(file, 'utf8').catch(() => null);
  return {
    binary: await read(path.join(root, 'bin/assembly-remote-server')),
    unit: await read(path.join(home, '.config/systemd/user/assembly-host.service')),
    manifest: await read(path.join(root, 'serve/backend-manifest.json')),
    database: await read(path.join(root, 'serve/assembly.db')),
    wal: await read(path.join(root, 'serve/assembly.db-wal')),
    agents: await read(path.join(root, 'serve/agents.json')),
    state: await read(path.join(home, 'state')),
    before: await stat(path.join(root, 'before-update')).then(() => true, () => false)
  };
}

async function withMachine(act: (run: (binary: string, version: string, args: string[], env?: Record<string, string>) => Promise<{ code: number; stderr: string }>, home: string) => Promise<void>) {
  const home = await mkdtemp(path.join(tmpdir(), 'assembly-remote-undo-test-'));
  try {
    const bin = path.join(home, '.local/bin');
    await mkdir(bin, { recursive: true });
    await writeFile(path.join(bin, 'cp'), `#!/bin/sh
for last; do :; done
if [ -n "\${CP_FAILS_ON:-}" ]; then case "$*" in *"$CP_FAILS_ON"*) printf partial >"$last"; exit 1 ;; esac; fi
exec /bin/cp "$@"
`, { mode: 0o755 });
    for (const tool of ['curl', 'tar', 'python3', 'node', 'pidwait', 'sleep']) await writeFile(path.join(bin, tool), '#!/bin/sh\nexit 0\n', { mode: 0o755 });
    await writeFile(path.join(home, 'state'), 'inactive\n');
    await mkdir(path.join(home, 'cgroup'));
    await writeFile(path.join(bin, 'systemctl'), `#!/bin/sh
server=$HOME/assembly-host/bin/assembly-remote-server
case "$*" in
  *is-active*) cat "$HOME/state"; [ "$(cat "$HOME/state")" = active ] ;;
  *restart*)
    [ ! -e "$HOME/kill-on-restart" ] || { rm "$HOME/kill-on-restart"; kill -KILL $PPID; }
    [ ! -e "$HOME/term-on-restart" ] || kill -TERM $PPID
    if grep -q migrates "$server"; then echo migrated >"$HOME/assembly-host/serve/assembly.db"; echo migrated-wal >"$HOME/assembly-host/serve/assembly.db-wal"; fi
    if grep -q exits "$server"; then echo failed >"$HOME/state"; else echo active >"$HOME/state"; fi ;;
  *stop*)
    echo inactive >"$HOME/state"
    [ ! -e "$HOME/term-on-stop" ] || { rm "$HOME/term-on-stop"; kill -TERM $PPID; } ;;
  *ControlGroup*) echo "/../../..$HOME/cgroup" ;;
  *) exit 0 ;;
esac
`, { mode: 0o755 });
    await writeFile(path.join(bin, 'ss'), `#!/bin/sh
[ "$(cat "$HOME/state")" = active ] || exit 0
grep -q good "$HOME/assembly-host/bin/assembly-remote-server" || exit 0
port=$(sed -n 's/^ExecStart=.* --port \\([0-9]*\\) .*/\\1/p' "$HOME/.config/systemd/user/assembly-host.service")
case "$*" in *":$port"*) printf 'LISTEN 0 128 127.0.0.1:%s 0.0.0.0:*\\n' "$port" ;; esac
`, { mode: 0o755 });
    let run = 0;
    await act(async (binary, version, args, env = {}) => {
      const stage = path.join(home, `stage-${run++}`);
      await mkdir(path.join(stage, 'payload/dotnet/scripts'), { recursive: true });
      await writeFile(path.join(stage, 'payload/assembly-remote-server'), binary);
      await writeFile(path.join(stage, 'payload/dotnet/scripts/agents-file.sh'), 'printf "%s" "$ASSEMBLY_ADAPTER_TAG" >"$2"\n[ -z "${SESSION_STARTS:-}" ] || echo "$SESSION_STARTS" >"$HOME/cgroup/cgroup.procs"\n[ -z "${SETTING_SAVED:-}" ] || echo saved-meanwhile >"$HOME/assembly-host/serve/agents.json"\n');
      await writeFile(path.join(stage, 'install.sh'), REMOTE_BACKEND_INSTALL_SCRIPT);
      await writeFile(path.join(stage, 'manifest.json'), JSON.stringify({ version }, null, 2));
      const sum = createHash('sha256').update(binary).digest('hex');
      await writeFile(path.join(stage, 'SHA256SUMS'), `${sum}  payload/assembly-remote-server\n`);
      try {
        const { stderr } = await execFileAsync('sh', ['install.sh', ...args], { cwd: stage, env: { HOME: home, PATH: '/usr/bin:/bin', ...env } });
        return { code: 0, stderr };
      } catch (error) {
        const failure = error as { code: number | null; stderr: string };
        return { code: failure.code ?? -1, stderr: failure.stderr };
      }
    }, home);
  } finally {
    await rm(home, { recursive: true, force: true });
  }
}

test('a new backend that exits at once puts the old one back, running, with its unit and data', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    const root = path.join(home, 'assembly-host');
    await writeFile(path.join(root, 'serve/assembly.db'), 'written by the old backend');
    await writeFile(path.join(root, 'serve/assembly.db-wal'), 'its wal');
    const old = await installed(home);

    const failed = await run('exits migrates', '2.0.0', ['--keep-adapters', '9802', '', '']);

    assert.equal(failed.code, 1);
    assert.match(failed.stderr, /previous backend is running again on port 9801/);
    assert.deepEqual(await installed(home), old);
    assert.equal(old.state, 'active\n');
    assert.match(old.unit!, /--port 9801 /);
    assert.equal(old.before, false);
  });
});

test('a new backend that never listens is undone as well', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    const old = await installed(home);
    const failed = await run('silent', '2.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(failed.code, 1);
    assert.match(failed.stderr, /within 120 seconds/);
    assert.deepEqual(await installed(home), old);
  });
});

test('adapters that came with a failed update go back too', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.0.0', ['9801', 'old-tag', 'x'])).code, 0);
    assert.equal((await installed(home)).agents, 'old-tag');
    assert.equal((await run('exits', '2.0.0', ['9801', 'new-tag', 'x'])).code, 1);
    assert.equal((await installed(home)).agents, 'old-tag');
  });
});

test('a first install that fails leaves no service behind and can be pressed again', async () => {
  await withMachine(async (run, home) => {
    const failed = await run('exits', '1.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(failed.code, 1);
    assert.match(failed.stderr, /no earlier one to go back to/);
    const after = await installed(home);
    assert.equal(after.unit, null);
    assert.equal(after.binary, null);
    assert.equal(after.state, 'inactive\n');
    assert.equal((await run('good one', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
  });
});

test('an older backend is refused with nothing touched, and the same version can go on again', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.10.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    const current = await installed(home);
    const refused = await run('good older', '1.9.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(refused.code, 1);
    assert.match(refused.stderr, /backend is 1\.10\.0, newer than 1\.9\.0, so nothing was changed/);
    assert.deepEqual(await installed(home), current);
    assert.equal((await run('good same', '1.10.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    assert.equal((await run('good newer', '1.11.0', ['--keep-adapters', '9801', '', ''])).code, 0);
  });
});

test('a tools-only update waits on the port the service has, not the one saved since', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    const updated = await run('not this binary', '2.0.0', ['--adapters', '9999', 'tag', 'x']);
    assert.equal(updated.code, 0, updated.stderr);
    const after = await installed(home);
    assert.match(after.unit!, /--port 9801 /);
    assert.equal(after.binary, 'good one');
    assert.equal(after.agents, 'tag');
  });
});

test('the token file is the install\'s own and is no longer something to pass', async () => {
  await withMachine(async (run, home) => {
    assert.equal((await run('good one', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    const after = await installed(home);
    assert.match(after.unit!, new RegExp(`--token-file ${home}/\\.config/assembly-host/env\\n`));
  });
});

async function installedWithData(run: (binary: string, version: string, args: string[], env?: Record<string, string>) => Promise<{ code: number; stderr: string }>, home: string) {
  assert.equal((await run('good one', '1.0.0', ['9801', 'old-tag', 'x'])).code, 0);
  const root = path.join(home, 'assembly-host');
  await writeFile(path.join(root, 'serve/assembly.db'), 'the database');
  await writeFile(path.join(root, 'serve/assembly.db-wal'), 'its log');
  return installed(home);
}

test('a signal during the stop leaves a working install exactly as it was, and running', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    await writeFile(path.join(home, 'term-on-stop'), '');
    const stopped = await run('good two', '2.0.0', ['--keep-adapters', '9802', '', '']);
    assert.equal(stopped.code, 1);
    assert.match(stopped.stderr, /previous backend is running again on port 9801/);
    assert.doesNotMatch(stopped.stderr, /no earlier one/);
    assert.deepEqual(await installed(home), old);
  });
});

for (const file of ['assembly.db', 'assembly-remote-server', 'assembly-host.service']) {
  test(`a copy of ${file} that fails partway is never put back over the real one`, async () => {
    await withMachine(async (run, home) => {
      const old = await installedWithData(run, home);
      const failed = await run('exits', '2.0.0', ['--keep-adapters', '9802', '', ''], { CP_FAILS_ON: file });
      assert.equal(failed.code, 1);
      assert.match(failed.stderr, /previous backend is running again on port 9801/);
      assert.deepEqual(await installed(home), old);
    });
  });
}

test('a signal during the undo does not stop it', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    await writeFile(path.join(home, 'term-on-restart'), '');
    const failed = await run('exits migrates', '2.0.0', ['--keep-adapters', '9802', '', '']);
    assert.equal(failed.code, 1);
    await rm(path.join(home, 'term-on-restart'));
    assert.deepEqual(await installed(home), old);
  });
});

test('an install killed with things replaced is undone by the next run before anything else, and loses nothing', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    await writeFile(path.join(home, 'kill-on-restart'), '');
    const killed = await run('exits migrates', '2.0.0', ['--keep-adapters', '9802', '', '']);
    assert.notEqual(killed.code, 0);
    assert.equal((await installed(home)).binary, 'exits migrates');
    assert.equal((await installed(home)).before, true);

    // Even an older package than the one left behind gets a fair reading: it's compared with what was really installed.
    const next = await run('good two', '1.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(next.code, 0, next.stderr);
    assert.match(next.stderr, /stopped partway/);
    const after = await installed(home);
    assert.equal(after.binary, 'good two');
    assert.equal(after.database, old.database);
    assert.equal(after.wal, old.wal);
    assert.equal(after.before, false);
  });
});

test('a session that starts during the adapters refuses the update and changes nothing', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    assert.equal(old.state, 'active\n');
    const refused = await run('good two', '2.0.0', ['9801', 'new-tag', 'x'], { SESSION_STARTS: String(process.pid) });
    assert.equal(refused.code, 75);
    assert.deepEqual(await installed(home), old);
    assert.equal(old.agents, 'old-tag');
  });
});

test('a first install killed partway is removed by the next run, not kept as if it were the old one', async () => {
  await withMachine(async (run, home) => {
    await writeFile(path.join(home, 'kill-on-restart'), '');
    assert.notEqual((await run('exits', '1.0.0', ['--keep-adapters', '9801', '', ''])).code, 0);
    assert.equal((await installed(home)).binary, 'exits');

    const next = await run('exits', '1.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(next.code, 1);
    assert.match(next.stderr, /stopped partway/);
    assert.match(next.stderr, /no earlier one to go back to/);
    const after = await installed(home);
    assert.equal(after.binary, null);
    assert.equal(after.unit, null);
  });
});

test('a session in what a killed install left is not ended: the next run refuses and keeps what it needs', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    await writeFile(path.join(home, 'kill-on-restart'), '');
    assert.notEqual((await run('migrates', '2.0.0', ['--keep-adapters', '9802', '', ''])).code, 0);
    await writeFile(path.join(home, 'state'), 'active\n');
    await writeFile(path.join(home, 'cgroup/cgroup.procs'), `${process.pid}\n`);

    const refused = await run('good two', '3.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(refused.code, 75);
    assert.equal((await installed(home)).binary, 'migrates');
    assert.equal((await installed(home)).before, true);

    await writeFile(path.join(home, 'cgroup/cgroup.procs'), '');
    const next = await run('good two', '3.0.0', ['--keep-adapters', '9801', '', '']);
    assert.equal(next.code, 0, next.stderr);
    assert.match(next.stderr, /stopped partway/);
    const after = await installed(home);
    assert.equal(after.binary, 'good two');
    assert.equal(after.database, old.database);
  });
});

test('a setting saved during the adapters is never overwritten: the update refuses and can run again', async () => {
  await withMachine(async (run, home) => {
    const old = await installedWithData(run, home);
    const refused = await run('good two', '2.0.0', ['9801', 'new-tag', 'x'], { SETTING_SAVED: '1' });
    assert.equal(refused.code, 75);
    assert.match(refused.stderr, /agents file changed/);
    const after = await installed(home);
    assert.equal(after.agents, 'saved-meanwhile\n');
    assert.equal(after.binary, old.binary);
    assert.equal(after.state, old.state);
    assert.equal(after.before, false);

    const again = await run('good two', '2.0.0', ['9801', 'new-tag', 'x']);
    assert.equal(again.code, 0, again.stderr);
    assert.equal((await installed(home)).agents, 'new-tag');
  });
});
