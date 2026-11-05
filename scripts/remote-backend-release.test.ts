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
    const start = REMOTE_BACKEND_INSTALL_SCRIPT.indexOf('systemctl --user restart assembly-host.service');
    assert.ok(start > 0, 'restart step exists');
    const env: Record<string, string> = { HOME: home, PATH: `${bin}:/usr/bin:/bin`, LISTEN_ADDRESS: scenario.address ?? '127.0.0.1:9797' };
    if (scenario.readyAt !== undefined) env.READY_AT = String(scenario.readyAt);
    if (scenario.exitAt !== undefined) env.EXIT_AT = String(scenario.exitAt);
    if (scenario.restartAt !== undefined) env.RESTART_AT = String(scenario.restartAt);
    let code = 0;
    let stderr = '';
    try {
      await execFileAsync('sh', ['-c', `set -eu\nmode=install\nport=9797\nroot=$HOME/assembly-host\n${REMOTE_BACKEND_INSTALL_SCRIPT.slice(start)}`], { cwd: home, env });
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
