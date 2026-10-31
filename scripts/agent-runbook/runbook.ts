import { mkdirSync, readFileSync, writeFileSync, renameSync, openSync, closeSync, fsyncSync, unlinkSync, existsSync, statSync, readSync } from 'node:fs';
import { join, dirname, isAbsolute } from 'node:path';
import { homedir, hostname } from 'node:os';
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { rules } from './rules.ts';

export type Binding = { path: string; project: string; owner: string; readOnly: boolean; parent?: string; ssh?: string };
export const STATE = '\n<!-- live-state -->\n';
export const OBSERVED = '\n<!-- observed-checkpoint -->\n';
const root = () => join(homedir(), '.local/state/agent-runbook');
export const bindingPath = (provider: string, id: string) =>
  join(root(), 'bindings', provider, createHash('sha256').update(id).digest('hex') + '.json');
export function identity(provider: string, id: string) {
  if (!['codex', 'claude'].includes(provider) || !id || id.length > 256) throw Error('Expected provider codex|claude and a session ID.');
  return provider + ':' + id;
}
export function atomicWrite(path: string, text: string) {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  const temp = path + '.' + randomUUID() + '.tmp';
  let fd: number | undefined;
  try {
    fd = openSync(temp, 'wx', 0o600);
    writeFileSync(fd, text, 'utf8');
    fsyncSync(fd); closeSync(fd); fd = undefined;
    renameSync(temp, path);
  } finally {
    if (fd !== undefined) closeSync(fd);
    if (existsSync(temp)) unlinkSync(temp);
  }
}
export function getBinding(provider: string, id: string): Binding | undefined {
  identity(provider, id);
  const path = bindingPath(provider, id);
  return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : undefined;
}
export function saveBinding(provider: string, id: string, binding: Binding) {
  identity(provider, id);
  if (!isAbsolute(binding.path)) throw Error('Runbook path must be absolute.');
  if (binding.ssh && (!/^[a-zA-Z0-9][a-zA-Z0-9_.@-]*$/.test(binding.ssh) || !binding.readOnly)) throw Error('SSH bindings must be read-only and use an existing SSH alias.');
  atomicWrite(bindingPath(provider, id), JSON.stringify(binding));
}
export function projectFor(cwd: string) {
  const parts = cwd.split('/');
  return parts.includes('Assembly') ? 'Assembly' : parts.includes('rental-management') ? 'rental-management' : '';
}
function render(binding: Binding, state: string, checkpoint = 'Not checkpointed yet.') {
  const text = rules(binding.project) + STATE + '## Live state\n' + state.trim() + OBSERVED + '## Automatic checkpoint\n' + checkpoint.trim() + '\n';
  if (text.length > 12000) throw Error('Runbook exceeds 12,000 characters; shorten live state first.');
  return text;
}
export function init(provider: string, id: string, cwd: string, path?: string, task?: string): Binding {
  const owner = identity(provider, id);
  const current = getBinding(provider, id);
  if (current) {
    if (path && current.path !== path) throw Error('Session is already bound; explicitly bind the intended existing runbook.');
    return current;
  }
  path ??= join(root(), 'runs', provider, createHash('sha256').update(id).digest('hex'), 'runbook.md');
  if (existsSync(path) || existsSync(path + '.owner.json')) throw Error('Refusing to claim an existing runbook.');
  const binding = { path, project: projectFor(cwd), owner, readOnly: false };
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  writeFileSync(path + '.owner.json', JSON.stringify({ owner }), { flag: 'wx', mode: 0o600 });
  atomicWrite(path, render(binding, 'Task: ' + (task || 'UNASSIGNED — parent must bind/record the canonical host/path on the active task.') + '\nCurrent step / next action: not recorded.\nWriter: ' + owner + '\nHost/worktree: ' + hostname() + ' ' + cwd));
  saveBinding(provider, id, binding);
  return binding;
}
function ownerCheck(provider: string, id: string, binding: Binding) {
  if (binding.readOnly || binding.ssh || binding.owner !== identity(provider, id)) throw Error('Read-only child/remote binding: report state to your spawning parent.');
  const owner = JSON.parse(readFileSync(binding.path + '.owner.json', 'utf8'));
  if (owner.owner !== binding.owner) throw Error('Runbook writer does not match; ask the parent.');
}
export function readRunbook(binding: Binding) {
  const text = binding.ssh
    ? execFileSync('ssh', ['-o', 'BatchMode=yes', '-o', 'ConnectTimeout=3', binding.ssh, "cat -- '" + binding.path.replaceAll("'", "'\\''") + "'"], { encoding: 'utf8', timeout: 4500, maxBuffer: 65536 })
    : readFileSync(binding.path, 'utf8');
  if (text.length > 12000) throw Error('Runbook exceeds 12,000 characters; ask its parent to shorten it.');
  return text;
}
export function update(provider: string, id: string, state: string) {
  const binding = getBinding(provider, id);
  if (!binding) throw Error('No runbook binding; initialize or ask your parent.');
  ownerCheck(provider, id, binding);
  if (!state.trim() || state.length > 6500 || state.includes(STATE.trim()) || state.includes(OBSERVED.trim())) throw Error('Live state must be 1–6,500 characters without managed section markers.');
  const old = readRunbook(binding);
  if (!old.includes(STATE) || !old.includes(OBSERVED)) throw Error('Unrecognized runbook format; do not overwrite it.');
  const checkpoint = old.split(OBSERVED)[1].replace(/^## Automatic checkpoint\n/, '');
  atomicWrite(binding.path, render(binding, 'Updated: ' + new Date().toISOString() + '\n' + state, checkpoint));
}
function transcriptExcerpt(path?: string) {
  if (!path) return 'Transcript unavailable; semantic state was not inferred.';
  let fd: number | undefined;
  try {
    fd = openSync(path, 'r');
    const size = statSync(path).size;
    const buffer = Buffer.alloc(Math.min(size, 131072));
    readSync(fd, buffer, 0, buffer.length, size - buffer.length);
    const messages: string[] = [];
    for (const line of buffer.toString('utf8').split('\n').reverse()) {
      try {
        const row = JSON.parse(line);
        const message = row.type === 'response_item' ? row.payload : row.message;
        const role = message?.role || (['user', 'assistant'].includes(row.type) ? row.type : '');
        if (!['user', 'assistant'].includes(role) || messages.some(x => x.startsWith(role + ':'))) continue;
        const content = typeof message?.content === 'string' ? message.content : (message?.content || []).filter((x: any) => ['text', 'input_text', 'output_text'].includes(x.type)).map((x: any) => x.text).join('\n');
        if (content?.trim()) messages.push(role + ': ' + content.trim().slice(-650));
        if (messages.length === 2) break;
      } catch { /* Partial/unrecognized transcript records are not facts. */ }
    }
    return messages.length ? messages.reverse().join('\n') : 'No recognized recent text; semantic state was not inferred.';
  } catch { return 'Transcript unreadable; semantic state was not inferred.'; }
  finally { if (fd !== undefined) closeSync(fd); }
}
export function checkpoint(provider: string, id: string, event: any) {
  const binding = getBinding(provider, id);
  if (!binding) throw Error('No bound runbook before compaction.');
  if (binding.readOnly) return;
  ownerCheck(provider, id, binding);
  const old = readRunbook(binding);
  if (!old.includes(STATE) || !old.includes(OBSERVED)) throw Error('Unrecognized runbook format; ask its owner.');
  const state = old.split(STATE)[1].split(OBSERVED)[0].replace(/^## Live state\n/, '');
  let git = 'Git checkout not available.';
  try { git = execFileSync('git', ['-C', event.cwd, 'rev-parse', '--abbrev-ref', 'HEAD'], { encoding: 'utf8', timeout: 1000, stdio: ['ignore', 'pipe', 'ignore'] }).trim() + ' @ ' + execFileSync('git', ['-C', event.cwd, 'rev-parse', 'HEAD'], { encoding: 'utf8', timeout: 1000, stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch {}
  const observed = [
    'Checkpoint: ' + new Date().toISOString() + '; host: ' + hostname(),
    'Cwd: ' + String(event.cwd).slice(0,500), 'Branch/HEAD: ' + git,
    'Recent transcript observations — not approved decisions or verified results:',
    transcriptExcerpt(event.transcript_path),
  ].join('\n');
  atomicWrite(binding.path, render(binding, state, observed));
}
export function recover(provider: string, id: string, binding: Binding, eventName = 'SessionStart') {
  let text: string;
  try { text = readRunbook(binding); }
  catch (error) { text = rules(binding.project) + '\nRECOVERY BLOCKED: ' + (error as Error).message + '\nAsk the spawning parent / runbook owner for current state before affected work.'; }
  const guidance = binding.readOnly
    ? 'Check with your spawning parent (' + (binding.parent || binding.owner) + ') before resuming affected work. This binding is read-only; report updates to the parent.'
    : 'Parent: update semantic state on state changes, before manual compaction and at handoff using agent-runbook update ' + provider + ' ' + id + ' (stdin). Keep task/plan/PR/proof links, next action, lanes, questions and blockers current. Record this host/path on the active Notion task; checkpoint there at milestones/handoff. Automatic excerpts do not replace that update.';
  return { hookSpecificOutput: { hookEventName: eventName, additionalContext: text + '\nRunbook: ' + (binding.ssh ? binding.ssh + ':' : hostname() + ':') + binding.path + '\n' + guidance } };
}
