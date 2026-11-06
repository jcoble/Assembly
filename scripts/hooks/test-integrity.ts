#!/usr/bin/env node
// EdiPlatform's TEST-CHANGE discipline, adapted to Assembly's C#, Rust, TS and Dart.
// A reason is an audit trail, never approval to change behavior or proof of correctness.
// Approval to change an assertion is the controller's signed approval (approve-test-change.ts) of the exact test text:
// <id>.json {id, branch, tests: [{file, name, sha256} | {file, name, removed: true}], reason, approvedBy, at, expires}
// beside <id>.json.sig in ~/.local/state/assembly-test-approvals/, verified against
// ~/.config/assembly-test-approvals/allowed_signers. ASSEMBLY_TEST_APPROVALS_HOME, when set, replaces both: approvals
// in it, signers at <it>/allowed_signers. A commit that holds an approval's text moves it to used/ (--used, post-commit).
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readdirSync, readFileSync, renameSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, join } from 'node:path';
import { pathToFileURL } from 'node:url';

export interface Change { path: string; previousPath?: string; before: string; after: string; }
export const isTest = (path: string): boolean => /(?:^|\/)(?:[^/]*[Tt]ests?|[Ff]ixtures?|testdata)(?:\/|\.)|(?:[._](?:test|spec)|_tests?)\.(?:cs|rs|ts|tsx|dart|sh)$|\.expected\.json$/.test(path);
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
// A test's own text, from the line that names it through its closing bracket, without comments or whitespace outside
// literals and without a comma before a closing bracket, so a formatter's wrap of the same code reads the same.
function normalized(text: string): string {
  let out = '', quote = '', escaped = false;
  for (const c of text) {
    if (quote) {
      out += c;
      if (escaped) escaped = false; else if (c === '\\') escaped = true; else if (c === quote) quote = '';
      continue;
    }
    if (/\s/.test(c)) continue;
    if (')]}'.includes(c) && out.endsWith(',')) out = out.slice(0, -1);
    if (c === '"' || c === "'" || c === '`') quote = c;
    out += c;
  }
  return out;
}
export function testText(source: string, name: string): string | undefined {
  const lines = executable(source.replaceAll('\r\n', '\n')).split('\n'), found: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    if (method(lines[i]) !== name) continue;
    let body = lines[i];
    while (!(balance(body) <= 0 && (body.includes('{') || /;\s*$/.test(body))) && i + 1 < lines.length && method(lines[i + 1]) === undefined) body += '\n' + lines[++i];
    found.push(normalized(body));
  }
  return found.length ? found.join('\n') : undefined;
}
export function testHash(source: string, name: string): string | undefined {
  const text = testText(source, name);
  return text === undefined ? undefined : createHash('sha256').update(text).digest('hex');
}

