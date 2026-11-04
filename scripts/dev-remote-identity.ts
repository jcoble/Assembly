import { createHash } from 'node:crypto';
import { lstat, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { command } from './dev-remote-process.ts';

// The Remote -> Infrastructure -> Application/Engine -> Domain project closure. Common MSBuild/NuGet inputs
// and the ACP 2 launcher contract are included; Rust, Desktop, tests and deployment tooling are not build inputs.
// An exact match on these is what a check before a PR requires: side effects of a difference can't always be known.
export function backendInput(file: string) {
  return /^dotnet\/Assembly\.(Remote|Infrastructure|Application|Engine|Domain)\//.test(file)
    || /^(dotnet\/)?(Directory\.[^/]+\.(props|targets)|[Nn]u[Gg]et\.[Cc]onfig|global\.json)$/.test(file)
    || adapterInput(file)
    || ['scripts/dev-remote-build-profile.ts', 'scripts/dev-remote-identity.ts', 'scripts/dev-remote-process.ts'].includes(file);
}
// What the published adapter bundle is built from: the daily job compares these with its latest release.
export const packagingInputs = ['scripts/adapters/claude-launcher.ts', 'scripts/package-adapters.ts'];
// Adapters, launchers and their scripts live on local and on every remote. A change here updates them on both, the same
// way (dev-toolchain.ts, ensureTools); it needs no backend build, so it isn't what makes the backend differ on a launch.
export const adapterSources = [...packagingInputs, 'dotnet/scripts/agents-file.sh', 'scripts/dev-toolchain.ts'];
export function adapterInput(file: string) {
  return adapterSources.includes(file);
}
export async function sourcesDigest(files: string[], read: (file: string) => Promise<string | Buffer | undefined>) {
  const digest = createHash('sha256');
  for (const file of files) digest.update(file + '\0').update((await read(file)) ?? 'missing').update('\0');
  return digest.digest('hex');
}
// The list: every kind of backend change that needs the remote to change too. One difference here is why a dev launch
// builds and deploys its backend; anything else leaves the backend alone. Off the list: the desktop's client side of the
// remote, features only the desktop hosts, tooling that isn't compiled in, docs, and the adapter sources above, which
// have their own check. The release version counts as well.
const offTheList = new RegExp([
  String.raw`^dotnet/Assembly\.Infrastructure/(Remote|Notion|Clock)/`,
  String.raw`^dotnet/Assembly\.Infrastructure/Updates/(?!RemoteCli)`,
  String.raw`^dotnet/Assembly\.Infrastructure/Workflows/RemoteWorkflowMirror\.cs$`,
  String.raw`^dotnet/Assembly\.Application/Features/(Notion|Workspaces)/`,
  String.raw`\.md$`,
  String.raw`^scripts/dev-remote-(identity|process)\.ts$`,
].join('|'));
export function remoteContractInput(file: string) {
  return backendInput(file) && !adapterInput(file) && !offTheList.test(file);
}
export function digestInputs(inputs: string[], version: string) {
  return createHash('sha256').update(`assembly-dev-remote-v3:ACP2:${version}\n` + [...inputs].sort().join('\n')).digest('hex');
}
export function digestContract(inputs: string[], version: string) {
  return createHash('sha256').update(`assembly-dev-remote-list-v1:ACP2:${version}\n` + [...inputs].sort().join('\n')).digest('hex');
}
const path = (line: string) => line.split('\t')[1] ?? '';
// The app's real release version, which moves only on a release: the package version desktop/Cargo.toml declares.
function versionIn(manifest: string) {
  return /^version\s*=\s*"([^"]+)"/m.exec(manifest)?.[1] ?? 'none';
}
async function committedVersion(root: string, commit: string, signal?: AbortSignal) {
  try { return versionIn(await command('git', ['show', `${commit}:desktop/Cargo.toml`], { cwd: root, signal })); }
  catch (error) { signal?.throwIfAborted(); return versionIn(''); }
}
async function workingVersion(root: string) {
  try { return versionIn(await readFile(join(root, 'desktop/Cargo.toml'), 'utf8')); }
  catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; return versionIn(''); }
}
async function committedInputs(root: string, commit: string, signal?: AbortSignal) {
  if (!/^[a-f0-9]{40}$/.test(commit)) throw new Error('unknown running backend commit');
  const tree = await command('git', ['ls-tree', '-r', commit], { cwd: root, signal });
  const inputs = tree.split('\n').filter(line => backendInput(path(line)));
  if (!inputs.some(line => line.endsWith('dotnet/Assembly.Remote/Assembly.Remote.csproj')))
    throw new Error('remote backend source is missing from the selected commit');
  return inputs;
}
export async function backendIdentity(root: string, commit: string, signal?: AbortSignal) {
  return digestInputs(await committedInputs(root, commit, signal), await committedVersion(root, commit, signal));
}
export async function remoteContract(root: string, commit: string, signal?: AbortSignal) {
  return digestContract((await committedInputs(root, commit, signal)).filter(line => remoteContractInput(path(line))), await committedVersion(root, commit, signal));
}
// The files actually being built, including dirty edits; git is never needed by a delivered binary.
async function workingInputs(root: string, commit: string, signal?: AbortSignal) {
  const tree = await command('git', ['ls-tree', '-r', commit], { cwd: root, signal });
  const entries = new Map(tree.split('\n').filter(line => backendInput(path(line))).map(line => [path(line), line]));
  const changed = await command('git', ['diff', '--name-only', commit], { cwd: root, signal });
  const untracked = await command('git', ['ls-files', '--others', '--exclude-standard'], { cwd: root, signal });
  for (const file of new Set([...changed.split('\n'), ...untracked.split('\n')].filter(backendInput))) {
    try {
      const info = await lstat(join(root, file));
      if (!info.isFile()) throw Error('backend inputs must be regular files: ' + file);
      const hash = await command('git', ['hash-object', '--', file], { cwd: root, signal });
      entries.set(file, `${info.mode & 0o111 ? '100755' : '100644'} blob ${hash}\t${file}`);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      entries.delete(file);
    }
  }
  return [...entries.values()];
}
export async function workingBackendIdentity(root: string, commit: string, signal?: AbortSignal) {
  return digestInputs(await workingInputs(root, commit, signal), await workingVersion(root));
}
export async function workingRemoteContract(root: string, commit: string, signal?: AbortSignal) {
  return digestContract((await workingInputs(root, commit, signal)).filter(line => remoteContractInput(path(line))), await workingVersion(root));
}
export async function requireCleanBackend(root: string, signal?: AbortSignal, input: (file: string) => boolean = backendInput) {
  const [paths, untracked] = await Promise.all([
    command('git', ['diff', '--name-only', 'HEAD'], { cwd: root, signal }),
    command('git', ['ls-files', '--others', '--exclude-standard'], { cwd: root, signal }),
  ]);
  if ([...paths.split('\n'), ...untracked.split('\n')].some(input))
    throw new Error('uncommitted backend changes: Workbox Test stays behind this checkout; commit them before launching again');
}
export type Lane = { name: string; number: number; unit: string; data: string; port: number };
export function lane(name: string, number: number): Lane {
  if (['main', 'base', 'rail', 'remote', 'assembly-dotnet', 'assembly-dotnet-base', 'assembly-dotnet-rail', 'assembly-remote'].includes(name))
    throw new Error('lane identity refuses protected names');
  if (!/^[a-z0-9][a-z0-9-]*$/.test(name) || !Number.isInteger(number) || number < 1 || number > 9)
    throw new Error('automatic remote sync refuses main or an invalid lane');
  return { name, number, unit: `assembly-dotnet-${name}`, data: `assembly-dotnet-${name}`, port: 9797 + number * 10 };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const root = process.argv[2];
  const commit = await command('git', ['rev-parse', '--verify', 'HEAD^{commit}'], { cwd: root });
  // Stamped into the binary (dotnet/Directory.Build.targets): exact identity, list identity, release version.
  console.log(`${await workingBackendIdentity(root, commit)} ${await workingRemoteContract(root, commit)} ${await workingVersion(root)}`);
}
