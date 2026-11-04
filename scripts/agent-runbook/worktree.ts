import { mkdirSync, existsSync, readdirSync, lstatSync, readFileSync } from 'node:fs';
import { join, basename, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { atomicWrite } from './runbook.ts';
import { inside, canonical, worktreeRoot } from './guard.ts';
import { git, tryGit, defaultBranch, registry, mutateRegistry, withLock, withWorktree, assertExclusive, repositories, stateRoot, liveCwds, lease } from './worktree-state.ts';
import { backupWorktree } from './backup.ts';
import { collectMeta } from './meta.ts';
export { backupWorktree, lease };

function managedPath(name: string, repo: string) {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$/.test(name) || name.includes('..')) throw Error('Worktree name must be a simple name within the managed root.');
  const common = git(repo, 'rev-parse', '--path-format=absolute', '--git-common-dir');
  const project = basename(dirname(common));
  const path = join(worktreeRoot(), project, name);
  if (!inside(path) || existsSync(path)) throw Error('Worktree destination already exists or escapes the root: ' + path);
  return path;
}
function register(path: string, repo: string) {
  const now = new Date().toISOString();
  let taskKey: string | undefined, taskId: string | undefined;
  try { ({ taskKey, taskId } = collectMeta(path, 'workflow', 'worktree')); } catch {}
  mutateRegistry(rows => { rows[canonical(path)] = { repo, branch: git(path, 'branch', '--show-current'), created: now, updated: now, leases: {}, taskKey, taskId }; });
}
export function createWorktree(name: string, cwd: string): string {
  if (git(cwd, 'rev-parse', '--is-bare-repository') !== 'false') throw Error('WorktreeCreate requires a non-bare checkout.');
  const path = managedPath(name, cwd);
  const main = git(cwd, 'worktree', 'list', '--porcelain').split('\n')[0].slice(9);
  // Reserve the destination before git mutates it. Never fall back to another root.
  return withWorktree(path, () => {
    mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    git(cwd, 'worktree', 'add', '-b', name, '--', path);
    register(path, main);
    return path;
  });
}
export function restore(branch: string, repo: string): string {
  git(repo, 'check-ref-format', '--branch', branch);
  const path = managedPath(branch.replaceAll('/', '-'), repo);
  const main = git(repo, 'worktree', 'list', '--porcelain').split('\n')[0].slice(9);
  return withWorktree(path, () => {
    mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    const ref = 'refs/remotes/origin/' + branch;
    git(repo, 'fetch', 'origin', 'refs/heads/' + branch + ':' + ref);
    if (tryGit(repo, 'rev-parse', '--verify', 'refs/heads/' + branch)) git(repo, 'worktree', 'add', '--', path, branch);
    else git(repo, 'worktree', 'add', '-b', branch, '--', path, ref);
    register(path, main); return path;
  });
}
function confirmedRemote(path: string, head: string, branch: string) {
  return git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/' + branch).split(/\s/)[0] === head;
}
function removable(path: string, done = false, owner?: string) {
  if (!inside(path)) throw Error('Outside managed root.');
  const row = assertExclusive(path, owner);
  if (canonical(row.repo) === canonical(path)) throw Error('Main checkout is never removed.');
  if (git(path, 'status', '--porcelain')) throw Error('Dirty worktree is kept.');
  const head = git(path, 'rev-parse', 'HEAD');
  if (row.backedUpHead !== head || !confirmedRemote(path, head, row.branch)) throw Error('Backup HEAD is not confirmed on remote.');
  if (!done && !row.done) {
    try { git(path, 'merge-base', '--is-ancestor', head, 'refs/heads/' + defaultBranch(path)); }
    catch { throw Error('Idle unmerged worktree is reported, never removed.'); }
  }
  return row;
}
function remove(path: string, owner?: string) {
  const row = removable(path, false, owner);
  git(row.repo, 'worktree', 'remove', '--', path);
  if (existsSync(path)) throw Error('Git left the worktree directory; keep residual files for inspection.');
  mutateRegistry(rows => { delete rows[path]; });
}
export function done(path: string, owner?: string) {
  path = canonical(path);
  assertExclusive(path, owner);
  const result = backupWorktree(path, owner);
  if (!['pushed', 'clean'].includes(result.status)) throw Error(result.reason || 'Backup failed; worktree kept.');
  return withWorktree(path, () => {
    removable(path, true, owner);
    mutateRegistry(rows => { rows[path].done = true; if (owner) delete rows[path].leases[owner]; });
    remove(path); return result;
  });
}
export type SweepReport = { removed: string[]; kept: { path: string; reason: string }[]; skipped?: string };
export function sweep(opts: { legacy: boolean; dryRun: boolean; hourly?: boolean }): SweepReport {
  const report: SweepReport = { removed: [], kept: [] };
  try {
    return withLock('sweep', () => {
      const stamp = join(stateRoot(), 'sweep.stamp');
      if (opts.hourly && existsSync(stamp) && Date.now() - Number(readFileSync(stamp, 'utf8')) < 3600000) return { ...report, skipped: 'Already swept this hour.' };
      for (const path of Object.keys(registry())) {
        if (!existsSync(path)) { report.kept.push({ path, reason: 'Missing worktree; use agent-worktree restore <branch> <repo>.' }); continue; }
        try {
          withWorktree(path, () => {
            removable(path);
            if (!opts.dryRun) remove(path);
          });
          report.removed.push(path);
        } catch (error) { report.kept.push({ path, reason: (error as Error).message }); }
      }
      if (opts.legacy) for (const row of status()) if (row.legacy) report.kept.push({ path: row.path, reason: 'Legacy: report only.' });
      if (opts.hourly && !opts.dryRun) atomicWrite(stamp, String(Date.now()));
      return report;
    });
  } catch (error: any) {
    if (error.code === 'EEXIST') return { ...report, skipped: 'Sweep locked by another process.' };
    throw error;
  }
}
function diskSize(path: string): number {
  let bytes = 0;
  for (const entry of readdirSync(path, { withFileTypes: true })) {
    const child = join(path, entry.name);
    bytes += entry.isDirectory() ? diskSize(child) : lstatSync(child).size;
  }
  return bytes;
}
export type StatusRow = { path: string; branch: string; dirty: boolean; ahead?: number; prUrl?: string; live?: boolean; processError?: string; size: number; stray: boolean; legacy: boolean; reclaimable: number; flag?: string };
export function status(): StatusRow[] {
  const rows: StatusRow[] = [], seen = new Set<string>();
  const owned = registry();
  let processes: ReturnType<typeof liveCwds> | undefined, processError: string | undefined;
  try { processes = liveCwds(); } catch { processError = 'Process inventory unavailable; cleanup is blocked.'; }
  for (const repo of repositories()) {
    for (const block of git(repo, 'worktree', 'list', '--porcelain').split('\n\n')) {
      const lines = block.split('\n'), path = lines.find(line => line.startsWith('worktree '))?.slice(9);
      if (!path || seen.has(path) || !existsSync(path)) continue;
      seen.add(path);
      const branch = lines.find(line => line.startsWith('branch '))?.slice(18) || 'HEAD';
      const live = processes?.some(p => canonical(p.path) === canonical(path) || inside(p.path, path));
      const dirty = !!git(path, 'status', '--porcelain');
      const aheadText = tryGit(path, 'rev-list', '--count', '@{upstream}..HEAD');
      let prUrl: string | undefined;
      try { prUrl = JSON.parse(execFileSync('gh', ['pr', 'view', '--json', 'url'], { cwd: path, encoding: 'utf8', timeout: 2000, stdio: ['ignore', 'pipe', 'ignore'] })).url; } catch {}
      const size = diskSize(path);
      let reclaimable = 0;
      if (owned[path] && processes && !live && !dirty) { try { removable(path); reclaimable = size; } catch {} }
      rows.push({ path, branch, dirty, ahead: aheadText ? Number(aheadText) : undefined, prUrl, live, processError, size, stray: !inside(path) && canonical(path) !== canonical(repo), legacy: !owned[path], reclaimable, flag: owned[path]?.flag });
    }
  }
  return rows;
}
