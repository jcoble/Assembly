#!/usr/bin/env node
// EdiPlatform's cheap no-new-Python hook, using Assembly's TypeScript tooling rule.
import { execFileSync } from 'node:child_process';
import { hardContract, versionFile, versionIn } from '../dev-remote-identity.ts';
const commit = process.argv[2] === '--commit' ? process.argv[3] : undefined;
const exists = (spec: string) => { try { execFileSync('git', ['cat-file','-e',spec], {stdio:'ignore'}); return true; } catch { return false; } };
if (commit && !exists(commit + ':scripts/hooks/staged-policy.ts') && !exists(commit + '^:scripts/hooks/staged-policy.ts')) process.exit(0);
const args = commit ? ['diff-tree', '--root', '--no-commit-id', '-r', '--name-only', '-z', '--diff-filter=ACMR', commit] : ['diff', '--cached', '--name-only', '-z', '--diff-filter=ACMR'];
const paths = execFileSync('git', args, { encoding: 'utf8' }).split('\0').filter(path => path.endsWith('.py'));
if (paths.length) { process.stderr.write('New or modified Python tooling is blocked; Assembly tooling uses TypeScript.\n' + paths.join('\n') + '\n'); process.exitCode = 1; }

// A change to a hard contract needs the remote to change too, and the version number is how it knows. The rest of the list is
// the author's judgment. Merges carry their parents' numbers.
const versionGuard = 'hardContract(';
const show = (spec: string) => execFileSync('git', ['show', spec], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
const number = (spec: string) => versionIn(show(spec)).split('.').map(Number);
const raised = (now: number[], then: number[]) => { for (let i = 0; i < 3; i++) if (now[i] !== then[i]) return now[i] > then[i]; return false; };
const merging = commit ? false : exists('MERGE_HEAD');
const before = commit ? commit + '^' : 'HEAD', after = commit ? commit : '';
// A commit made before this rule existed, or the first commit of a repository, has nothing to compare.
const applies = !merging && exists(before + ':' + versionFile) && exists(after + ':' + versionFile)
  && (!commit || (exists(before + ':scripts/hooks/staged-policy.ts') && show(before + ':scripts/hooks/staged-policy.ts').includes(versionGuard)));
if (applies) {
  const listed = execFileSync('git', args, { encoding: 'utf8' }).split('\0').filter(hardContract);
  if (listed.length && !raised(number(after + ':' + versionFile), number(before + ':' + versionFile))) {
    process.stderr.write('This commit changes a contract the remote must match, so raise the version number: version under [package] in ' + versionFile + ' (and desktop/Cargo.lock).\n' + listed.join('\n') + '\n');
    process.exitCode = 1;
  }
}
