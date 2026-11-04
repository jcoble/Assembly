import { mkdirSync, rmSync, readFileSync, existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { atomicWrite } from './runbook.ts';
import { inside, canonical } from './guard.ts';
let deadline = Infinity;
export function setHookBudget(milliseconds: number) { deadline = Date.now() + milliseconds; }
export function remainingBudget(maximum: number) {
  const budget = Math.min(maximum, deadline - Date.now());
  if (budget <= 0) throw Error('Hook I/O budget exhausted; worktree kept.');
  return budget;
}

export const stateRoot = () => join(homedir(), '.local/state/agent-runbook');
export type Lease = { pid?: number };
export type WorktreeRecord = { repo: string; branch: string; created: string; updated: string; leases: Record<string, Lease>; backedUpHead?: string; done?: boolean; flag?: string };
export const registryPath = () => join(stateRoot(), 'worktrees.json');
export function registry(): Record<string, WorktreeRecord> {
  return existsSync(registryPath()) ? JSON.parse(readFileSync(registryPath(), 'utf8')) : {};
}
export function withLock<T>(name: string, action: () => T): T {
  const lock = join(stateRoot(), name + '.lock');
  mkdirSync(stateRoot(), { recursive: true, mode: 0o700 });
  mkdirSync(lock, { mode: 0o700 });
  try { return action(); } finally { rmSync(lock, { recursive: true }); }
}
export function mutateRegistry(action: (rows: Record<string, WorktreeRecord>) => void) {
  withLock('registry', () => { const rows = registry(); action(rows); atomicWrite(registryPath(), JSON.stringify(rows)); });
}
export function withWorktree<T>(path: string, action: () => T) {
  return withLock('tree-' + createHash('sha256').update(canonical(path)).digest('hex'), action);
}
export function git(cwd: string, ...args: string[]) {
  return execFileSync('git', ['-C', cwd, ...args], { env: { ...process.env, GIT_OPTIONAL_LOCKS: '0' }, encoding: 'utf8', timeout: remainingBudget(10000), maxBuffer: 8388608, stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
export function tryGit(cwd: string, ...args: string[]) { try { return git(cwd, ...args); } catch { return ''; } }
export function defaultBranch(repo: string): string {
  const ref = tryGit(repo, 'symbolic-ref', '--short', 'refs/remotes/origin/HEAD');
  if (!ref.startsWith('origin/')) throw Error('Default branch is unknown; set origin/HEAD before backup.');
  return ref.slice('origin/'.length);
}
export function onDefault(path: string) {
  try { git(path, 'merge-base', '--is-ancestor', 'HEAD', 'refs/remotes/origin/' + defaultBranch(path)); return true; } catch { return false; }
}
export function liveCwds(): { pid: number; path: string }[] {
  // lsof exit 1 also means no matches, but an empty/failed inventory is not proof of safety.
  const text = execFileSync('lsof', ['-nP', '-a', '-d', 'cwd', '-Fpn'], { encoding: 'utf8', timeout: remainingBudget(3000), maxBuffer: 8388608, stdio: ['ignore', 'pipe', 'pipe'] });
  let pid = 0;
  const result: { pid: number; path: string }[] = [];
  for (const line of text.split('\n')) {
    if (line.startsWith('p')) pid = Number(line.slice(1));
    if (line.startsWith('n') && pid !== process.pid) result.push({ pid, path: line.slice(1) });
  }
  return result;
}
export function assertExclusive(path: string, owner?: string) {
  const row = registry()[canonical(path)];
  if (!row) throw Error('Legacy or unowned worktree; report only.');
  if (Object.keys(row.leases).some(id => id !== owner)) throw Error('Another session has a lease; worktree kept.');
  const live = liveCwds().filter(p => canonical(p.path) === canonical(path) || inside(p.path, path));
  if (live.length) throw Error('Live process cwd inside worktree; kept.');
  return row;
}
export function lease(path: string, owner: string, release = false) {
  path = canonical(path);
  withWorktree(path, () => mutateRegistry(rows => {
    const row = rows[path];
    if (!row) return; // Never convert legacy work into automatic cleanup candidates.
    if (release) delete row.leases[owner]; else row.leases[owner] = {};
    row.updated = new Date().toISOString();
  }));
}
export function repositories(): string[] {
  const repos = new Set(Object.values(registry()).map(row => row.repo));
  const root = join(homedir(), 'dev/work');
  if (existsSync(root)) for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory() && existsSync(join(path, '.git'))) repos.add(path);
  }
  return [...repos];
}
