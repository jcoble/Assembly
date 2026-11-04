import { lstatSync } from 'node:fs';
import { basename, join } from 'node:path';
import { git, tryGit, defaultBranch, assertExclusive, withWorktree, mutateRegistry } from './worktree-state.ts';
import { canonical, inside } from './guard.ts';

export type BackupResult = { status: 'pushed' | 'clean' | 'skipped' | 'failed'; reason?: string; kept: string[] };
const forbidden = /(?:^|\/)(?:\.env[^/]*|id_rsa[^/]*|[^/]*credentials[^/]*|[^/]*\.(?:pem|key|db|sqlite[^/]*))$/i;
function containsSecret(diff: string) {
  if (/^GIT binary patch$/m.test(diff)) throw Error('Secret scanner cannot inspect an opaque binary diff; no commit or push.');
  const patterns = [
    /-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----/,
    /\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\b/,
    /(?:api[_-]?key|secret|password|(?:access[_-]?|auth[_-]?)?token)\s*[=:]\s*["']?[^\s"']{12,}/i,
  ];
  return patterns.some(pattern => pattern.test(diff));
}
// The caller owns the worktree lock. Verification scans a clean tree without pushing.
export function backupUnderLock(path: string, owner?: string, verifyOnly = false): BackupResult {
  let phase = 'preflight';
  try {
    if (!inside(path)) return { status: 'skipped', reason: 'Outside managed worktree root.', kept: [] };
    assertExclusive(path, owner);
    if (git(path, 'rev-parse', '--is-bare-repository') !== 'false') throw Error('Bare repository; kept.');
    const main = git(path, 'worktree', 'list', '--porcelain').split('\n')[0].slice(9);
    if (canonical(main) === path) return { status: 'skipped', reason: 'Main checkout; kept.', kept: [] };
    const defaultName = defaultBranch(path);
    let branch = tryGit(path, 'symbolic-ref', '--short', 'HEAD');
    if (branch === defaultName) return { status: 'skipped', reason: 'Default branch; kept.', kept: [] };
    if (!tryGit(path, 'remote', 'get-url', 'origin')) return { status: 'skipped', reason: 'No origin remote; kept.', kept: [] };
    // Tracked files include the existing index; untracked files exclude gitignored paths.
    const files = git(path, 'ls-files', '-z', '--cached', '--others', '--exclude-standard').split('\0').filter(Boolean);
    const kept = [...new Set(files)].filter(file => {
      if (forbidden.test(file)) return true;
      try { return lstatSync(join(path, file)).size > 20 * 1024 * 1024; } catch (error: any) { if (error.code === 'ENOENT') return false; throw error; }
    });
    if (kept.length) return { status: 'skipped', reason: 'Sensitive filename or large file; nothing committed or pushed.', kept };
    git(path, 'add', '-A', '--', '.');
    phase = 'scan';
    if (containsSecret(git(path, 'diff', '--cached', '--no-ext-diff', '--no-textconv', '--binary', '--'))) return { status: 'skipped', reason: 'Secret scanner found a possible credential; no commit or push.', kept: [] };
    // Existing unpushed commits must pass the same scan, including clean trees.
    const remote = 'refs/remotes/origin/' + branch;
    const base = tryGit(path, 'rev-parse', '--verify', remote) ? remote : 'refs/remotes/origin/' + defaultName;
    const history = git(path, 'log', '--format=', '-p', '--binary', '--no-ext-diff', '--no-textconv', base + '..HEAD');
    // Reuse the same bounded signature scan on history without executing any diff content.
    if (containsSecret(history)) return { status: 'skipped', reason: 'Secret scanner found a credential in unpushed history.', kept: [] };
    if (verifyOnly) return { status: 'clean', kept: [] };
    phase = 'commit';
    if (!branch) { branch = 'backup/' + basename(path); git(path, 'switch', '-c', branch); }
    if (tryGit(path, 'status', '--porcelain')) {
      const taskNumber = /^tsk-(\d+)/.exec(branch)?.[1];
      const taskPattern = taskNumber ? new RegExp('^Task: TSK-' + taskNumber + ' https://app[.]notion[.]com/p/[0-9a-fA-F]{32}([?].*)?$') : undefined;
      const taskLine = taskPattern ? git(path, 'log', '--format=%B', defaultName + '..HEAD').split('\n').find(line => taskPattern.test(line)) : undefined;
      const task = taskLine ? taskLine + '\n' : '';
      git(path, 'commit', '-m', 'chore: back up agent worktree\n\n' + task + 'Architecture: unchanged\nCommitted-by: agent-worktree');
    }
    phase = 'push';
    git(path, 'push', '-u', 'origin', 'HEAD:refs/heads/' + branch);
    const head = git(path, 'rev-parse', 'HEAD');
    if (git(path, 'ls-remote', '--heads', 'origin', 'refs/heads/' + branch).split(/\s/)[0] !== head) throw Error('Remote did not confirm backup HEAD.');
    mutateRegistry(rows => { rows[path].backedUpHead = head; rows[path].branch = branch; rows[path].updated = new Date().toISOString(); delete rows[path].flag; });
    return { status: 'pushed', kept: [] };
  } catch (error) {
    const reason = (phase === 'scan' ? 'Secret scanner failure: ' : '') + (error as Error).message;
    return { status: phase === 'preflight' ? 'skipped' : 'failed', reason, kept: [] };
  }
}
export function backupWorktree(path: string, owner?: string): BackupResult {
  path = canonical(path);
  try {
    return withWorktree(path, () => {
      const result = backupUnderLock(path, owner);
      if (result.status === 'skipped' || result.status === 'failed') mutateRegistry(rows => { if (rows[path]) rows[path].flag = result.reason; });
      return result;
    });
  } catch (error) { return { status: 'failed', reason: (error as Error).message, kept: [] }; }
}
