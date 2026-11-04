// Development packages declare source selection. Only authenticated post-start welcome verifies runtime identity.
import { promises as fs, createReadStream, createWriteStream, constants } from 'node:fs';
import { createHash } from 'node:crypto';
import { pipeline } from 'node:stream/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const exec = promisify(execFile);
const MAX_FILES = 4096, MAX_UNPACKED = 1024 ** 3, MAX_ARCHIVE = 512 * 1024 ** 2;
export type PackageInput = { publishDir: string; commit: string; outputFile: string; sourceDir: string };
export type PackageFile = { path: string; bytes: number; sha256: string; mode: '0644' | '0755' };
export type RemoteBackendManifest = {
  schemaVersion: 1; packageType: 'assembly-dotnet-remote-backend-dev'; buildKind: 'development';
  target: 'linux-x64'; commit: string; protocol: 1; format: 5;
  frameworks: { name: string; version: string }[]; files: PackageFile[];
};
export function sameBackendFiles(before: PackageFile[], after: PackageFile[]) {
  return before.length === after.length && after.every(file => before.some(old =>
    old.path === file.path && old.sha256 === file.sha256 && old.bytes === file.bytes && old.mode === file.mode));
}
const sha = (bytes: Uint8Array) => createHash('sha256').update(bytes).digest('hex');
function safePath(value: string) {
  if (!value || value.length > 240 || !/^[A-Za-z0-9_.+/-]+$/.test(value)
    || value.split('/').some(part => !part || part === '.' || part === '..')) throw new Error('Unsafe package pathname');
}
async function inventory(root: string) {
  const result: { relative: string; size: number; mode: number; ino: number; dev: number; mtime: number; ctime: number }[] = [];
  let total = 0;
  async function walk(relative: string) {
    const dir = path.join(root, relative);
    if (!(await fs.lstat(dir)).isDirectory()) throw new Error('Publish directories must be real directories');
    for (const name of (await fs.readdir(dir)).sort()) {
      const rel = relative ? `${relative}/${name}` : name; safePath(rel);
      const info = await fs.lstat(path.join(root, rel));
      if (info.isSymbolicLink()) throw new Error('Package symlinks are forbidden');
      if (info.isDirectory()) { await walk(rel); continue; }
      if (!info.isFile() || info.nlink !== 1) throw new Error('Package requires regular files without hardlinks');
      total += info.size;
      if (total > MAX_UNPACKED || result.length >= MAX_FILES - 1) throw new Error('Publish closure exceeds package limits');
      result.push({ relative: rel, size: info.size, mode: info.mode, ino: info.ino, dev: info.dev, mtime: info.mtimeMs, ctime: info.ctimeMs });
    }
  }
  await walk(''); return result;
}
async function sourceVersions(source: string) {
  for (const [relative, expression] of [
    ['dotnet/Assembly.Infrastructure/Socket/SocketConnection.cs', /\bconst int Protocol = (\d+);/g],
    ['dotnet/Assembly.Application/Features/Chat/Rows/RowStore.cs', /\bconst int FormatVersion = (\d+);/g]
  ] as const) {
    const matches = [...(await fs.readFile(path.join(source, relative), 'utf8')).matchAll(expression)];
    const expected = relative.endsWith('SocketConnection.cs') ? '1' : '5';
    if (matches.length !== 1 || matches[0][1] !== expected) throw new Error('Unsupported or ambiguous source protocol/format');
  }
}
async function prepareBackendDirectory(input: Pick<PackageInput, 'publishDir' | 'sourceDir' | 'commit'>, staging: string, previous?: string) {
  if (!/^[0-9a-f]{40}$/.test(input.commit)) throw new Error('An explicit full development commit is required');
  const publish = await fs.realpath(input.publishDir);
  if ((await fs.lstat(input.publishDir)).isSymbolicLink()) throw new Error('Publish root must not be a symlink');
  if (previous && await fs.realpath(previous) !== previous) throw new Error('Cached backend root must not be a symlink');
  await sourceVersions(input.sourceDir);
  const initial = await inventory(publish);
  for (const required of ['Assembly.Remote.dll', 'Assembly.Remote.deps.json', 'Assembly.Remote.runtimeconfig.json'])
    if (!initial.some(file => file.relative === required)) throw new Error(`Missing required publish file: ${required}`);
  const files: PackageFile[] = [];
  let reusedFiles = 0, changedBytes = 0;
  const oldFiles = previous ? JSON.parse(await fs.readFile(path.join(previous, 'manifest.json'), 'utf8')).files as PackageFile[] : [];
  for (const file of initial) {
    const name = `backend/${file.relative}`, destination = path.join(staging, name);
    await fs.mkdir(path.dirname(destination), { recursive: true });
    const handle = await fs.open(path.join(publish, file.relative), constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const before = await handle.stat();
      if (!before.isFile() || before.nlink !== 1 || before.ino !== file.ino || before.dev !== file.dev
        || before.size !== file.size || before.mtimeMs !== file.mtime || before.ctimeMs !== file.ctime)
        throw new Error('Publish closure changed while packaging');
      const hash = createHash('sha256'); let bytes = 0;
      const mode = file.mode & 0o111 ? '0755' : '0644';
      const reader = handle.createReadStream({ autoClose: false });
      reader.on('data', chunk => { bytes += chunk.length; hash.update(chunk); });
      let digest: string;
      if (previous) {
        for await (const _ of reader) { /* hash this publish file before selecting an unchanged host file */ }
        digest = hash.digest('hex');
        const old = oldFiles.find(item => item.path === name && item.sha256 === digest && item.bytes === bytes && item.mode === mode);
        if (old) {
          const existing = path.join(previous, name), info = await fs.lstat(existing);
          if (!info.isFile() || info.isSymbolicLink() || await fs.realpath(existing) !== existing || info.size !== bytes)
            throw new Error('Cached backend file is not an unchanged regular file');
          const oldHash = createHash('sha256');
          for await (const chunk of createReadStream(existing)) oldHash.update(chunk);
          if (oldHash.digest('hex') !== digest) throw new Error('Cached backend file differs from its manifest');
          // Kernel reflink where supported; regular private copy otherwise. Never hardlink mutable releases.
          await fs.copyFile(existing, destination, constants.COPYFILE_FICLONE);
          reusedFiles++;
        } else {
          await pipeline(handle.createReadStream({ start: 0, autoClose: false }), createWriteStream(destination, { flags: 'wx', mode: parseInt(mode, 8) }));
          changedBytes += bytes;
        }
        const check = createHash('sha256');
        for await (const chunk of createReadStream(destination)) check.update(chunk);
        if (check.digest('hex') !== digest) throw new Error('Publish closure changed while packaging');
      } else {
        await pipeline(reader, createWriteStream(destination, { flags: 'wx', mode: parseInt(mode, 8) }));
        digest = hash.digest('hex'); changedBytes += bytes;
      }
      const after = await handle.stat();
      if (bytes !== file.size || after.size !== file.size || after.mtimeMs !== file.mtime || after.ctimeMs !== file.ctime)
        throw new Error('Publish closure changed while packaging');
      await fs.chmod(destination, parseInt(mode, 8));
      files.push({ path: name, bytes, sha256: digest, mode });
    } finally { await handle.close(); }
  }
  if (JSON.stringify(await inventory(publish)) !== JSON.stringify(initial)) throw new Error('Publish closure changed while packaging');
  const runtime = JSON.parse(await fs.readFile(path.join(staging, 'backend/Assembly.Remote.runtimeconfig.json'), 'utf8'));
  const frameworks = runtime.runtimeOptions?.frameworks;
  if (!Array.isArray(frameworks) || !frameworks.length || frameworks.some(item =>
    !item || typeof item.name !== 'string' || !/^[A-Za-z0-9.]+$/.test(item.name)
    || typeof item.version !== 'string' || !/^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$/.test(item.version)))
    throw new Error('Missing or invalid framework requirements');
  const manifest: RemoteBackendManifest = { schemaVersion: 1, packageType: 'assembly-dotnet-remote-backend-dev',
    buildKind: 'development', target: 'linux-x64', commit: input.commit, protocol: 1, format: 5,
    frameworks: frameworks.map(({ name, version }) => ({ name, version })), files };
  const encoded = Buffer.from(JSON.stringify(manifest, null, 2) + '\n');
  if (files.reduce((sum, file) => sum + file.bytes, encoded.length) > MAX_UNPACKED) throw new Error('Unpacked package exceeds limit');
  await fs.writeFile(path.join(staging, 'manifest.json'), encoded, { mode: 0o644 });
  await fs.chmod(path.join(staging, 'manifest.json'), 0o644);
  const removedFiles = oldFiles.filter(file => !files.some(current => current.path === file.path)).length;
  return { manifest, encoded, manifestSha256: sha(encoded), reusedFiles, changedFiles: files.length - reusedFiles, changedBytes, removedFiles };
}