export interface ApprovedTest { file: string; name: string; sha256?: string; removed?: boolean; }
export interface Approval { id: string; branch: string; tests: ApprovedTest[]; expires: string; path?: string; }
const approvalsHome = process.env.ASSEMBLY_TEST_APPROVALS_HOME;
const approvalsFolder = approvalsHome ?? join(homedir(), '.local/state/assembly-test-approvals');
const signers = approvalsHome ? join(approvalsHome, 'allowed_signers') : join(homedir(), '.config/assembly-test-approvals/allowed_signers');
function signed(json: Buffer, signature: string): boolean {
  try {
    execFileSync('ssh-keygen', ['-Y', 'verify', '-f', signers, '-I', 'controller', '-n', 'assembly-test-change', '-s', signature], { input: json, stdio: ['pipe', 'ignore', 'ignore'] });
    return true;
  } catch { return false; }
}
export function currentBranch(cwd?: string): string {
  try { return execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch { return 'HEAD'; }
}
// The controller's signed approvals for this branch, unexpired at `at`; with `used`, also those a commit already used.
// A detached checkout (HEAD) gets none. Anything unreadable approves nothing.
export function approvals(branch: string, at = Date.now(), used = false): Approval[] {
  const result: Approval[] = [];
  if (!branch || branch === 'HEAD') return result;
  for (const folder of used ? [approvalsFolder, join(approvalsFolder, 'used')] : [approvalsFolder]) {
    let names: string[];
    try { names = readdirSync(folder).filter(name => name.endsWith('.json')); } catch { continue; }
    for (const name of names) {
      try {
        const path = join(folder, name), json = readFileSync(path);
        if (!signed(json, path + '.sig')) continue;
        const approval = JSON.parse(json.toString('utf8'));
        if (approval.id !== name.slice(0, -'.json'.length) || approval.branch !== branch || !(Date.parse(approval.expires) > at)) continue;
        const tests = approval.tests;
        if (!Array.isArray(tests) || !tests.length || !tests.every(test => typeof test?.file === 'string' && typeof test.name === 'string'
          && (test.removed === true || typeof test.sha256 === 'string'))) continue;
        result.push({ ...approval, path });
      } catch { continue; }
    }
  }
  return result;
}
// Every entry the approval has for this file holds at once in the file's text after the change.
export function satisfies(approval: Approval, file: string, after: string): boolean {
  const entries = approval.tests.filter(test => test.file === file);
  return entries.length > 0 && entries.every(test => test.removed ? testText(after, test.name) === undefined : testHash(after, test.name) === test.sha256);
}

// The tests whose assertions are not all still there, unchanged, in the same test.
export function changedAssertions(before: string, after: string): string[] {
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
// With approvals (on a host that holds them), a changed assertion also needs one that matches the committed text, cited by id.
export function violations(changes: Change[], message: string, approved?: Approval[]): string[] {
  const errors: string[] = [], tests = changes.filter(change => (isTest(change.path) || Boolean(change.previousPath && isTest(change.previousPath))));
  const production = changes.some(change => !isTest(change.path) && !/^(?:docs\/|evidence\/|scripts\/|\.githooks\/|\.github\/|\.claude\/|\.codex\/)|\.(?:md|mdx)$/.test(change.path));
  const tokens = message.split('\n').map(line => line.trim());
  if (tests.length && !production && !tokens.some(line => /^TEST-CHANGE:\s*\S/.test(line))) errors.push('Test-only changes require TEST-CHANGE: <reason>.');
  for (const change of tests) {
    for (const name of changedAssertions(change.before, change.after)) {
      const prefix = 'TEST-CHANGE: ' + name + ':';
      if (!tokens.some(line => line.startsWith(prefix) && line.slice(prefix.length).trim())) errors.push(change.path + ': changed/removed assertion in ' + name + '; add ' + prefix + ' <reason and governing contract>.');
      else if (approved && !approved.some(approval => approval.tests.some(test => test.file === change.path && test.name === name)
        && satisfies(approval, change.path, change.after) && tokens.some(line => line.startsWith(prefix) && line.includes(approval.id)))) {
        errors.push(change.path + ': ' + name + ' needs a signed, unused, unexpired controller approval for this branch that matches the committed test text, cited by its id in the ' + prefix + ' line.');
      }
    }
  }
  return errors;
}
function git(args: string[]): string { return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] }); }
function exists(spec: string): boolean { try { git(['cat-file', '-e', spec]); return true; } catch { return false; } }
function content(spec: string): string { return exists(spec) ? git(['show', spec]) : ''; }
export function stagedChanges(commit?: string): Change[] {
  const args = commit ? ['diff-tree', '--root', '--no-commit-id', '-r', '--name-status', '-z', '-M', commit] : ['diff', '--cached', '--name-status', '-z', '-M'];
  const parts = git(args).split('\0'), result: Change[] = [];
  const parent = commit && exists(commit + '^') ? commit + '^' : undefined;
  for (let i = 0; i < parts.length && parts[i];) {
    const status = parts[i++], oldPath = parts[i++], path = /^[RC]/.test(status) ? parts[i++] : oldPath;
    const before = status === 'A' || (commit && !parent) ? '' : content((commit ? parent + ':' : 'HEAD:') + oldPath);
    const after = status === 'D' ? '' : content((commit ? commit + ':' : ':') + path);
    result.push({ path, previousPath: oldPath, before, after });
  }
  return result;
}
// post-commit: an approval whose whole approved text this commit holds, in a file it changed, is used and moves to used/.
function markUsed(): number {
  const changed = git(['diff-tree', '--root', '--no-commit-id', '-r', '--name-only', '-z', 'HEAD']).split('\0').filter(Boolean);
  for (const approval of approvals(currentBranch())) {
    const files = [...new Set(approval.tests.map(test => test.file))];
    if (!files.some(file => changed.includes(file)) || !files.every(file => satisfies(approval, file, content('HEAD:' + file)))) continue;
    const used = join(approvalsFolder, 'used'), path = approval.path!;
    mkdirSync(used, { recursive: true });
    renameSync(path + '.sig', join(used, basename(path) + '.sig'));
    renameSync(path, join(used, basename(path)));
    process.stdout.write('Test approval ' + approval.id + ' is used by this commit; moved to ' + used + '\n');
  }
  return 0;
}
// The approval check activates prospectively, from the commit after the one that adds it, and not in CI, which has no approvals.
const approvalCheck = 'approvals(currentBranch(), ';
function approving(base: string): boolean {
  return process.env.GITHUB_ACTIONS !== 'true' && exists(base + ':scripts/hooks/test-integrity.ts') && content(base + ':scripts/hooks/test-integrity.ts').includes(approvalCheck);
}
export function main(args: string[]): number {
  if (args[0] === '--used') return markUsed();
  const commit = args[0] === '--commit' ? args[1] : undefined;
  // Activate prospectively. Removing the policy after activation cannot deactivate it.
  if (commit ? !exists(commit + ':scripts/hooks/test-integrity.ts') && !exists(commit + '^:scripts/hooks/test-integrity.ts') : !exists('HEAD:scripts/hooks/test-integrity.ts') && !exists(':scripts/hooks/test-integrity.ts')) return 0;
  const message = commit ? git(['log', '-1', '--format=%B', commit]) : readFileSync(args[0], 'utf8');
  // A pushed commit may already have used its approval, so --commit also accepts used ones, valid at its author date.
  const approved = !approving(commit ? commit + '^' : 'HEAD') ? undefined
    : commit ? approvals(currentBranch(), Date.parse(git(['log', '-1', '--format=%aI', commit]).trim()), true) : approvals(currentBranch(), Date.now());
  const errors = violations(stagedChanges(commit), message, approved);
  if (errors.length) { process.stderr.write('Test integrity: commit stopped.\n' + errors.join('\n') + '\nInvestigate; fix incorrect production code. TEST-CHANGE is not permission to change behavior.\n'); return 1; }
  return 0;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) { process.stderr.write('Test integrity: ' + String(error) + '\n'); process.exitCode = 1; }
}
