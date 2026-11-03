#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
export interface Update { local: string; remote: string; remoteRef: string; }
export function updates(input: string): Update[] {
  return input.split('\n').filter(line => line.trim()).map(line => {
    const fields = line.trim().split(/\s+/);
    if (fields.length !== 4 || !/^[0-9a-f]{40,64}$/.test(fields[1]) || !/^[0-9a-f]{40,64}$/.test(fields[3])) throw new Error('Invalid push reference input');
    if (/^refs\/heads\/(main|master)$/.test(fields[2])) throw new Error('Direct default-branch pushes are blocked. Use the owner-approved pull request.');
    return { local: fields[1], remote: fields[3], remoteRef: fields[2] };
  });
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  let input = ''; for await (const chunk of process.stdin) input += chunk;
  try {
    const commits = new Set<string>();
    const git = (args: string[]) => execFileSync('git', args, { encoding: 'utf8' }).trim();
    const branch = git(['branch', '--show-current']);
    for (const update of updates(input)) {
      if (/^0+$/.test(update.local) || !update.remoteRef.startsWith('refs/heads/')) continue;
      const base = /^0+$/.test(update.remote) ? git(['merge-base', update.local, 'refs/remotes/origin/main']) : update.remote;
      for (const commit of git(['rev-list', '--no-merges', base + '..' + update.local]).split('\n').filter(Boolean)) commits.add(commit);
    }
    for (const commit of commits) execFileSync('bash', ['scripts/check-commit-msg.sh', '--commit', commit, branch], { stdio: 'inherit' });
    process.stdout.write('Pre-push: outgoing commit messages, architecture declarations and test integrity passed; no build or test suite was started.\n');
  } catch (error) { process.stderr.write('Pre-push stopped: ' + String(error) + '\n'); process.exitCode = 1; }
}
