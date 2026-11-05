#!/usr/bin/env node
// Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit): EdiPlatform's guard-test-changes.
// Stops an edit that changes or removes an assertion in a test file, at the moment it is made.
// test-integrity.ts is the commit-level check and owns what counts as a test and an assertion.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, relative } from 'node:path';
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
function refusal(input: ToolInput): string | undefined {
  const path = input.file_path;
  if (!path || !existsSync(path)) return undefined;
  const inRepo = repoPath(path);
  if (!inRepo || !isTest(inRepo)) return undefined;
  const before = unix(readFileSync(path, 'utf8')), names = changedAssertions(before, result(before, input));
  if (!names.length) return undefined;
  return 'BLOCKED: this edit changes or removes an assertion in ' + inRepo + ' (' + names.join(', ') + ').\n'
    + 'Tests define expected behavior. Do NOT change tests to match code. If the code has a bug, fix the code.\n'
    + 'If this test is genuinely outdated:\n'
    + '1. Explain to Jesse why the test expectation is wrong.\n'
    + '2. Get his explicit approval before changing it.\n'
    + '3. Name the test in a TEST-CHANGE line when you commit.\n';
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
