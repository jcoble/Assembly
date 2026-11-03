import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, existsSync, fstatSync, lstatSync, mkdirSync, openSync, realpathSync, unlinkSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, dirname, isAbsolute, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export function parseArgs(args: string[]): { worktree: string; manifest: string; dryRun: boolean } {
  let worktree = ''; let manifest = ''; let dryRun = false;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if ((arg === '--worktree' || arg === '--manifest') && args[i + 1] && !args[i + 1].startsWith('--')) {
      if (arg === '--worktree') { if (worktree) throw new Error('Duplicate --worktree.'); worktree = args[++i]; }
      else { if (manifest) throw new Error('Duplicate --manifest.'); manifest = args[++i]; }
    } else if (arg === '--dry-run' && !dryRun) dryRun = true;
    else throw new Error(`Unknown or incomplete argument: ${arg}`);
  }
  if (!worktree || !manifest) throw new Error('--worktree and --manifest are required.');
  return { worktree, manifest, dryRun };
}
export function safePath(input: string, mustExist = true): string {
  if (!isAbsolute(input) || input.split(sep).some(part => part === '.' || part === '..')) throw new Error('Expected absolute path without traversal.');
  const full = resolve(input);
  for (let at = full;; at = dirname(at)) {
    try { const stat = lstatSync(at); if (stat.isSymbolicLink()) throw new Error('Symlink paths are refused.'); }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
    if (dirname(at) === at) break;
  }
  if (mustExist && !existsSync(full)) throw new Error('Explicit path does not exist.');
  let ancestor = full;
  const suffix: string[] = [];
  while (!existsSync(ancestor)) { suffix.unshift(basename(ancestor)); ancestor = dirname(ancestor); }
  return join(realpathSync(ancestor), ...suffix);
}
export function profileFor(input: string, userHome = homedir()): { root: string; home: string } {
  const root = realpathSync(safePath(input));
  for (const marker of ['desktop/Cargo.toml', 'dotnet/Assembly.Desktop/Assembly.Desktop.csproj']) {
    if (!existsSync(join(root, marker))) throw new Error(`Not an Assembly worktree (missing ${marker}).`);
  }
  const key = createHash('sha256').update(root).digest('hex').slice(0, 16);
  const name = basename(root).replace(/[^a-zA-Z0-9-]/g, '-');
  return { root, home: safePath(join(userHome, 'assembly-dev', 'worktrees', `${name}-${key}`), false) };
}
export function takeLauncherLock(home: string): () => void {
  safePath(home, false); mkdirSync(home, { recursive: true, mode: 0o700 });
  const path = join(home, 'launcher.lock');
  let fd: number;
  try { fd = openSync(path, 'wx', 0o600); }
  catch (error) { if ((error as NodeJS.ErrnoException).code === 'EEXIST') throw new Error('Existing launcher.lock: close the isolated app; no lock is removed.'); throw error; }
  const identity = fstatSync(fd); writeFileSync(fd, JSON.stringify({ launcher: process.pid, purpose: 'dev-seed' }) + '\n');
  return () => {
    try { const current = lstatSync(path); if (current.ino === identity.ino && current.dev === identity.dev) unlinkSync(path); }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
    finally { closeSync(fd); }
  };
}
export function waitForOwnedClose(owned: ReturnType<typeof spawn>): Promise<number> {
  return new Promise((done, reject) => {
    let failure: Error | undefined;
    owned.once('error', error => { failure = error; });
    owned.once('close', code => failure ? reject(failure) : done(code ?? 1));
  });
}
const help = `Usage: node scripts/seed-dev.ts --worktree PATH --manifest PATH [--dry-run]
Import explicitly listed ACP captures, rowFormat-4 normalized rows with complete detail sidecars,
or a compatible closed current-format safe database backup. Sources stay private and read-only.
Destination: ~/assembly-dev/worktrees/<worktree-name>-<sha256(canonical-path)[0:16]>/assembly.db
Stop the isolated app first. Existing sessions, drafts, settings and files are preserved; divergent
replicas are refused. No provider resumes, remote connections, owner clones, app launch or overwrite.
Output reports unique real/synthetic sessions, rows/tools/details/files/attachments and limitations.
Dry run validates without destination writes. Never publish private manifests or source recordings.`;
export async function main(args: string[]): Promise<void> {
  if (args.length === 1 && args[0] === '--help') { console.log(help); return; }
  const options = parseArgs(args); const { root, home } = profileFor(options.worktree);
  const manifestPath = safePath(options.manifest);
  const info = lstatSync(manifestPath);
  if (!info.isFile() || info.nlink !== 1) throw new Error('Manifest must be a regular file without hardlink aliases.');
  console.log(`Worktree: ${root}\nLocal database: ${join(home, 'assembly.db')}`);
  const release = options.dryRun ? () => {} : takeLauncherLock(home);
  const environment = { ...process.env, MSBUILDDISABLENODEREUSE: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
  delete environment.ASSEMBLY_REMOTE;
  let child: ReturnType<typeof spawn> | undefined;
  let phase: 'build' | 'apply' = 'build';
  let cancelled: 'SIGINT' | 'SIGTERM' | undefined;
  const cancel = (signal: 'SIGINT' | 'SIGTERM') => {
    cancelled = signal;
    if (phase === 'apply') child?.kill(signal);
    else console.error('Cancellation requested; waiting for the owned helper build to close.');
  };
  const interrupt = () => cancel('SIGINT');
  const terminate = () => cancel('SIGTERM');
  process.once('SIGINT', interrupt); process.once('SIGTERM', terminate);
  const checkCancellation = () => {
    if (cancelled) throw Object.assign(new Error('Development seed cancelled.'), { exitCode: cancelled === 'SIGINT' ? 130 : 143 });
  };
  try {
    const output = join(root, 'dotnet/Assembly.DevSeed/bin/dev-seed-cli');
    child = spawn('dotnet', ['build', join(root, 'dotnet/Assembly.DevSeed'), '--configuration', 'Debug', '--output', output, '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], { cwd: root, env: environment, stdio: ['ignore', 'inherit', 'inherit'] });
    const built = await waitForOwnedClose(child);
    checkCancellation();
    if (built !== 0) throw new Error(`Development seed helper build failed (${built}).`);
    phase = 'apply';
    child = spawn('dotnet', [join(output, 'Assembly.DevSeed.dll')], { cwd: root, env: environment, stdio: ['pipe', 'inherit', 'inherit'] });
    const applied = waitForOwnedClose(child);
    child.stdin!.on('error', () => {});
    child.stdin!.end(JSON.stringify({ version: 1, canonicalWorktree: root, computedHome: home, manifestPath, dryRun: options.dryRun }));
    const code = await applied;
    checkCancellation();
    if (code !== 0) throw new Error(`Development seed companion failed (${code}).`);
  } finally { process.removeListener('SIGINT', interrupt); process.removeListener('SIGTERM', terminate); release(); }

}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = error.exitCode ?? 1; });
}
