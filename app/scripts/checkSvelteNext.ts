#!/usr/bin/env node
/**
 * checkSvelteNext.ts — type-check the Svelte components the app shell owns.
 *
 * WHY THIS WRAPPER EXISTS
 * `pnpm run check` runs `tsc --noEmit`, and TypeScript cannot read a `.svelte`
 * file at all. So until now nothing type-checked the ~45 components this shell
 * is made of: a prop name that no longer matches the library it comes from
 * would compile, build, and render wrong.
 *
 * `svelte-check` closes that gap, but it checks the WHOLE project, and the old
 * shell (`src/routes/+page.svelte` and the components only it mounts) has a
 * long-standing backlog of its own type errors. Running the tool bare would
 * report a hundred-odd problems nobody on this branch caused, which is the same
 * as reporting nothing.
 *
 * So: run `svelte-check` over everything (it has to build one program anyway),
 * then keep only the errors that land in the files the app shell owns, listed
 * in OWNED below. Exit 1 if any of those has an error; exit 0 otherwise.
 * Warnings are printed for the owned paths but never fail the run — accessibility
 * and unused-CSS notes are advice, not a broken build.
 *
 * The old shell's own errors are counted and reported as one line at the end so
 * the backlog stays visible without being in the way. If you fix the old shell,
 * widen OWNED.
 *
 * Usage: node --experimental-strip-types scripts/checkSvelteNext.ts
 *        (or `pnpm run check:svelte`)
 */

import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/**
 * The files the app shell owns. A path counts as owned when it starts with one
 * of these, compared against the project-relative path svelte-check prints.
 */
const OWNED = [
  'src/lib/shell/',
  'src/lib/components/ui/',
  'src/routes/',
  'src/lib/utils.ts'
];

function isOwned(file) {
  return OWNED.some((prefix) =>
    prefix.endsWith('/') ? file.startsWith(prefix) : file === prefix
  );
}

/**
 * svelte-check's `--output machine` format, one record per line:
 *   <timestamp> <SEVERITY> "<file>" <line>:<column> "<message>"
 * plus a final `<timestamp> COMPLETED <n> FILES <n> ERRORS ...` line.
 */
