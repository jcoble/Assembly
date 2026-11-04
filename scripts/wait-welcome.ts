// Readiness uses the owner control socket; credentials arrive on stdin, never argv or the URL.
import { execFile } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { backendIdentity } from './dev-remote-identity.ts';
import { promisify } from 'node:util';

const git = promisify(execFile);
export type Welcome = { t: 'welcome'; protocol: number; machine: string; format: number; commit: string | null; backendIdentity?: string };
type Options = { name: string; base: string; commit: string; seconds: number };

export async function waitForWelcome(options: Options, tokenInput: string | typeof process.stdin): Promise<string> {
  const url = new URL(options.base);
  if (!['http:', 'https:', 'ws:', 'wss:'].includes(url.protocol) || url.username || url.password || url.search || url.hash)
    throw new Error('readiness requires an HTTP or WebSocket base URL without credentials or query');
  url.protocol = url.protocol === 'https:' || url.protocol === 'wss:' ? 'wss:' : 'ws:';
  url.pathname = '/socket';
  if (!Number.isFinite(options.seconds) || options.seconds <= 0) throw new Error('deadline must be positive seconds');
  if (!/^[a-f0-9]{7,40}$/i.test(options.commit)) throw new Error('target commit is unknown');
  const port = url.port || (url.protocol === 'wss:' ? '443' : '80');
  const label = options.name.replace(/[\r\n]/g, ' ');
  const controller = new AbortController();
  let socket: WebSocket | undefined;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let last = 'no answer';
  let finished = false;
  let token: string;
  let complete: (value: string) => void;
  let fail: (error: Error) => void;
  const outcome = new Promise<string>((resolve, reject) => { complete = resolve; fail = reject; });
  // One deadline owns all connection attempts and any in-flight Git comparison.
  const deadline = setTimeout(() => finish(undefined, `deadline after ${options.seconds} s; last: ${last}`), options.seconds * 1000);
  function finish(commit?: string, reason?: string) {
    if (finished) return;
    finished = true;
    clearTimeout(deadline);
    clearTimeout(retry);
    controller.abort();
    socket?.close();
    if (commit) complete(commit);
    else fail(new Error(`${label} on port ${port}: ${reason}`));
  }
  async function command(args: string[]): Promise<string> {
    const result = await git('git', args, { signal: controller.signal });
    return result.stdout.trim();
  }
  function readToken(): Promise<string> {
    if (typeof tokenInput === 'string') return Promise.resolve(tokenInput);
    const input = tokenInput;
    return new Promise((resolve, reject) => {
      let buffered = '';
      function clear() {
        input.off('data', data); input.off('end', end); input.off('error', error);
        controller.signal.removeEventListener('abort', abort);
        input.pause();
      }
      function data(part: Buffer | string) {
        buffered += part;
        if (buffered.includes('\n')) { clear(); resolve(buffered.split('\n')[0].replace(/\r$/, '')); }
      }
      function end() { clear(); resolve(buffered); }
      function error() { clear(); reject(new Error('owner token input failed')); }
      function abort() { clear(); reject(new Error('owner token input deadline')); }
      input.on('data', data); input.once('end', end); input.once('error', error);
      controller.signal.addEventListener('abort', abort, { once: true });
    });
  }
  function again(peer: WebSocket) {
    if (finished || peer !== socket) return;
    peer.onopen = peer.onmessage = peer.onerror = peer.onclose = null;
    peer.close();
    socket = undefined;
    clearTimeout(retry);
    retry = setTimeout(connect, 100);
  }
  let target: string;
  let targetIdentity: string;
  function connect() {
    if (finished) return;
    const peer = socket = new WebSocket(url);
    peer.onopen = () => peer.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
    peer.onerror = () => { last = 'socket unreachable'; again(peer); };
    peer.onclose = event => { last = `socket closed (${event.code})`; again(peer); };
    peer.onmessage = async event => {
      peer.onmessage = null;
      let frame: Welcome;
      try { frame = JSON.parse(String(event.data)); }
      catch { finish(undefined, 'invalid welcome JSON'); return; }
      if (!frame || frame.t !== 'welcome' || typeof frame.machine !== 'string' || !frame.machine.trim()) {
        finish(undefined, 'invalid welcome machine or frame'); return;
      }
      if (frame.protocol !== 1) { finish(undefined, 'incompatible welcome protocol (expected 1)'); return; }
      if (frame.format !== 5) { finish(undefined, 'incompatible welcome format (expected 5)'); return; }
      if (frame.commit === null) { finish(undefined, 'welcome commit is null (unknown build)'); return; }
      if (typeof frame.commit !== 'string' || !/^[a-f0-9]{40}$/i.test(frame.commit)) {
        finish(undefined, 'welcome commit is missing or invalid (unknown build)'); return;
      }
      if (typeof frame.backendIdentity !== 'string' || !/^[a-f0-9]{64}$/.test(frame.backendIdentity)) {
        finish(undefined, `remote ${frame.commit}; desktop ${target}: welcome backend identity is missing or invalid`); return;
      }
      if (frame.backendIdentity === targetIdentity) finish(frame.commit);
      else finish(undefined, `backend mismatch: remote ${frame.commit}; desktop ${target}`);
    };
  }
  void readToken().then(value => {
    if (!value || /[\r\n]/.test(value)) throw new Error('owner token is missing or invalid');
    token = value;
    return command(['rev-parse', '--verify', `${options.commit}^{commit}`]);
  }).then(async value => {
    target = value;
    targetIdentity = await backendIdentity(process.cwd(), target, controller.signal);
    if (!finished) connect();
  }, error => {
    if (!finished) finish(undefined, error instanceof Error && error.message.startsWith('owner token') ? error.message : 'target commit is unknown');
  }).catch(error => { if (!finished) finish(undefined, String(error)); });
  return outcome;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [name, base, commit, rawSeconds = '30'] = process.argv.slice(2);
  let output: string, status = 0;
  try {
    if (!name || !base || !commit) throw new Error('usage: wait-version.sh <name> <base url> <commit> [seconds]');
    output = await waitForWelcome({ name, base, commit, seconds: Number(rawSeconds) }, process.stdin);
  } catch (error) {
    output = error instanceof Error ? error.message : 'welcome readiness failed';
    status = 1;
  }
  // This short-lived CLI owns its sockets. Exit after flushing, including when a peer ignores close.
  process.stdout.write(`${output}\n`, () => process.exit(status));
}
