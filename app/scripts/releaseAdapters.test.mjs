import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile, rm } from 'node:fs/promises';
import { promisify } from 'node:util';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const adapterDir = new URL('../src-tauri/adapters/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('manifest.json', adapterDir), 'utf8'));
assert.equal(manifest.schemaVersion, 1);
const execFileAsync = promisify(execFile);

async function sha256(path) {
  return createHash('sha256').update(await readFile(path)).digest('hex');
}

async function initialize(adapter) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15_000);
  const child = spawn(fileURLToPath(new URL(adapter.executable, adapterDir)), [], {
    signal: controller.signal,
    stdio: ['pipe', 'pipe', 'pipe']
  });
  child.on('error', () => {});
  child.stdin.write(`${JSON.stringify({
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: {
      protocolVersion: 1,
      clientCapabilities: {},
      clientInfo: { name: 'assembly-release-test', version: '0.1.0' }
    }
  })}\n`);

  let response = '';
  for await (const line of createInterface({ input: child.stdout })) {
    response = line;
    break;
  }
  clearTimeout(timeout);
  child.stdin.end();
  if (!child.killed) child.kill('SIGTERM');
  assert.ok(response, `${adapter.id} did not answer initialize`);
  return JSON.parse(response);
}

for (const adapter of manifest.adapters) {
  for (const file of adapter.files) {
    assert.equal(await sha256(new URL(file.path, adapterDir)), file.sha256);
  }
  // The Codex and Claude runtimes are Deno binaries, not Bun ones.
  for (const file of adapter.files) {
    if (!file.path.endsWith('-acp-runtime')) continue;
    const bytes = await readFile(new URL(file.path, adapterDir));
    assert.ok(bytes.includes('DENO_'), `${file.path} is not a Deno binary`);
    assert.ok(!bytes.includes('Bun v'), `${file.path} still contains the Bun runtime`);
  }
  const initialized = await initialize(adapter);
  assert.equal(initialized.result.agentInfo.version, adapter.version);
}

const releaseTarget = process.arch === 'arm64'
  ? 'aarch64-apple-darwin'
  : 'x86_64-apple-darwin';
await execFileAsync(process.execPath, [
  '--experimental-strip-types',
  new URL('prepareProviderReleaseAssets.ts', import.meta.url).pathname,
  releaseTarget
]);
const releaseDir = new URL(`../provider-release/${releaseTarget}/`, import.meta.url);
const releaseManifest = JSON.parse(
  await readFile(new URL(`provider-manifest-${releaseTarget}.json`, releaseDir), 'utf8')
);
assert.equal(releaseManifest.target, releaseTarget);
for (const adapter of releaseManifest.adapters) {
  for (const file of adapter.files) {
    assert.equal(
      await sha256(new URL(`provider-${releaseTarget}-${file.path}`, releaseDir)),
      file.sha256
    );
  }
}
await rm(new URL('../provider-release/', import.meta.url), { recursive: true, force: true });

console.log('release adapter checks passed');
