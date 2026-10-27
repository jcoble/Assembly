// Preferences over the socket (B7): a save on one socket reaches another's subscription, a later subscription gets the
// saved value, and a save cancelled while in flight is either fully saved or not at all. The desktop serves
// preferences in every case, so this always talks to it (CHECK4_API). It uses one key per case and puts back what the
// key held. node scripts/features/preferences.ts, with CHECK4_API, CHECK4_TOKEN, CHECK4_AGENT and CHECK4_MACHINE set
// (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_AGENT: agent, CHECK4_MACHINE: machine } = process.env;
const key = `check4.${agent}.${machine}`;

type Frame = { t: string; id?: number; sub?: number; code?: string; key?: string; items?: { key: string; item: { value: unknown } }[]; item?: { value: unknown } };
type Socket = Awaited<ReturnType<typeof open>>;

class Failed extends Error {}
function fail(step: string, why: string): never {
  console.log(`fail ${step}: ${why}`);
  throw new Failed();
}

async function open() {
  const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
  const frames: Frame[] = [];
  let next: (() => void) | undefined;
  socket.onmessage = (event) => {
    frames.push(JSON.parse(String(event.data)));
    next?.();
  };
  // One waiter at a time, cleared once it takes its frame, so a later frame is never taken for it.
  const frame = () =>
    new Promise<Frame>((resolve) => {
      next = () => {
        if (!frames.length) return;
        next = undefined;
        resolve(frames.shift()!);
      };
      next();
    });
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = () => reject(new Error('the socket failed to open'));
  });
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  if ((await frame()).t !== 'welcome') fail('hello is answered with welcome', 'it was not');
  const send = (message: object) => socket.send(JSON.stringify(message));
  return { socket, frame, send };
}

const value = (state: Frame) => state.items?.find((entry) => entry.key === key)?.item.value;
const save = (id: number, saved: unknown) => ({ t: 'call', id, feature: 'preferences', method: 'set', args: { key, value: saved } });
const mark = `${agent}-${process.pid}`;
const sockets: Socket[] = [];
let putBack: unknown;
let changed = false;

async function run() {
  const window = await open();
  const other = await open();
  sockets.push(window, other);
  other.send({ t: 'sub', id: 1, topic: 'preferences' });
  const state = await other.frame();
  if (state.t !== 'state') fail('a subscription starts with the state', `it started with ${state.t}`);
  putBack = value(state) ?? null;

  changed = true;
  window.send(save(1, mark));
  const saved = await window.frame();
  if (saved.t !== 'result' || saved.id !== 1) fail('a save is answered', `it answered ${JSON.stringify(saved)}`);
  console.log('ok a save is answered');
  const change = await other.frame();
  if (change.t !== 'change' || change.key !== key || change.item?.value !== mark)
    fail("a save reaches another socket's subscription", `it got ${JSON.stringify(change)}`);
  console.log("ok a save reaches another socket's subscription");

  const later = await open();
  sockets.push(later);
  later.send({ t: 'sub', id: 1, topic: 'preferences' });
  if (value(await later.frame()) !== mark) fail('a later subscription gets the saved value', 'it did not');
  console.log('ok a later subscription gets the saved value');

  // A cancel for a save in flight: the save is kept if it had committed, and answered cancelled with nothing saved if not.
  window.send(save(2, `${mark}-2`));
  window.send({ t: 'cancel', id: 2 });
  const cancelled = await window.frame();
  const check = await open();
  sockets.push(check);
  check.send({ t: 'sub', id: 1, topic: 'preferences' });
  const now = value(await check.frame());
  if (cancelled.t === 'result' ? now !== `${mark}-2` : cancelled.code !== 'cancelled' || now !== mark)
    fail('a cancelled save is all or nothing', `it answered ${JSON.stringify(cancelled)} and holds ${JSON.stringify(now)}`);
  console.log(`ok a cancelled save is all or nothing (${cancelled.t === 'result' ? 'it had committed' : 'cancelled'})`);
}

// Every way out puts the key back, on a socket of its own, then closes the sockets.
async function finish(code: number): Promise<never> {
  if (changed) {
    try {
      const restore = await open();
      sockets.push(restore);
      restore.send(save(3, putBack));
      if ((await restore.frame()).t !== 'result') throw new Error('the save failed');
    } catch (e) {
      console.log(`fail the key is put back: ${e instanceof Error ? e.message : e}`);
      code = 1;
    }
  }
  for (const each of sockets) each.socket.close();
  process.exit(code);
}

setTimeout(() => {
  console.log('fail preferences answer within 10 s: it was still waiting');
  setTimeout(() => process.exit(1), 3_000);
  void finish(1);
}, 10_000);
try {
  await run();
  await finish(0);
} catch (e) {
  if (!(e instanceof Failed)) console.log(`fail preferences: ${e instanceof Error ? e.message : e}`);
  await finish(1);
}
