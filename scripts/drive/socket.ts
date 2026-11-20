// Small socket framing shared only by the retained diagnostic scripts. The caller owns its deadline.
import { pathToFileURL } from 'node:url';

export type Frame = { t: string; [key: string]: any };
export type Welcome = { t: 'welcome'; protocol: number; machine: string; format: number; commit: string | null };
type Options = { signal: AbortSignal; endpoint?: string; machine?: string; format?: number; commit?: string };
const label = (name: string) => name.split('.')[0]!.toLowerCase();

export async function connectSocket(api: string, token: string, options: Options) {
  options.signal.throwIfAborted();
  const url = new URL(options.endpoint ?? '/socket', api.replace(/^http/, 'ws'));
  if (url.username || url.password || url.search) throw new Error('socket address must not contain credentials or query parameters');
  const socket = new WebSocket(url);
  const frames: Frame[] = [];
  const waiting = new Set<() => void>();
  let queuedBytes = 0, next = 0;
  let failure: Error | undefined;
  let closing = false;
  let didClose!: () => void;
  const closed = new Promise<void>(resolve => { didClose = resolve; });
  const wake = () => { for (const notify of [...waiting]) notify(); };
  const fail = (error: Error) => {
    failure ??= error;
    wake();
    if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) socket.close();
  };
  const abort = () => fail(new Error(String(options.signal.reason?.message ?? options.signal.reason ?? 'socket cancelled')));
  options.signal.addEventListener('abort', abort, { once: true });
  socket.onmessage = event => {
    try {
      const text = String(event.data);
      if (Buffer.byteLength(text) > 4 * 1024 * 1024) throw new Error('socket frame exceeded 4 MB');
      const frame = JSON.parse(text) as Frame;
      queuedBytes += Buffer.byteLength(JSON.stringify(frame));
      if (queuedBytes > 16 * 1024 * 1024) throw new Error('diagnostic socket queue exceeded 16 MB');
      if (typeof frame.t !== 'string') throw new Error('socket sent a malformed frame');
      frames.push(frame);
      wake();
    } catch { fail(new Error('socket sent invalid or excessive JSON')); }
  };
  socket.onerror = () => fail(new Error('socket connection failed'));
  socket.onclose = event => {
    options.signal.removeEventListener('abort', abort);
    if (!closing) fail(new Error(`socket closed (${event.code})`));
    didClose();
  };
  const wait = async (predicate: (frame: Frame) => boolean, stopped?: () => boolean): Promise<Frame> => {
    for (;;) {
      if (failure) throw failure;
      if (stopped?.()) throw new Error('subscription disposed');
      const at = frames.findIndex(predicate);
      if (at >= 0) {
        const frame = frames.splice(at, 1)[0]!;
        queuedBytes -= Buffer.byteLength(JSON.stringify(frame));
        if (frame.t === 'error') throw new Error(`${frame.code}: ${frame.message ?? 'socket request failed'}`);
        return frame;
      }
      await new Promise<void>(resolve => { const notify = () => { waiting.delete(notify); resolve(); }; waiting.add(notify); });
    }
  };
  const send = (frame: Record<string, unknown>) => {
    if (failure) throw failure;
    socket.send(JSON.stringify(frame));
  };
  const close = async () => {
    closing = true;
    options.signal.removeEventListener('abort', abort);
    failure ??= new Error('socket disposed');
    wake();
    frames.length = 0;
    if (socket.readyState !== WebSocket.CLOSED) socket.close();
    await closed;
  };
  try {
    await new Promise<void>((resolve, reject) => {
      const failed = () => { if (failure) { waiting.delete(failed); reject(failure); } };
      waiting.add(failed);
      socket.onopen = () => { waiting.delete(failed); resolve(); };
    });
    send({ t: 'hello', token, protocol: 1 });
    const welcome = await wait(frame => frame.t === 'welcome' || frame.t === 'error') as Welcome;
    if (welcome.protocol !== 1 || !Number.isInteger(welcome.format) || welcome.format !== (options.format ?? 5))
      throw new Error('incompatible socket protocol or row format');
    if (!welcome.machine || (options.machine && label(welcome.machine) !== label(options.machine)))
      throw new Error('socket welcome names the wrong machine');
    if (options.commit !== undefined && (!/^[0-9a-f]{40}$/i.test(welcome.commit ?? '') || welcome.commit !== options.commit))
      throw new Error('socket welcome has an unknown or stale build commit');
    const request = async (frame: Record<string, unknown>) => {
      const id = ++next;
      send({ ...frame, id });
      return await wait(answer => answer.id === id && (answer.t === 'result' || answer.t === 'error'));
    };
    return {
      welcome, request,
      async call(feature: string, method: string, args: unknown = {}) { return (await request({ t: 'call', feature, method, args })).result; },
      async subscribe(topic: string, key?: string, after?: number) {
        const id = ++next;
        send({ t: 'sub', id, topic, ...(key === undefined ? {} : { key }), ...(after === undefined ? {} : { after }) });
        const state = await wait(frame => frame.sub === id && frame.t === 'state' || frame.id === id && frame.t === 'error');
        let active = true;
        return {
          state,
          async next() {
            if (!active) throw new Error('subscription disposed');
            return await wait(frame => frame.sub === id || frame.id === id && frame.t === 'error', () => !active);
          },
          close() {
            if (!active) return;
            active = false;
            wake();
            if (!failure && !closing) send({ t: 'unsub', id });
          }
        };
      },
      close
    };
  } catch (error) { await close(); throw error; }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(new Error('socket scenario did not complete within 10 seconds')), 10_000);
  let connection: Awaited<ReturnType<typeof connectSocket>> | undefined;
  try {
    connection = await connectSocket(process.env.CHECK4_HOST_API!, process.env.CHECK4_HOST_TOKEN!, {
      signal: controller.signal, machine: process.env.CHECK4_HOST_NAME, commit: process.env.CHECK4_HOST_COMMIT
    });
    console.log(`ok hello is answered with welcome from ${connection.welcome.machine}`);
    try { await connection.call('check4', 'nothing'); throw new Error('unknown call succeeded'); }
    catch (error) { if (!(error instanceof Error) || !error.message.startsWith('not_found:')) throw error; }
    console.log('ok an unknown call answers not_found');
  } catch (error) { console.log(`fail socket scenario: ${(error as Error).message}`); process.exitCode = 1; }
  finally { clearTimeout(timeout); await connection?.close(); }
}
