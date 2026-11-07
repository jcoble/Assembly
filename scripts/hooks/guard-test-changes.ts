#!/usr/bin/env node
// Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit): EdiPlatform's guard-test-changes.
// It stops an agent that changes or removes an assertion in a test file, so it thinks or asks; it never touches git.
// Allowed: an edit that leaves every assertion there unchanged (so any pure addition), and a test the controller OKed.
// Unlike EdiPlatform, new text containing the old verbatim is not enough on its own: `// assert...` contains `assert...`.
// The controller's OK is a plain note, any <name>.json in
// ~/.local/state/assembly-test-approvals/ (ASSEMBLY_TEST_APPROVALS_HOME when set):
//   {"branch": "<branch>", "tests": [{"file": "<repo-relative path>", "name": "<test name>"}], "reason": "<why>"}
// A detached checkout (branch HEAD) gets no approvals.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, realpathSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, relative } from 'node:path';

const isTest = (path: string): boolean => /(?:^|\/)(?:[^/]*[Tt]ests?|[Ff]ixtures?|testdata)(?:\/|\.)|(?:[._](?:test|spec)|_tests?)\.(?:cs|rs|ts|tsx|dart|sh)$|\.expected\.json$/.test(path);
const assertion = /\bAssert\.\w+(?:<[^>]+>)?\s*\(|\.Should\(\)\s*\.|\b(?:debug_)?assert(?:_eq|_ne|_matches)?!\s*\(|\bassert(?:\.\w+)?\s*\(|\bexpect\s*\(/;

function method(line: string): string | undefined {
  return line.match(/\b(?:public|private|internal)\s+(?:(?:static|async)\s+)*[\w<>?,.\[\]]+\s+(\w+)\s*\(/)?.[1]
    ?? line.match(/\b(?:async\s+)?fn\s+(\w+)\s*\(/)?.[1]
    ?? line.match(/\b(?:it|test)(?:\.[a-zA-Z]+)*\s*\(\s*(['"`])((?:\\.|(?!\1).)*)\1/)?.[2];
}

// Balance complete assertion expressions, including multiline expected values.
// Preserve whitespace inside literals; changing an expected string must be detected.
function balance(text: string): number {
  let depth = 0, quote = '', escaped = false, comment = false, block = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i], next = text[i + 1];
    if (comment) { if (c === '\n') comment = false; continue; }
    if (block) { if (c === '*' && next === '/') { block = false; i++; } continue; }
    if (quote) {
      if (escaped) escaped = false;
      else if (c === '\\') escaped = true;
      else if (c === quote) quote = '';
    } else if (c === '/' && next === '/') { comment = true; i++; }
    else if (c === '/' && next === '*') { block = true; i++; }
    else if (c === '"' || c === "'" || c === '`') quote = c;
    else if ('([{'.includes(c)) depth++;
    else if (')]}'.includes(c)) depth--;
  }
  return depth;
}
// Remove comments across lines while retaining literal bytes and line boundaries.
function executable(source: string): string {
  let result = '', quote = '', escaped = false, line = false, block = false;
  for (let i = 0; i < source.length; i++) {
    const c = source[i], next = source[i + 1];
    if (line) { if (c === '\n') { line = false; result += c; } else result += ' '; continue; }
    if (block) {
      if (c === '*' && next === '/') { block = false; result += '  '; i++; }
      else result += c === '\n' ? c : ' ';
      continue;
    }
    if (quote) {
      result += c;
      if (escaped) escaped = false;
      else if (c === '\\') escaped = true;
      else if (c === quote) quote = '';
    } else if (c === '/' && next === '/') { line = true; result += '  '; i++; }
    else if (c === '/' && next === '*') { block = true; result += '  '; i++; }
    else { result += c; if (c === '"' || c === "'" || c === '`') quote = c; }
  }
  return result;
}
function assertions(source: string): Map<string, string[]> {
  const result = new Map<string, string[]>(), lines = executable(source).split('\n');
  let owner = '<file>';
  for (let i = 0; i < lines.length; i++) {
    owner = method(lines[i]) ?? owner;
    if (!assertion.test(lines[i]) || /^\s*(?:\/\/|\*)/.test(lines[i])) continue;
    let body = lines[i];
    while (i + 1 < lines.length && (balance(body) > 0 || /^\s*\./.test(lines[i + 1]))) body += '\n' + lines[++i];
    const values = result.get(owner) ?? [];
    values.push(body.trim()); result.set(owner, values);
  }
  return result;
}
// The tests whose assertions are not all still there, unchanged, in the same test.
function changedAssertions(before: string, after: string): string[] {
  const names: string[] = [], kept = assertions(after);
  for (const [name, values] of assertions(before)) {
    const remaining = [...(kept.get(name) ?? [])];
    let changed = false;
    for (const value of values) {
      const index = remaining.indexOf(value);
      if (index < 0) changed = true; else remaining.splice(index, 1);
    }
    if (changed) names.push(name);
  }
  return names;
}
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
const approvals = process.env.ASSEMBLY_TEST_APPROVALS_HOME ?? join(homedir(), '.local/state/assembly-test-approvals');
// The test names in this file the controller OKed for the current branch. Anything unreadable approves nothing.
function approved(path: string, inRepo: string): Set<string> {
  const names = new Set<string>();
  let files: string[], branch: string;
  try {
    files = readdirSync(approvals).filter(name => name.endsWith('.json'));
    if (!files.length) return names;
    branch = execFileSync('git', ['-C', dirname(realpathSync(path)), 'rev-parse', '--abbrev-ref', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch { return names; }
  if (branch === 'HEAD') return names;
  for (const name of files) {
    try {
      const approval = JSON.parse(readFileSync(join(approvals, name), 'utf8'));
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
    + 'Tests define expected behaviour. If the code is the bug, fix the code, not the test.\n'
    + 'If the test itself is wrong:\n'
    + '1. Explain to your controller (the session that started you) why the test is wrong. The controller OKs test\n'
    + '   changes; only a test change that changes product behaviour goes to Jesse.\n'
    + '2. The controller records the OK as an approval note for this branch, file and test. Never write one yourself.\n'
    + '3. Then make the edit again.\n';
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
