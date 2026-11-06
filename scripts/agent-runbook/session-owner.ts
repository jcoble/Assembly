import { readFileSync, unlinkSync } from 'node:fs';
import { join, basename } from 'node:path';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { atomicWrite } from './runbook.ts';

function processInfo(pid: number) {
  if (!Number.isSafeInteger(pid) || pid <= 0) throw Error('Invalid pid');
  const text = execFileSync('ps', ['-p', String(pid), '-o', 'ppid=', '-o', 'lstart=', '-o', 'args='], { encoding: 'utf8', timeout: 500, stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  const match = /^(\d+)\s+(\S{3}\s+\S{3}\s+\d+\s+\d\d:\d\d:\d\d\s+\d{4})\s+(\S+)/.exec(text);
  if (!match) throw Error('Unknown ps format');
  return { parent: Number(match[1]), startTime: match[2], executable: basename(match[3]) };
}
export function sessionOwner(provider: string, event: { hook_event_name?: string; session_id?: string; agent_id?: string }) {
  try {
    if (!['SessionStart', 'SessionEnd'].includes(event.hook_event_name || '') || Object.hasOwn(event, 'agent_id') || !event.session_id) return '';
    if (!['claude', 'codex'].includes(provider)) return '';
    let pid = process.ppid, current;
    const visited = new Set<number>();
    while (pid > 1 && !visited.has(pid)) {
      visited.add(pid); current = processInfo(pid);
      if (['claude', 'codex'].includes(current.executable)) break;
      pid = current.parent; current = undefined;
    }
    if (!current) return '';
    const path = join(homedir(), '.local/state/agent-runbook/owners', provider, createHash('sha256').update(event.session_id).digest('hex'));
    let previous: { pid: number; startTime: string } | undefined;
    try { previous = JSON.parse(readFileSync(path, 'utf8')); } catch { /* No usable owner yet. */ }
    if (event.hook_event_name === 'SessionEnd') {
      if (previous?.pid === pid && previous.startTime === current.startTime) unlinkSync(path);
      return '';
    }
    let warning = '';
    if (previous && previous.pid !== pid) {
      try {
        if (processInfo(previous.pid).startTime === previous.startTime) warning = `Another running copy of this session already exists (pid ${previous.pid}). Do not change anything or ask Jesse anything until you have messaged it and agreed who continues.`;
      } catch { /* Previous process exited or cannot be inspected. */ }
    }
    atomicWrite(path, JSON.stringify({ pid, startTime: current.startTime }));
    return warning;
  } catch { return ''; }
}
