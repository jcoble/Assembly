#!/usr/bin/env node
// EdiPlatform's TEST-CHANGE discipline, adapted to Assembly's C#, Rust, TS and Dart.
// A reason is an audit trail, never approval to change behavior or proof of correctness.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
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
export function violations(changes: Change[], message: string): string[] {
  const errors: string[] = [], tests = changes.filter(change => (isTest(change.path) || Boolean(change.previousPath && isTest(change.previousPath))));
  const production = changes.some(change => !isTest(change.path) && !/^(?:docs\/|evidence\/|scripts\/|\.githooks\/|\.github\/|\.claude\/|\.codex\/)|\.(?:md|mdx)$/.test(change.path));
  const tokens = message.split('\n').map(line => line.trim());
  if (tests.length && !production && !tokens.some(line => /^TEST-CHANGE:\s*\S/.test(line))) errors.push('Test-only changes require TEST-CHANGE: <reason>.');
  for (const change of tests) {
    const before = assertions(change.before), after = assertions(change.after);
    for (const [name, values] of before) {
      const remaining = [...(after.get(name) ?? [])];
      let changed = false;
      for (const value of values) {
        const index = remaining.indexOf(value);
        if (index < 0) changed = true; else remaining.splice(index, 1);
      }
      const prefix = 'TEST-CHANGE: ' + name + ':';
      if (changed && !tokens.some(line => line.startsWith(prefix) && line.slice(prefix.length).trim())) errors.push(change.path + ': changed/removed assertion in ' + name + '; add ' + prefix + ' <reason and governing contract>.');
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
export function main(args: string[]): number {
  const commit = args[0] === '--commit' ? args[1] : undefined;
  // Activate prospectively. Removing the policy after activation cannot deactivate it.
  if (commit ? !exists(commit + ':scripts/hooks/test-integrity.ts') && !exists(commit + '^:scripts/hooks/test-integrity.ts') : !exists('HEAD:scripts/hooks/test-integrity.ts') && !exists(':scripts/hooks/test-integrity.ts')) return 0;
  const message = commit ? git(['log', '-1', '--format=%B', commit]) : readFileSync(args[0], 'utf8');
  const errors = violations(stagedChanges(commit), message);
  if (errors.length) { process.stderr.write('Test integrity: commit stopped.\n' + errors.join('\n') + '\nInvestigate; fix incorrect production code. TEST-CHANGE is not permission to change behavior.\n'); return 1; }
  return 0;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) { process.stderr.write('Test integrity: ' + String(error) + '\n'); process.exitCode = 1; }
}
