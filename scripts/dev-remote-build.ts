import { promises as fs, createReadStream } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { dirname, join } from 'node:path';
import { writeRemoteBackendDirectory, type RemoteBackendManifest } from './remote-backend-package.ts';
import { backendIdentity } from './dev-remote-identity.ts';
import { remotePublishArguments } from './dev-remote-build-profile.ts';
import { command } from './dev-remote-process.ts';
import { stage } from './dev-remote-timing.ts';

const hash = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
async function hashFile(file: string) {
  const digest = createHash('sha256');
  for await (const chunk of createReadStream(file)) digest.update(chunk);
  return digest.digest('hex');
}
export type PreparedBackend = { identity: string; commit: string; directory: string; manifestSha256: string };

// A release is bin/<commit>-<manifest>/backend/Assembly.Remote.dll. A host published before that layout has no
// manifest, so it has nothing to compare against or reuse files from.
export async function installedRelease(dll: string) {
  const directory = dirname(dirname(dll));
  try { return { directory, manifest: JSON.parse(await fs.readFile(join(directory, 'manifest.json'), 'utf8')) as RemoteBackendManifest }; }
  catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined; throw error; }
}
// Keeps what is running and what it replaced. Only directories this path wrote are candidates.
export async function pruneReleases(root: string, keep: string[]) {
  const bin = join(root, 'bin');
  for (const entry of await fs.readdir(bin))
    if (/^[a-f0-9]{40}-[a-f0-9]{64}$/.test(entry) && !keep.includes(join(bin, entry)))
      await fs.rm(join(bin, entry), { recursive: true, force: true });
}

// A prepared release is only a build cache. Verify its complete closure and source identity before reusing it;
// authenticated post-start welcome remains the authority for the actual running host.
export async function readPreparedBackend(root: string, identity: string, source: string, signal?: AbortSignal): Promise<PreparedBackend | undefined> {
  let cached: PreparedBackend;
  try { cached = JSON.parse(await fs.readFile(join(root, 'prepared-backend.json'), 'utf8')); }
  catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined; throw error; }
  if (cached.identity !== identity) return undefined;
  if (!/^[a-f0-9]{40}$/.test(cached.commit) || !/^[a-f0-9]{64}$/.test(cached.manifestSha256)
    || cached.directory !== join(root, 'bin', cached.commit + '-' + cached.manifestSha256)
    || await fs.realpath(cached.directory) !== cached.directory) throw Error('prepared backend cache has an invalid lane path');
  if (await backendIdentity(source, cached.commit, signal) !== identity) throw Error('prepared backend cache source identity differs');
  const encoded = await fs.readFile(join(cached.directory, 'manifest.json'));
  if (hash(encoded) !== cached.manifestSha256) throw Error('prepared backend cache manifest changed');
  const manifest = JSON.parse(encoded.toString()) as RemoteBackendManifest;
  if (manifest.commit !== cached.commit || manifest.schemaVersion !== 1 || !Array.isArray(manifest.files)) throw Error('prepared backend cache commit or schema differs');
  const expected = new Set(['manifest.json', ...manifest.files.map(file => file.path)]);
  const actual = new Set<string>();
  for (const relative of await fs.readdir(cached.directory, { recursive: true })) {
    const info = await fs.lstat(join(cached.directory, relative));
    if (info.isSymbolicLink()) throw Error('prepared backend cache contains a symlink');
    if (info.isFile()) actual.add(relative);
  }
  if (actual.size !== expected.size || [...actual].some(file => !expected.has(file))) throw Error('prepared backend cache closure changed');
  for (const item of manifest.files) {
    signal?.throwIfAborted();
    if (!/^backend\/[A-Za-z0-9_.+/-]+$/.test(item.path) || item.path.split('/').some(part => !part || part === '.' || part === '..'))
      throw Error('prepared backend cache contains an unsafe path');
    const file = join(cached.directory, item.path), info = await fs.lstat(file);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1 || await fs.realpath(file) !== file
      || info.size !== item.bytes || (info.mode & 0o777) !== Number.parseInt(item.mode, 8)
      || await hashFile(file) !== item.sha256) throw Error('prepared backend cache file changed: ' + item.path);
  }
  return cached;
}

export async function prepareBackend(root: string, request: { identity: string; commit: string }, currentDll: string | undefined, signal: AbortSignal) {
  const built = stage('build');
  const cached = await readPreparedBackend(root, request.identity, process.cwd(), signal);
  if (cached) {
    built({ cacheHit: true, built: false });
    return cached;
  }
  const temporary = await fs.mkdtemp(join(root, '.dev-sync-'));
  try {
    const publish = join(temporary, 'publish');
    await command('dotnet', ['publish', 'dotnet/Assembly.Remote', ...remotePublishArguments,
      `-p:SourceRevisionId=${request.commit}`, '-o', publish], { signal, live: true });
    try { await command('git', ['diff', '--quiet', 'HEAD'], { signal }); }
    catch {
      signal.throwIfAborted();
      throw Error('the build modified tracked backend source; host kept unchanged; inspect the lane build checkout git diff');
    }
    built({ cacheHit: false });
    const packaged = stage('package');
    const previous = currentDll ? (await installedRelease(currentDll))?.directory : undefined;
    const result = await writeRemoteBackendDirectory({ publishDir: publish, sourceDir: process.cwd(), commit: request.commit, outputDir: join(temporary, 'package') }, previous);
    const directory = join(root, 'bin', request.commit + '-' + result.manifestSha256);
    await fs.mkdir(join(root, 'bin'), { recursive: true });
    await fs.rename(join(temporary, 'package'), directory);
    packaged({ files: result.manifest.files.length, reusedFiles: result.reusedFiles, changedFiles: result.changedFiles, removedFiles: result.removedFiles, changedBytes: result.changedBytes, binaryNetworkBytes: 0 });
    const prepared = { identity: request.identity, commit: request.commit, directory, manifestSha256: result.manifestSha256 };
    const pointer = join(root, 'prepared-backend.json'), staging = pointer + '.' + randomUUID();
    await fs.writeFile(staging, JSON.stringify(prepared) + '\n', { flag: 'wx', mode: 0o600 });
    await fs.rename(staging, pointer);
    return prepared;
  } finally { await fs.rm(temporary, { recursive: true, force: true }); }
}
