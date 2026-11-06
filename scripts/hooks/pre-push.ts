#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
export interface Update { local: string; remote: string; remoteRef: string; }
export function updates(input: string): Update[] {
  return input.split('\n').filter(line => line.trim()).map(line => {
    const fields = line.trim().split(/\s+/);
    if (fields.length !== 4 || !/^[0-9a-f]{40,64}$/.test(fields[1]) || !/^[0-9a-f]{40,64}$/.test(fields[3])) throw new Error('Invalid push reference input');
    if (/^refs\/heads\/(main|master)$/.test(fields[2])) throw new Error('Direct default-branch pushes are blocked. Use the owner-approved pull request.');
    return { local: fields[1], remote: fields[3], remoteRef: fields[2] };
  });
}
export type Git = (args: string[]) => string;
// The commits this update puts on origin: a commit any origin branch already has isn't outgoing, so a new branch built
// on another unmerged branch answers only for its own commits. A pull request's commits are still checked by CI.
export function outgoing(update: Update, git: Git): string[] {
  if (/^0+$/.test(update.local) || !update.remoteRef.startsWith('refs/heads/')) return [];
  const replaced = /^0+$/.test(update.remote) ? [] : ['^' + update.remote];
  return git(['rev-list', '--no-merges', update.local, ...replaced, '--not', '--remotes=origin']).split('\n').filter(Boolean);
}
if (process.argv[1] && realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url))) {
  let input = ''; for await (const chunk of process.stdin) input += chunk;
  try {
    const commits = new Set<string>();
    const git: Git = args => execFileSync('git', args, { encoding: 'utf8' }).trim();
    const branch = git(['branch', '--show-current']);
    for (const update of updates(input)) for (const commit of outgoing(update, git)) commits.add(commit);
    for (const commit of commits) execFileSync('bash', ['scripts/check-commit-msg.sh', '--commit', commit, branch], { stdio: 'inherit' });
    process.stdout.write('Pre-push: outgoing commit messages, architecture declarations and test integrity passed; no build or test suite was started.\n');
  } catch (error) { process.stderr.write('Pre-push stopped: ' + String(error) + '\n'); process.exitCode = 1; }
}
