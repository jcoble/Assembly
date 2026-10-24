import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';

import {
  REMOTE_BACKEND_INSTALL_SCRIPT,
  writeRemoteBackendPackage
} from './remoteBackendPackage.ts';

const execFileAsync = promisify(execFile);

test('builds a deterministic remote backend package contract', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'assembly-remote-package-test-'));
  try {
    const input = path.join(root, 'input');
    const output = path.join(root, 'output');
    const extracted = path.join(root, 'extracted');
    await mkdir(input);
    await mkdir(extracted);
    await writeFile(path.join(input, 'server'), 'server-bytes');
    const archive = await writeRemoteBackendPackage({
      binaryPath: path.join(input, 'server'),
      outputDirectory: output,
      version: '1.2.3',
      commit: '0123456789abcdef0123456789abcdef01234567'
    });
    const archiveBytes = await readFile(archive);
    const record = JSON.parse(await readFile(`${archive}.download.json`, 'utf8')) as {
      schemaVersion: number; version: string; protocolVersion: number; target: string;
      archiveFile: string; bytes: number; sha256: string;
    };
    const protocolSource = await readFile(new URL('../src-tauri/src/agent_conversation/remote/mod.rs', import.meta.url), 'utf8');
    assert.match(protocolSource, new RegExp(`const PROTOCOL_VERSION: u16 = ${record.protocolVersion};`));
    assert.deepEqual(record, {
      schemaVersion: 1, version: '1.2.3', protocolVersion: record.protocolVersion,
      target: 'x86_64-unknown-linux-gnu', archiveFile: path.basename(archive),
      bytes: archiveBytes.length, sha256: createHash('sha256').update(archiveBytes).digest('hex')
    });
    await execFileAsync('tar', ['-xzf', archive, '-C', extracted]);
    const manifest = JSON.parse(await readFile(path.join(extracted, 'manifest.json'), 'utf8')) as {
      schemaVersion: number;
      packageType: string;
      target: string;
      commit: string;
      files: Array<{ path: string; sha256: string }>;
    };
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.packageType, 'assembly-remote-backend');
    assert.equal(manifest.target, 'x86_64-unknown-linux-gnu');
    assert.equal(manifest.commit, '0123456789abcdef0123456789abcdef01234567');
    assert.equal(manifest.files.length, 2);
    for (const file of manifest.files) {
      const digest = createHash('sha256').update(await readFile(path.join(extracted, file.path))).digest('hex');
      assert.equal(digest, file.sha256);
    }
    assert.equal(await readFile(path.join(extracted, 'install.sh'), 'utf8'), REMOTE_BACKEND_INSTALL_SCRIPT);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /sha256sum -c SHA256SUMS/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /ASSEMBLY_SERVER_BIND=127\.0\.0\.1:7777/);
    // The phone listener's settings are optional and survive reinstalls.
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /\nEnvironmentFile=-\$HOME\/\.config\/assembly\/phone\.env\n/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /systemctl --user restart assembly-remote\.service/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /systemctl --user stop assembly-remote\.service/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /backend-manifest\.json/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /\.assembly-remote-server\.new/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /\.local\/bin\/assembly-adapters/);
    assert.doesNotMatch(REMOTE_BACKEND_INSTALL_SCRIPT, /\.local\/bin\/adapters/);
    assert.doesNotMatch(REMOTE_BACKEND_INSTALL_SCRIPT, /payload\/adapters\//);
    assert.doesNotMatch(REMOTE_BACKEND_INSTALL_SCRIPT, /command -v claude-agent-acp/);
    assert.doesNotMatch(REMOTE_BACKEND_INSTALL_SCRIPT, /command in node codex/);
    assert.match(REMOTE_BACKEND_INSTALL_SCRIPT, /\$4 != "127\.0\.0\.1:7777"/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('rejects invalid release identity before writing', async () => {
  await assert.rejects(
    writeRemoteBackendPackage({
      binaryPath: '/missing',
      outputDirectory: '/missing',
      version: '../bad',
      commit: 'not-a-commit'
    }),
    /Invalid backend version/
  );
});

test('remote download verifier rejects changed, truncated, and unexpected archives', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'assembly-remote-download-test-'));
  try {
    const input = path.join(root, 'input');
    const output = path.join(root, 'output');
    await mkdir(input);
    await writeFile(path.join(input, 'server'), 'server-bytes');
    const archive = await writeRemoteBackendPackage({
      binaryPath: path.join(input, 'server'),
      outputDirectory: output, version: '1.2.3', commit: '0123456789abcdef0123456789abcdef01234567'
    });
    const rust = await readFile(new URL('../src-tauri/src/agent_conversation/remote/install.rs', import.meta.url), 'utf8');
    const script = rust.match(/const REMOTE_DOWNLOAD: &str = r#"([\s\S]*?)"#;/)?.[1];
    assert.ok(script, 'remote verifier script exists');
    const run = async (source: string, bytes: number, digest: string) => {
      const destination = path.join(root, `stage-${crypto.randomUUID()}`);
      await mkdir(destination);
      const wrapper = `import os, urllib.request\nclass Source:\n def __init__(self, path):\n  self.file=open(path,'rb'); self.headers={'Content-Length':str(os.path.getsize(path))}\n def __enter__(self): return self\n def __exit__(self,*args): self.file.close()\n def geturl(self): return 'https://github.com/jcoble/mac-command-bar/releases/download/test/package.tar.gz'\n def read(self,n): return self.file.read(n)\nurllib.request.urlopen=lambda request,timeout:Source(${JSON.stringify(source)})\n`;
      try {
        const { stdout } = await execFileAsync('python3', ['-c', wrapper + script, 'https://github.com/jcoble/mac-command-bar/releases/download/test/package.tar.gz', path.join(destination, 'package.tar.gz'), String(bytes), digest, '1.2.3']);
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

test('the install script writes the token file readable only by its owner from the moment it exists', async () => {
  const home = await mkdtemp(path.join(tmpdir(), 'assembly-remote-token-test-'));
  try {
    const start = REMOTE_BACKEND_INSTALL_SCRIPT.indexOf('token=$(');
    const endMarker = 'mv "$HOME/.config/assembly/.server.env.new" "$HOME/.config/assembly/server.env"\n';
    const end = REMOTE_BACKEND_INSTALL_SCRIPT.indexOf(endMarker);
    assert.ok(start > 0 && end > start, 'token block exists');
    const tokenBlock = REMOTE_BACKEND_INSTALL_SCRIPT.slice(start, end + endMarker.length);
    const token = 'a'.repeat(64);
    await mkdir(path.join(home, '.config', 'assembly'), { recursive: true });
    await writeFile(path.join(home, '.config', 'assembly', 'server.env'), `ASSEMBLY_SERVER_TOKEN=${token}\n`);
    // A stand-in `cat` reports the file's permissions while the token is being written into it.
    const bin = path.join(home, 'bin');
    await mkdir(bin);
    await writeFile(path.join(bin, 'cat'), '#!/bin/sh\nexec 3>&1\nls -lL /dev/fd/3 | cut -c1-10 >"$HOME/mode-while-writing"\nexec /bin/cat "$@"\n', { mode: 0o755 });
    await execFileAsync('sh', ['-c', `set -eu\numask 022\n${tokenBlock}`], {
      env: { HOME: home, PATH: `${bin}:/usr/bin:/bin` }
    });
    assert.equal((await readFile(path.join(home, 'mode-while-writing'), 'utf8')).trim(), '-rw-------');
    const saved = path.join(home, '.config', 'assembly', 'server.env');
    assert.equal((await stat(saved)).mode & 0o777, 0o600);
    assert.match(await readFile(saved, 'utf8'), new RegExp(`ASSEMBLY_SERVER_TOKEN=${token}\n`));
  } finally {
    await rm(home, { recursive: true, force: true });
  }
});

// Runs the install script's restart-and-wait tail against stand-in systemctl, ss and sleep.
// Each `sleep` call is one second, so `seconds` is how long the script waited.
async function runStartupWait(scenario: { readyAt?: number; address?: string; exitAt?: number; restartAt?: number }) {
  const home = await mkdtemp(path.join(tmpdir(), 'assembly-remote-wait-test-'));
  try {
    const bin = path.join(home, 'bin');
    await mkdir(bin);
    await mkdir(path.join(home, '.local', 'share', 'assembly'), { recursive: true });
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
    const start = REMOTE_BACKEND_INSTALL_SCRIPT.indexOf('systemctl --user restart assembly-remote.service');
    assert.ok(start > 0, 'restart step exists');
    const env: Record<string, string> = { HOME: home, PATH: `${bin}:/usr/bin:/bin`, LISTEN_ADDRESS: scenario.address ?? '127.0.0.1:7777' };
    if (scenario.readyAt !== undefined) env.READY_AT = String(scenario.readyAt);
    if (scenario.exitAt !== undefined) env.EXIT_AT = String(scenario.exitAt);
    if (scenario.restartAt !== undefined) env.RESTART_AT = String(scenario.restartAt);
    let code = 0;
    let stderr = '';
    try {
      await execFileAsync('sh', ['-c', `set -eu\n${REMOTE_BACKEND_INSTALL_SCRIPT.slice(start)}`], { cwd: home, env });
    } catch (error) {
      const failure = error as { code: number; stderr: string };
      code = failure.code;
      stderr = failure.stderr;
    }
    const manifest = await stat(path.join(home, '.local', 'share', 'assembly', 'backend-manifest.json')).then(() => true, () => false);
    return { code, stderr, seconds: Number(await readFile(path.join(home, 'seconds'), 'utf8')), manifest };
  } finally {
    await rm(home, { recursive: true, force: true });
  }
}

test('install waits past the old 15 seconds for a backend that is still starting', async () => {
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
  const result = await runStartupWait({ readyAt: 1, address: '0.0.0.0:7777' });
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
