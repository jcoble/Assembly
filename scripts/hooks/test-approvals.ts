// The controller's signed test-change approvals (approve-test-change.ts), and where each check finds them.
// An approval is test-approvals/<id>.json {id, branch, tests: [{file, name, sha256} | {file, name, removed: true}], reason,
// approvedBy, at, expires} with <id>.json.sig. It travels with the change: the commit that makes the approved test change
// adds both files under test-approvals/, and an id a commit's parent or the base already holds is used up. Signatures are
// checked against test-approvals/allowed_signers at the base tip, BASE (CI's pull request base) or origin/HEAD, never a
// branch's own copy or an old fork point's. The controller installs new approvals in ~/.local/state/assembly-test-approvals/ for the lane to commit.
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';

export interface ApprovedTest { file: string; name: string; sha256?: string; removed?: boolean; }
export interface Approval { id: string; branch: string; tests: ApprovedTest[]; expires: string; }
interface Signed { id: string; json: string; sig: string; }
export const approvalsFolder = 'test-approvals';
export const hostApprovals = join(homedir(), '.local/state/assembly-test-approvals');

const git = (repo: string, args: string[]) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });
const tryGit = (repo: string, args: string[]) => { try { return git(repo, args); } catch { return undefined; } };
export function currentBranch(repo: string): string { return tryGit(repo, ['rev-parse', '--abbrev-ref', 'HEAD'])?.trim() ?? 'HEAD'; }
const base = () => process.env.BASE || 'origin/HEAD';
// The controller's key as the base tip holds it. Without one, nothing is approved.
export function baseSigners(repo: string): string | undefined { return tryGit(repo, ['show', base() + ':' + approvalsFolder + '/allowed_signers']); }
const held = (repo: string, tree: string) => (tryGit(repo, ['ls-tree', '-r', '--name-only', '-z', tree, '--', approvalsFolder + '/']) ?? '').split('\0').filter(Boolean);
// The approval files a commit adds: in its tree, in none of its parents'.
function addedPaths(repo: string, commit: string): string[] {
  const parents = (tryGit(repo, ['rev-list', '--parents', '-n', '1', commit]) ?? '').trim().split(' ').slice(1);
  const before = new Set(parents.flatMap(parent => held(repo, parent)));
  return held(repo, commit).filter(path => !before.has(path));
}

function verified(signers: string | undefined, candidates: Signed[]): Signed[] {
  if (!signers || !candidates.length) return [];
  const temp = mkdtempSync(join(tmpdir(), 'assembly-approval-check-'));
  try {
    writeFileSync(join(temp, 'allowed_signers'), signers);
    return candidates.filter(({ json, sig }) => {
      try {
        writeFileSync(join(temp, 'sig'), sig);
        execFileSync('ssh-keygen', ['-Y', 'verify', '-f', join(temp, 'allowed_signers'), '-I', 'controller', '-n', 'assembly-test-change', '-s', join(temp, 'sig')], { input: json, stdio: ['pipe', 'ignore', 'ignore'] });
        return true;
      } catch { return false; }
    });
  } finally { rmSync(temp, { recursive: true, force: true }); }
}
function parsed({ id, json }: Signed, branch: string): Approval | undefined {
  try {
    const approval = JSON.parse(json);
    const tests = approval.tests;
    if (approval.id !== id || approval.branch !== branch || branch === 'HEAD' || typeof approval.expires !== 'string') return undefined;
    if (!Array.isArray(tests) || !tests.length || !tests.every(test => typeof test?.file === 'string' && typeof test.name === 'string'
      && (test.removed === true || typeof test.sha256 === 'string'))) return undefined;
    return approval;
  } catch { return undefined; }
}
const accepted = (candidates: Signed[], signers: string | undefined, branch: string, now?: number) => verified(signers, candidates)
  .map(candidate => parsed(candidate, branch)).filter((approval): approval is Approval => Boolean(approval) && (now === undefined || Date.parse(approval!.expires) > now));

// The approvals a commit adds (or, without a commit, the index adds over HEAD), for the commit check. `now` is the commit
// hook's expiry check; CI and pre-push leave it out, since an approval can be added only once. Undefined while the base
// holds no controller key: the check is off and the TEST-CHANGE rule alone applies. Only the base turns it on or off.
export function addedApprovals(repo: string, commit: string | undefined, branch: string, now?: number): Approval[] | undefined {
  const signers = baseSigners(repo);
  if (!signers) return undefined;
  const used = new Set(held(repo, base()));
  const added = commit ? addedPaths(repo, commit) : (tryGit(repo, ['diff', '--cached', '--name-only', '-z', '--diff-filter=A', '--', approvalsFolder]) ?? '').split('\0');
  const tree = commit ? commit + ':' : ':';
  const candidates = added.filter(path => /^test-approvals\/[^/]+\.json$/.test(path) && !used.has(path)).flatMap(path => {
    const json = tryGit(repo, ['show', tree + path]), sig = tryGit(repo, ['show', tree + path + '.sig']);
    return json !== undefined && sig !== undefined ? [{ id: path.slice(approvalsFolder.length + 1, -'.json'.length), json, sig }] : [];
  });
  return accepted(candidates, signers, branch, now);
}
// For the Edit guard: approvals installed on this host or copied into the checkout, not yet used on this branch or the base.
export function pendingApprovals(repo: string, top: string, host: string, now: number): Approval[] {
  const read = (folder: string): Signed[] => {
    let names: string[];
    try { names = readdirSync(folder).filter(name => name.endsWith('.json')); } catch { return []; }
    return names.flatMap(name => {
      try { return [{ id: name.slice(0, -'.json'.length), json: readFileSync(join(folder, name), 'utf8'), sig: readFileSync(join(folder, name + '.sig'), 'utf8') }]; } catch { return []; }
    });
  };
  const used = new Set([...held(repo, 'HEAD'), ...held(repo, base())]);
  const candidates = [...read(host), ...read(join(top, approvalsFolder))].filter(candidate => !used.has(approvalsFolder + '/' + candidate.id + '.json'));
  return accepted(candidates, baseSigners(repo), currentBranch(repo), now);
}
