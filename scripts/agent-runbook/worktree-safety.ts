import { existsSync, lstatSync } from 'node:fs';
import { join } from 'node:path';
import { git, gitOperation, onDefault } from './worktree-state.ts';
import type { WorktreeRecord } from './worktree-state.ts';

const regenerable = new Set(['target', 'node_modules', 'bin', 'obj', 'dist', 'build', '.build', '.next', '.svelte-kit', '__pycache__', '.turbo', '.cache']);
const gitPath = (path: string, name: string) => git(path, 'rev-parse', '--path-format=absolute', '--git-path', name);
export function checkRemovalSafety(path: string, row: WorktreeRecord, requirePushed = true) {
  if (row.flag) throw Error('Worktree flagged: ' + row.flag);
  const operation = gitOperation(path);
  if (operation) throw Error('Git operation in progress: ' + operation);
  const branch = git(path, 'symbolic-ref', '--short', 'HEAD');
  if (branch !== row.branch) throw Error('Worktree branch differs from lease record; kept.');
  if (git(path, 'stash', 'list', '--format=%s').split('\n').some(subject => subject.startsWith('WIP on ' + branch + ':') || subject.startsWith('On ' + branch + ':'))) throw Error('Unpushed stash exists; worktree kept.');
  const ignored = git(path, 'ls-files', '-z', '--others', '--ignored', '--exclude-standard').split('\0').filter(Boolean);
  const unique = ignored.filter(file => file.split('/').at(-1) !== '.DS_Store' && !file.startsWith('gen/schemas/') && !file.split('/').slice(0, -1).some(part => regenerable.has(part)));
  if (unique.length) throw Error('Ignored files need preservation: ' + unique.slice(0, 10).join(', '));
  const head = git(path, 'rev-parse', 'HEAD');
  if (requirePushed && git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/' + branch).split(/\s/)[0] !== head && !onDefault(path)) {
    throw Error('Unpushed commits or remote lacks HEAD; worktree kept.');
  }
}
export function idleHours(path: string, row: WorktreeRecord): number {
  try {
    const times = [Date.parse(row.updated)];
    for (const name of ['HEAD', 'index']) times.push(lstatSync(gitPath(path, name)).mtimeMs);
    // HEAD's reflog also records switches/commits that leave symbolic HEAD unchanged.
    const log = gitPath(path, 'logs/HEAD');
    if (existsSync(log)) times.push(lstatSync(log).mtimeMs);
    const files = git(path, 'ls-files', '-z', '--cached', '--others', '--exclude-standard').split('\0').filter(Boolean);
    for (const file of new Set(files)) times.push(lstatSync(join(path, file)).mtimeMs);
    const newest = times.reduce((latest, time) => Math.max(latest, time), -Infinity);
    if (!Number.isFinite(newest)) throw Error('Invalid activity timestamp.');
    return (Date.now() - newest) / 3600000;
  } catch (error) { throw Error('Cannot measure worktree activity; kept: ' + (error as Error).message); }
}
