#!/usr/bin/env node
// EdiPlatform's TEST-CHANGE discipline, adapted to Assembly's C#, Rust, TS and Dart.
// A reason is an audit trail, never approval to change behavior or proof of correctness.
// Changing an assertion also needs the controller's signed approval of the exact test text, committed with the change
// under test-approvals/ (test-approvals.ts). CI enforces it; the commit hook and pre-push warn early.
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { addedApprovals, approvalsFolder, baseRef, currentBranch, hostApprovals, type Approval } from './test-approvals.ts';

export interface Change { path: string; previousPath?: string; before: string; after: string; }
export const isTest = (path: string): boolean => /(?:^|\/)(?:[^/]*[Tt]ests?|[Ff]ixtures?|testdata)(?:\/|\.)|(?:[._](?:test|spec)|_tests?)\.(?:cs|rs|ts|tsx|dart|sh)$|\.expected\.json$/.test(path);
const assertion = /\bAssert\.\w+(?:<[^>]+>)?\s*\(|\.Should\(\)\s*\.|\b(?:debug_)?assert(?:_eq|_ne|_matches)?!\s*\(|\bassert(?:\.\w+)?\s*\(|\bexpect\s*\(/;

function method(line: string): string | undefined {
  return line.match(/\b(?:public|private|internal)\s+(?:(?:static|async)\s+)*[\w<>?,.\[\]]+\s+(\w+)\s*\(/)?.[1]
    ?? line.match(/\b(?:async\s+)?fn\s+(\w+)\s*\(/)?.[1]
    ?? line.match(/\b(?:it|test)(?:\.[a-zA-Z]+)*\s*\(\s*(['"`])((?:\\.|(?!\1).)*)\1/)?.[2];
}

// Marks each character as code (c), inside a literal (q) or a comment (m). A Rust lifetime ('a after & or <, 'static) is
// code, and a C# verbatim string (@"...") has no backslash escapes and doubles its quotes.
const lifetime = (text: string, i: number) => /^'[A-Za-z_]\w*(?![\w'])/.test(text.slice(i, i + 80)) && (text[i - 1] === '&' || text[i - 1] === '<' || text.startsWith("'static", i));
function scan(text: string): string {
  let kinds = '', quote = '', verbatim = false, escaped = false, line = false, block = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i], next = text[i + 1];
    if (line) { if (c === '\n') { line = false; kinds += 'c'; } else kinds += 'm'; continue; }
    if (block) {
      if (c === '*' && next === '/') { block = false; kinds += 'mm'; i++; } else kinds += c === '\n' ? 'c' : 'm';
      continue;
    }
    if (quote) {
      kinds += 'q';
      if (escaped) escaped = false;
      else if (c === '\\' && !verbatim) escaped = true;
      else if (c === quote && verbatim && next === '"') { kinds += 'q'; i++; }
      else if (c === quote) { quote = ''; verbatim = false; }
      continue;
    }
    if (c === '/' && (next === '/' || next === '*')) { if (next === '/') line = true; else block = true; kinds += 'mm'; i++; continue; }
    if (c === '"' || c === '`' || (c === "'" && !lifetime(text, i))) {
      quote = c; verbatim = c === '"' && (text[i - 1] === '@' || (text[i - 1] === '$' && text[i - 2] === '@')); kinds += 'q';
      continue;
    }
    kinds += 'c';
  }
  return kinds;
}
// Balance complete assertion expressions, including multiline expected values.
function balance(text: string): number {
  const kinds = scan(text);
  let depth = 0;
  for (let i = 0; i < text.length; i++) if (kinds[i] === 'c') { if ('([{'.includes(text[i])) depth++; else if (')]}'.includes(text[i])) depth--; }
  return depth;
}
// Remove comments across lines while retaining literal bytes and line boundaries.
function executable(source: string): string {
  const kinds = scan(source);
  let result = '';
  for (let i = 0; i < source.length; i++) result += kinds[i] === 'm' ? ' ' : source[i];
  return result;
}
// Each test runs from the line that names it through its closing bracket. Code outside any test belongs to its top-level
// block (named by that block's first line), so every assertion is credited to a span that its hash covers.
interface Span { name: string; start: number; end: number; }
function spans(source: string): { lines: string[]; tests: Span[]; blocks: Span[] } {
  const text = executable(source), lines = text.split('\n'), tests: Span[] = [], blocks: Span[] = [];
  for (let i = 0; i < lines.length; i++) {
    const name = method(lines[i]);
    if (name === undefined) continue;
    let end = i, body = lines[i];
    while (!(balance(body) <= 0 && (body.includes('{') || /;\s*$/.test(body))) && end + 1 < lines.length && method(lines[end + 1]) === undefined) body += '\n' + lines[++end];
    tests.push({ name, start: i, end });
  }
  const kinds = scan(text);
  let depth = 0, line = 0, start = -1;
  for (let i = 0; i <= text.length; i++) {
    if (i < text.length && text[i] !== '\n') {
      if (start < 0 && depth <= 0 && /\S/.test(text[i])) start = line;
      if (kinds[i] === 'c') { if ('([{'.includes(text[i])) depth++; else if (')]}'.includes(text[i])) depth--; }
      continue;
    }
    if (start >= 0 && (depth <= 0 || i === text.length)) { blocks.push({ name: lines[start].trim(), start, end: line }); start = -1; }
    line++;
  }
  return { lines, tests, blocks };
}
function owner(at: number, tests: Span[], blocks: Span[]): string {
  const inside = (span: Span) => span.start <= at && at <= span.end;
  return tests.filter(inside).sort((a, b) => b.start - a.start)[0]?.name ?? blocks.find(inside)?.name ?? '<file>';
}
function assertions(source: string): Map<string, string[]> {
  const result = new Map<string, string[]>(), { lines, tests, blocks } = spans(source);
  for (let i = 0; i < lines.length; i++) {
    if (!assertion.test(lines[i]) || /^\s*(?:\/\/|\*)/.test(lines[i])) continue;
    const name = owner(i, tests, blocks);
    let body = lines[i];
    while (i + 1 < lines.length && (balance(body) > 0 || /^\s*\./.test(lines[i + 1]))) body += '\n' + lines[++i];
    const values = result.get(name) ?? [];
    values.push(body.trim()); result.set(name, values);
  }
  return result;
}
// A test's exact text (every span with that name), with only CRLF and trailing whitespace on each line set aside.
export function testText(source: string, name: string): string | undefined {
  const unix = source.replaceAll('\r\n', '\n'), original = unix.split('\n'), { tests, blocks } = spans(unix);
  const chosen = tests.some(span => span.name === name) ? tests.filter(span => span.name === name) : blocks.filter(span => span.name === name);
  if (!chosen.length && name !== '<file>') return undefined;
  const text = (span: Span) => original.slice(span.start, span.end + 1).map(line => line.replace(/[ \t]+$/, '')).join('\n');
  return chosen.length ? chosen.map(text).join('\n') : text({ name, start: 0, end: original.length - 1 });
}
export function testHash(source: string, name: string): string | undefined {
  const text = testText(source, name);
  return text === undefined ? undefined : createHash('sha256').update(text).digest('hex');
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
// With approvals (once the check is active), a changed assertion also needs an approval the commit adds that matches the
// committed text, cited by id, and committed approvals can't be changed or removed.
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
        errors.push(change.path + ': ' + name + ' needs the controller\'s approval in this commit: add ' + approvalsFolder + '/<id>.json and <id>.json.sig (from '
          + hostApprovals + '), signed by the key in the base\'s ' + approvalsFolder + '/allowed_signers, for this branch, unexpired, matching the committed test text, and cite the id in the ' + prefix + ' line.');
      }
    }
  }
  if (approved) for (const change of changes) {
    const paths = [change.path, change.previousPath ?? change.path];
    if (change.before && paths.some(path => path.startsWith(approvalsFolder + '/') && path !== approvalsFolder + '/allowed_signers')) errors.push(change.path + ': approvals are only ever added; this commit changes or removes ' + paths.join(' / ') + '.');
  }
  return errors;
}
function git(args: string[]): string { return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] }); }
function exists(spec: string): boolean { try { git(['cat-file', '-e', spec]); return true; } catch { return false; } }
function content(spec: string): string { return exists(spec) ? git(['show', spec]) : ''; }
// git's own merge of two parents: its tree, and the files it conflicts on. Undefined for an octopus merge.
function autoMerge(parents: string[]): { tree: string; conflicts: Set<string> } | undefined {
  if (parents.length !== 2) return undefined;
  const result = spawnSync('git', ['merge-tree', '--write-tree', '--name-only', '--no-messages', '-z', ...parents], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  if (result.status !== 0 && result.status !== 1) throw new Error('git merge-tree failed: ' + result.stderr);
  const [tree, ...conflicts] = result.stdout.split('\0').filter(Boolean);
  return { tree, conflicts: new Set(conflicts) };
}
// A merge brings in its parents' work. Each test file it holds is judged against git's automatic merge of its parents, where
// that merges cleanly, and against its first parent where it conflicts (or for an octopus merge): a merge answers only for
// what it does beyond the automatic merge, a deletion included. A file equal to the base's current copy is fine too (a conflict
// resolved to the base's side); an older parent's copy isn't, since it may be weaker. It may not drop or change an approval file any parent holds,
// which would let the approval be added and used again.
function mergeChanges(commit: string): Change[] {
  const parents = git(['rev-list', '--parents', '-n', '1', commit]).trim().split(' ').slice(1);
  const blob = (spec: string) => exists(spec) ? git(['rev-parse', spec]).trim() : '';
  const auto = autoMerge(parents);
  const against = (path: string) => !auto || auto.conflicts.has(path) ? parents[0] : auto.tree;
  const paths = new Set([...git(['diff-tree', '-r', '--name-only', '-z', auto?.tree ?? parents[0], commit]).split('\0'), ...(auto?.conflicts ?? [])]);
  const based = (path: string) => exists(baseRef() + ':' + path) && blob(baseRef() + ':' + path) === blob(commit + ':' + path);
  const tests = [...paths].filter(path => path && isTest(path) && blob(against(path) + ':' + path) !== blob(commit + ':' + path) && !based(path))
    .map(path => ({ path, before: content(against(path) + ':' + path), after: content(commit + ':' + path) }));
  const approvals = new Set(parents.flatMap(parent => git(['ls-tree', '-r', '--name-only', '-z', parent, '--', approvalsFolder + '/']).split('\0').filter(Boolean)));
  return [...tests, ...[...approvals].flatMap(path => {
    const parent = parents.find(parent => exists(parent + ':' + path) && blob(parent + ':' + path) !== blob(commit + ':' + path));
    return parent ? [{ path, before: content(parent + ':' + path), after: content(commit + ':' + path) }] : [];
  })];
}
export function stagedChanges(commit?: string): Change[] {
  if (commit && exists(commit + '^2')) return mergeChanges(commit);
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
export function main(args: string[]): number {
  const commit = args[0] === '--commit' ? args[1] : undefined;
  // --commit <id> <branch> (CI and pre-push) needs no expiry: the parent tree already makes each approval single use.
  const branch = (commit ? args[2] : undefined) || currentBranch('.');
  // The approval check is on when the base holds the controller's key, whatever the branch does to this script.
  const approved = addedApprovals('.', commit, branch, commit ? undefined : Date.now());
  // Without it, the TEST-CHANGE rule applies from the commit that adds this script.
  if (!approved && (commit ? !exists(commit + ':scripts/hooks/test-integrity.ts') && !exists(commit + '^:scripts/hooks/test-integrity.ts') : !exists('HEAD:scripts/hooks/test-integrity.ts') && !exists(':scripts/hooks/test-integrity.ts'))) return 0;
  const message = commit ? git(['log', '-1', '--format=%B', commit]) : readFileSync(args[0], 'utf8');
  const errors = violations(stagedChanges(commit), message, approved);
  if (errors.length) { process.stderr.write('Test integrity: commit stopped.\n' + errors.join('\n') + '\nInvestigate; fix incorrect production code. TEST-CHANGE is not permission to change behavior.\n'); return 1; }
  return 0;
}
if (process.argv[1] && existsSync(process.argv[1]) && realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url))) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) { process.stderr.write('Test integrity: ' + String(error) + '\n'); process.exitCode = 1; }
}