const RECORD = /^\d+\s+(ERROR|WARNING)\s+"((?:[^"\\]|\\.)*)"\s+(\d+):(\d+)\s+"((?:[^"\\]|\\.)*)"\s*$/;

function unescape(value) {
  return value.replace(/\\n/g, '\n').replace(/\\"/g, '"').replace(/\\\\/g, '\\');
}

function run(): Promise<{ out: string; err: string; status: number | null; signal: string | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(
      process.execPath,
      [
        path.join(projectRoot, 'node_modules', 'svelte-check', 'bin', 'svelte-check'),
        '--tsconfig',
        './tsconfig.json',
        '--output',
        'machine'
      ],
      { cwd: projectRoot, stdio: ['ignore', 'pipe', 'pipe'] }
    );

    let out = '';
    let err = '';
    child.stdout.on('data', (chunk) => (out += chunk));
    child.stderr.on('data', (chunk) => (err += chunk));
    child.on('error', reject);
    child.on('close', (status, signal) => resolve({ out, err, status, signal }));
  });
}

const { out, err, status, signal } = await run();

const ownedErrors = [];
const ownedWarnings = [];
let otherErrors = 0;
let parsedErrors = 0;
let parsedWarnings = 0;
let totals = null;

for (const line of out.split('\n')) {
  const completed = /(?:^\d+\s+)?COMPLETED\s+\d+\s+FILES\s+(\d+)\s+ERRORS\s+(\d+)\s+WARNINGS\b/.exec(line.trim());
  if (completed) {
    totals = { errors: Number(completed[1]), warnings: Number(completed[2]) };
    continue;
  }
  const match = RECORD.exec(line.trim());
  if (!match) continue;
  const [, severity, file, row, column, message] = match;
  if (severity === 'ERROR') parsedErrors += 1;
  else parsedWarnings += 1;
  const entry = { file, row, column, message: unescape(message) };
  if (!isOwned(file)) {
    if (severity === 'ERROR') otherErrors += 1;
    continue;
  }
  if (severity === 'ERROR') ownedErrors.push(entry);
  else ownedWarnings.push(entry);
}

if (!totals) {
  console.error('svelte-check did not finish. Its output was:');
  console.error(out.trim() || '(nothing on stdout)');
  if (err.trim()) console.error(err.trim());
  process.exit(2);
}

if (parsedErrors !== totals.errors || parsedWarnings !== totals.warnings) {
  console.error(
    `svelte-check reported ${totals.errors} error(s) and ${totals.warnings} warning(s), ` +
      `but the wrapper parsed ${parsedErrors} error(s) and ${parsedWarnings} warning(s).`
  );
  process.exit(2);
}

const expectedBacklogFailure = status === 1 && totals.errors > 0 && otherErrors === totals.errors;
if (status !== 0 && !expectedBacklogFailure) {
  console.error(`svelte-check failed with ${signal ? `signal ${signal}` : `exit status ${status}`}.`);
  if (err.trim()) console.error(err.trim());
  process.exit(2);
}

const show = (entry, label) =>
  console.log(`${label} ${entry.file}:${entry.row}:${entry.column}  ${entry.message.split('\n')[0]}`);

for (const entry of ownedWarnings) show(entry, 'warning');
for (const entry of ownedErrors) show(entry, 'ERROR  ');

console.log('');
console.log(
  `Files the app shell owns: ${ownedErrors.length} error(s), ${ownedWarnings.length} warning(s).`
);
console.log(
  `Elsewhere in the project (not checked by this gate): ${otherErrors} error(s) — the old shell's own backlog.`
);

// ── The font floor ───────────────────────────────────────────────────────────
// The shell's rule is 13px body / 12px meta, and the class-based Tailwind trap
// is documented — but raw font-size and font shorthand values in a component's
// <style> block are invisible to a type checker. Walk the owned .svelte files
// and fail on any hardcoded px or rem size under 12px.
import { readdirSync, readFileSync, statSync } from 'node:fs';

function svelteFilesUnder(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) out.push(...svelteFilesUnder(full));
    else if (name.endsWith('.svelte')) out.push(full);
  }
  return out;
}

const fontFloorHits = [];
for (const prefix of OWNED) {
  if (!prefix.endsWith('/')) continue;
  const dir = path.join(projectRoot, prefix);
  let files = [];
  try {
    files = svelteFilesUnder(dir);
  } catch {
    continue;
  }
  for (const file of files) {
    const text = readFileSync(file, 'utf8');
    for (const match of text.matchAll(/(?<![-\w])(font-size|font)\s*:\s*([^;}]*)/g)) {
      // A calc() size has more than one number; reading only the first one misjudges it.
      if (match[2].includes('calc(')) continue;
      const size = /(\d*\.?\d+)(px|rem)\b/.exec(match[2]);
      if (!size) continue;
      const pixels = Number(size[1]) * (size[2] === 'rem' ? 16 : 1);
      if (pixels < 12) {
        const row = text.slice(0, match.index).split('\n').length;
        fontFloorHits.push(
          `${path.relative(projectRoot, file)}:${row}  ${match[1]}: ${size[1]}${size[2]}`
        );
      }
    }
  }
}

if (fontFloorHits.length > 0) {
  console.log('');
  console.log('Text below the 12px floor (the shell rule is 13px body / 12px meta):');
  for (const hit of fontFloorHits) console.log(`ERROR   ${hit}`);
}

if (ownedErrors.length > 0 || fontFloorHits.length > 0) {
  console.log('');
  console.log('Checking the app shell failed. Fix the problems listed above.');
  process.exit(1);
}
