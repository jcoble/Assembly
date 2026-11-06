#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
export function blocked(command: string, branch: string): string | undefined {
  // Recognize supported command prefixes; never execute the supplied text.
  const anchor = '(?:^|[;&|\\n])\\s*(?:env\\s+)?(?:[A-Za-z_][A-Za-z_0-9]*=\\S+\\s+)*(?:command\\s+)?';
  const gh = 'gh(?:\\s+(?:-R|--repo|--hostname)(?:=|\\s+)\\S+)*\\s+';
  const git = 'git(?:\\s+(?:-C|-c|--git-dir|--work-tree)(?:=|\\s+)\\S+)*\\s+';
  if (new RegExp(anchor + gh + 'api\\b[^;&|\\n]*/pulls/\\d+/merge\\b').test(command)) return 'GitHub REST merge';
  if (new RegExp(anchor + git + 'push\\b[^;&|\\n]*(?:\\s|:)(?:main|master)(?:\\s|$)').test(command)) return 'direct push to the default branch';
  for (const match of command.matchAll(new RegExp(anchor + gh + 'pr\\s+merge\\b[^;&|\\n]*', 'g'))) {
    if (!/(?:^|\s)ALLOW_MERGE=1\s/.test(match[0].slice(0, match[0].search(/\bgh\b/)))) return 'merge without the explicit owner-authorized command';
    if (/\s--(?:auto|squash|rebase)(?:[=\s]|$)/.test(match[0]) || !/\s--merge(?:\s|$)/.test(match[0])) return 'unsupported merge mode';
  }
  const selectedDefault = new RegExp(anchor + git + '(?:switch|checkout)\\s+(?:main|master)\\b').test(command);
  if ((branch === 'main' || branch === 'master' || selectedDefault) && new RegExp(anchor + git + 'merge\\b').test(command)) return 'local merge into the default branch';
  return undefined;
}
// Outside a repository there's no current branch. Refusing there would lock the shell out of every command, even the cd back.
function currentBranch(): string {
  try {
    return execFileSync('git', ['branch', '--show-current'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch {
    return '';
  }
}
if (process.argv[1] && realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url))) {
  let input = ''; for await (const chunk of process.stdin) input += chunk;
  try {
    const payload = JSON.parse(input), command = payload.tool_input?.command ?? payload.tool_input?.cmd ?? '';
    const reason = blocked(command, currentBranch());
    if (reason) { process.stderr.write('Blocked: ' + reason + '. Owner permission is required for a merge.\n'); process.exitCode = 2; }
  } catch (error) { process.stderr.write('Merge guard could not validate input: ' + String(error) + '\n'); process.exitCode = 2; }
}
