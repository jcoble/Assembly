import { test } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { writeRemoteBackendPackage } from './remote-backend-package.ts';
const exec = promisify(execFile);
const commit = 'a'.repeat(40), hash = (data: Uint8Array) => createHash('sha256').update(data).digest('hex');
async function fixture(t: any) {
  const root = await fs.mkdtemp(path.join(tmpdir(), 'remote-package-test-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const publishDir = path.join(root, 'publish'), sourceDir = path.join(root, 'source'), outputFile = path.join(root, 'out/backend.tar.gz');
  await fs.mkdir(publishDir);
  const source = {
    'dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs': 'internal const int Protocol = 1;',
    'dotnet/Assembly.Application/Features/Chat/Rows/RowStore.cs': 'public const int FormatVersion = 5;'
  };
  for (const [name, text] of Object.entries(source)) { await fs.mkdir(path.dirname(path.join(sourceDir, name)), { recursive: true }); await fs.writeFile(path.join(sourceDir, name), text); }
  await fs.writeFile(path.join(publishDir, 'Assembly.Remote.dll'), 'disposable fake assembly bytes');
  await fs.writeFile(path.join(publishDir, 'Assembly.Remote.deps.json'), '{}');
  await fs.writeFile(path.join(publishDir, 'Assembly.Remote.runtimeconfig.json'), JSON.stringify({ runtimeOptions: { frameworks: [
    { name: 'Microsoft.NETCore.App', version: '10.0.0' }, { name: 'Microsoft.AspNetCore.App', version: '10.0.0' }
  ] } }));
  return { root, publishDir, sourceDir, outputFile, commit };
}
test('development bundle contains exact recursive closure, independent hashes and normalized modes without modifying source', async t => {
  const input = await fixture(t);
  await fs.mkdir(path.join(input.publishDir, 'runtimes/linux-x64/native'), { recursive: true });
  const native = path.join(input.publishDir, 'runtimes/linux-x64/native/libsample.so');
  await fs.writeFile(native, Buffer.from([0, 1, 255]), { mode: 0o764 });
  const result = await writeRemoteBackendPackage(input), bytes = await fs.readFile(input.outputFile);
  assert.equal(result.sha256, hash(bytes)); assert.equal(result.bytes, bytes.length); assert.equal(result.commit, commit);
  const { stdout } = await exec('tar', ['-tzf', input.outputFile]);
  const entries = stdout.trim().split('\n');
  const listing = (await exec('tar', ['-tvzf', input.outputFile])).stdout;
  assert(listing.split('\n').some(line => line.startsWith('-rw-r--r--') && line.endsWith(' manifest.json')));
  assert.equal(entries.length, 5); assert(entries.includes('backend/runtimes/linux-x64/native/libsample.so'));
  assert(!entries.some(entry => /installer|source|readset/.test(entry)));
  const manifestBytes = (await exec('tar', ['-xOzf', input.outputFile, 'manifest.json'], { encoding: 'buffer' })).stdout;
  assert.equal(hash(manifestBytes), result.manifestSha256);
  const manifest = JSON.parse(manifestBytes.toString());
  assert.equal(manifest.packageType, 'assembly-dotnet-remote-backend-dev'); assert.equal(manifest.buildKind, 'development');
  assert.equal(manifest.protocol, 1); assert.equal(manifest.format, 5); assert.equal(manifest.target, 'linux-x64');
  assert.deepEqual(manifest.frameworks, [{ name: 'Microsoft.NETCore.App', version: '10.0.0' }, { name: 'Microsoft.AspNetCore.App', version: '10.0.0' }]);
  assert.deepEqual(entries.slice(1), manifest.files.map((file: any) => file.path));
  for (const file of manifest.files) {
    const content = (await exec('tar', ['-xOzf', input.outputFile, file.path], { encoding: 'buffer' })).stdout;
    assert.equal(file.sha256, hash(content)); assert.equal(file.bytes, content.length);
    assert.equal(file.mode, file.path.endsWith('.so') ? '0755' : '0644');
  }
  assert.equal((await fs.stat(native)).mode & 0o777, 0o764);
});
test('declared development commit is selection metadata, not an embedded runtime verification claim', async t => {
  const input = await fixture(t);
  const result = await writeRemoteBackendPackage(input);
  assert.equal(result.commit, commit); // Fixture DLL is deliberately not executable and no process is launched.
  const manifest = JSON.parse((await exec('tar', ['-xOzf', input.outputFile, 'manifest.json'])).stdout);
  assert.deepEqual(Object.keys(manifest).sort(), ['buildKind', 'commit', 'files', 'format', 'frameworks', 'packageType', 'protocol', 'schemaVersion', 'target'].sort());
});
for (const kind of ['symlink', 'hardlink', 'unsafe-name'] as const) test(`refuses ${kind} without publishing output`, async t => {
  const input = await fixture(t);
  if (kind === 'symlink') await fs.symlink('Assembly.Remote.dll', path.join(input.publishDir, 'linked.dll'));
  if (kind === 'hardlink') await fs.link(path.join(input.publishDir, 'Assembly.Remote.dll'), path.join(input.publishDir, 'linked.dll'));
  if (kind === 'unsafe-name') await fs.writeFile(path.join(input.publishDir, 'bad\\name'), 'bad');
  await assert.rejects(writeRemoteBackendPackage(input), /symlink|hardlink|Unsafe package pathname/);
  await assert.rejects(fs.stat(input.outputFile), { code: 'ENOENT' });
});
test('missing required closure preserves an existing output', async t => {
  const input = await fixture(t); await fs.mkdir(path.dirname(input.outputFile)); await fs.writeFile(input.outputFile, 'previous package');
  await fs.unlink(path.join(input.publishDir, 'Assembly.Remote.deps.json'));
  await assert.rejects(writeRemoteBackendPackage(input), /Missing required publish file/);
  assert.equal(await fs.readFile(input.outputFile, 'utf8'), 'previous package');
});
test('refuses incompatible source protocol and invalid runtime requirements', async t => {
  const input = await fixture(t);
  const protocol = path.join(input.sourceDir, 'dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs');
  await fs.writeFile(protocol, 'const int Protocol = 2;');
  await assert.rejects(writeRemoteBackendPackage(input), /source protocol/);
  await fs.writeFile(protocol, 'const int Protocol = 1;');
  await fs.writeFile(path.join(input.publishDir, 'Assembly.Remote.runtimeconfig.json'), '{}');
  await assert.rejects(writeRemoteBackendPackage(input), /framework requirements/);
  await assert.rejects(fs.stat(input.outputFile), { code: 'ENOENT' });
});
test('actual publish mutation after inventory is refused and staging is cleaned', async t => {
  const input = await fixture(t), original = fs.open.bind(fs);
  let changed = false;
  t.mock.method(fs, 'open', async (name: any, ...args: any[]) => {
    const handle = await original(name, ...args as [any]);
    if (name === path.join(input.publishDir, 'Assembly.Remote.dll') && !changed) {
      changed = true; await fs.writeFile(name, 'changed bytes after captured inventory');
    }
    return handle;
  });
  await assert.rejects(writeRemoteBackendPackage(input), /changed while packaging/);
  assert(changed); assert.deepEqual(await fs.readdir(path.dirname(input.outputFile)), []);
});