// Directory publication and full archives share the same checked closure and manifest. Dev sync builds on the
// target: only changed Git source objects cross SSH; unchanged payload files are reused from its active release.
export async function writeRemoteBackendDirectory(input: Pick<PackageInput, 'publishDir' | 'sourceDir' | 'commit'> & { outputDir: string }, previous?: string) {
  const output = path.resolve(input.outputDir), publish = await fs.realpath(input.publishDir);
  const relative = path.relative(publish, output);
  if (!relative.startsWith('..' + path.sep) && relative !== '..') throw new Error('Package output must be outside publish directory');
  await fs.mkdir(path.dirname(output), { recursive: true });
  const temporary = await fs.mkdtemp(path.join(path.dirname(output), '.remote-directory-'));
  try {
    const result = await prepareBackendDirectory(input, temporary, previous);
    await fs.rename(temporary, output);
    return { manifest: result.manifest, manifestSha256: result.manifestSha256, reusedFiles: result.reusedFiles, changedFiles: result.changedFiles, changedBytes: result.changedBytes, removedFiles: result.removedFiles };
  } finally { await fs.rm(temporary, { recursive: true, force: true }); }
}

export async function writeRemoteBackendPackage(input: PackageInput) {
  const publish = await fs.realpath(input.publishDir), output = path.resolve(input.outputFile);
  const relativeOutput = path.relative(publish, output);
  if (!relativeOutput.startsWith('..' + path.sep) && relativeOutput !== '..') throw new Error('Package output must be outside publish directory');
  await fs.mkdir(path.dirname(output), { recursive: true });
  const staging = await fs.mkdtemp(path.join(path.dirname(output), '.remote-package-'));
  try {
    const { manifest, encoded } = await prepareBackendDirectory(input, staging);
    const files = manifest.files;
    const list = path.join(staging, 'readset');
    await fs.writeFile(list, ['manifest.json', ...files.map(file => file.path)].join('\n') + '\n');
    const archive = path.join(staging, 'bundle.tar.gz');
    await exec('tar', ['--format=ustar', '--no-recursion', '-czf', archive, '-C', staging, '-T', list],
      { env: { ...process.env, COPYFILE_DISABLE: '1' } });
    const info = await fs.stat(archive);
    if (info.size > MAX_ARCHIVE) throw new Error('Compressed package exceeds limit');
    const hash = createHash('sha256'); for await (const part of createReadStream(archive)) hash.update(part);
    await fs.rename(archive, output);
    return { archivePath: output, bytes: info.size, sha256: hash.digest('hex'), manifestSha256: sha(encoded), commit: input.commit, target: 'linux-x64' as const };
  } finally { await fs.rm(staging, { recursive: true, force: true }); }
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [publishDir, commit, outputFile, sourceDir] = process.argv.slice(2);
  if (!publishDir || !commit || !outputFile || !sourceDir || process.argv.length !== 6)
    throw new Error('Usage: remote-backend-package.ts <publishDir> <commit> <outputFile> <sourceDir>');
  console.log(JSON.stringify(await writeRemoteBackendPackage({ publishDir, commit, outputFile, sourceDir })));
}
