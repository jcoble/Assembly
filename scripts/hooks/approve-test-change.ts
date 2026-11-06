#!/usr/bin/env node
// For the controller only: signs an approval that lets guard-test-changes.ts allow named test changes on one branch.
// node scripts/hooks/approve-test-change.ts --key <private key> --branch <b> --test <file>::<name> [--test ...] --reason "<text>" --out <folder>
// Writes <out>/<id>.json and signs it into <id>.json.sig with ssh-keygen. Never run this for your own test changes.
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, renameSync, writeFileSync } from 'node:fs';
import { hostname } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: {
  key: { type: 'string' }, branch: { type: 'string' }, test: { type: 'string', multiple: true }, reason: { type: 'string' }, out: { type: 'string' },
} });
const { key, branch, test, reason, out } = values;
if (!key || !branch || !test?.length || !reason || !out) throw new Error('usage: --key <private key> --branch <b> --test <file>::<name> [--test ...] --reason "<text>" --out <folder>');
const tests = test.map(spec => {
  const at = spec.indexOf('::');
  if (at <= 0 || at + 2 >= spec.length) throw new Error('--test must be <file>::<name>: ' + spec);
  return { file: spec.slice(0, at), name: spec.slice(at + 2) };
});
mkdirSync(out, { recursive: true });
const file = join(out, randomUUID() + '.json');
writeFileSync(file + '.tmp', JSON.stringify({ branch, tests, reason, approvedBy: 'controller@' + hostname(), at: new Date().toISOString() }, null, 2) + '\n');
renameSync(file + '.tmp', file);
execFileSync('ssh-keygen', ['-Y', 'sign', '-f', key, '-n', 'assembly-test-change', file], { stdio: ['ignore', 'ignore', 'inherit'] });
process.stdout.write(file + '\n');
