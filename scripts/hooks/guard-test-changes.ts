#!/usr/bin/env node
// Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit): EdiPlatform's guard-test-changes.
// Stops an edit that changes or removes an assertion in a test file, at the moment it is made.
// test-integrity.ts is the commit-level check and owns what counts as a test and an assertion.
// A changed test is allowed when the controller signed an approval for it (approve-test-change.ts):
// <id>.json {branch, tests: [{file, name}], reason, approvedBy, at} beside <id>.json.sig, in
// ~/.local/state/assembly-test-approvals/, verified against ~/.config/assembly-test-approvals/allowed_signers.
// ASSEMBLY_TEST_APPROVALS_HOME (tests only) replaces both: approvals in it, signers at <it>/allowed_signers.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, realpathSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, relative } from 'node:path';
import { changedAssertions, isTest } from './test-integrity.ts';

interface Replacement { old_string: string; new_string: string; replace_all?: boolean; }
interface ToolInput extends Partial<Replacement> { file_path?: string; content?: string; edits?: Replacement[]; }

const unix = (text: string): string => text.replaceAll('\r\n', '\n');
function replace(text: string, edit: Replacement): string {
  const from = unix(edit.old_string), to = unix(edit.new_string);
  if (edit.replace_all) return text.split(from).join(to);
  const index = text.indexOf(from);
  return index < 0 ? text : text.slice(0, index) + to + text.slice(index + from.length);
}
function result(before: string, input: ToolInput): string {
  if (input.content !== undefined) return unix(input.content);
  const edits = input.edits ?? [input as Replacement];
  return edits.reduce(replace, before);
}
function repoPath(path: string): string | undefined {
  const real = realpathSync(path);
  try {
    const top = execFileSync('git', ['-C', dirname(real), 'rev-parse', '--show-toplevel'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    return relative(top, real);
  } catch { return undefined; }
}
// A test moved into another changed test file, same name and same assertions, is a split and not a removal.
function changedTestsBesides(path: string): string[] {
  const real = realpathSync(path), run = (args: string[]) => execFileSync("git", ["-C", dirname(real), ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
  const top = run(["rev-parse", "--show-toplevel"]).trim();
  return run(["status", "--porcelain", "-u", "--no-renames"]).split("\n").filter(Boolean).map(line => line.slice(3))
    .filter(name => isTest(name) && existsSync(join(top, name)) && realpathSync(join(top, name)) !== real)
    .map(name => unix(readFileSync(join(top, name), "utf8")));
}
const approvalsHome = process.env.ASSEMBLY_TEST_APPROVALS_HOME;
const approvals = approvalsHome ?? join(homedir(), '.local/state/assembly-test-approvals');
const signers = approvalsHome ? join(approvalsHome, 'allowed_signers') : join(homedir(), '.config/assembly-test-approvals/allowed_signers');
function signed(json: Buffer, signature: string): boolean {
  try {
    execFileSync('ssh-keygen', ['-Y', 'verify', '-f', signers, '-I', 'controller', '-n', 'assembly-test-change', '-s', signature], { input: json, stdio: ['pipe', 'ignore', 'ignore'] });
    return true;
  } catch { return false; }
}
// The test names in this file the controller approved for the current branch. Anything unreadable approves nothing.
function approved(path: string, inRepo: string): Set<string> {
  const names = new Set<string>();
  let files: string[], branch: string;
  try {
    files = readdirSync(approvals).filter(name => name.endsWith('.json'));
    if (!files.length) return names;
    branch = execFileSync('git', ['-C', dirname(realpathSync(path)), 'rev-parse', '--abbrev-ref', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch { return names; }
  for (const name of files) {
    try {
      const file = join(approvals, name), json = readFileSync(file);
      if (!signed(json, file + '.sig')) continue;
      const approval = JSON.parse(json.toString('utf8'));
      if (approval.branch !== branch || !Array.isArray(approval.tests)) continue;
      for (const test of approval.tests) if (test?.file === inRepo && typeof test.name === 'string') names.add(test.name);
    } catch { continue; }
  }
  return names;
}
const movedAway = (before: string, name: string, others: string[]) => others.some(other => !changedAssertions(before, other).includes(name));
function refusal(input: ToolInput): string | undefined {
  const path = input.file_path;
  if (!path || !existsSync(path)) return undefined;
  const inRepo = repoPath(path);
  if (!inRepo || !isTest(inRepo)) return undefined;
  const before = unix(readFileSync(path, 'utf8')), changed = changedAssertions(before, result(before, input));
  if (!changed.length) return undefined;
  const others = changedTestsBesides(path), moved = changed.filter(name => !movedAway(before, name, others));
  if (!moved.length) return undefined;
  const allowed = approved(path, inRepo), names = moved.filter(name => !allowed.has(name));
  if (!names.length) return undefined;
  return 'BLOCKED: this edit changes or removes an assertion in ' + inRepo + ' (' + names.join(', ') + ').\n'
    + 'Tests define expected behavior. Do NOT change tests to match code. If the code has a bug, fix the code.\n'
    + 'If this test is genuinely outdated:\n'
    + '1. Do not change the test. Write the proposed change as a patch in your reports folder.\n'
    + '2. Explain why the expectation is wrong, and ask your controller (the session that started you), not Jesse.\n'
    + '3. Your controller approves by signing an approval for this branch and these tests. Never write or sign an approval yourself.\n'
    + '4. When it is approved, make the edit again, and name the test in a TEST-CHANGE line when you commit.\n';
}

let text = '';
for await (const chunk of process.stdin) text += chunk;
try {
  const message = refusal(JSON.parse(text).tool_input ?? {});
  if (message) { process.stderr.write(message); process.exitCode = 2; }
} catch (error) {
  process.stderr.write('guard-test-changes: refusing, could not check this edit: ' + String(error) + '\n');
  process.exitCode = 2;
}
