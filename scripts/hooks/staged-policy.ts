#!/usr/bin/env node
// EdiPlatform's cheap no-new-Python hook, using Assembly's TypeScript tooling rule.
import { execFileSync } from 'node:child_process';
const commit = process.argv[2] === '--commit' ? process.argv[3] : undefined;
const exists = (spec: string) => { try { execFileSync('git', ['cat-file','-e',spec], {stdio:'ignore'}); return true; } catch { return false; } };
if (commit && !exists(commit + ':scripts/hooks/staged-policy.ts') && !exists(commit + '^:scripts/hooks/staged-policy.ts')) process.exit(0);
const args = commit ? ['diff-tree', '--root', '--no-commit-id', '-r', '--name-only', '-z', '--diff-filter=ACMR', commit] : ['diff', '--cached', '--name-only', '-z', '--diff-filter=ACMR'];
const paths = execFileSync('git', args, { encoding: 'utf8' }).split('\0').filter(path => path.endsWith('.py'));
if (paths.length) { process.stderr.write('New or modified Python tooling is blocked; Assembly tooling uses TypeScript.\n' + paths.join('\n') + '\n'); process.exitCode = 1; }
