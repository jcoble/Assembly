// Preferences over the socket (B7): a save on one socket reaches another's subscription, a later subscription gets the
// saved value, and a save cancelled while in flight is either fully saved or not at all. The desktop serves
// preferences in every case, so this always talks to it (CHECK4_API). It uses one key per case and puts back what the
// key held. node scripts/features/preferences.ts, with CHECK4_API, CHECK4_TOKEN, CHECK4_AGENT and CHECK4_MACHINE set
// (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_AGENT: agent, CHECK4_MACHINE: machine } = process.env;
const key = `check4.${agent}.${machine}`;

type Frame = { t: string; id?: number; sub?: number; code?: string; key?: string; items?: { key: string; item: { value: unknown } }[]; item?: { value: unknown } };

function fail(step: string, why: string): never {
  console.log(`fail ${step}: ${why}`);
  process.exit(1);
}
setTimeout(() => fail('preferences answer within 10 s', 'it was still waiting'), 10_000);

async function open() {
  const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
  const frames: Frame[] = [];
  let next: (() => void) | undefined;
  socket.onmessage = (event) => {
    frames.push(JSON.parse(String(event.data)));
    next?.();
  };
  socket.onerror = () => fail('the socket opens', 'it failed');
  const frame = () =>
    new Promise<Frame>((resolve) => {
      const take = () => frames.length && resolve(frames.shift()!);
      next = take;
      take();
    });
  await new Promise((resolve) => (socket.onopen = resolve));
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  if ((await frame()).t !== 'welcome') fail('hello is answered with welcome', 'it was not');
  const send = (message: object) => socket.send(JSON.stringify(message));
  return { socket, frame, send };
}

const value = (state: Frame) => state.items?.find((entry) => entry.key === key)?.item.value;
const save = (id: number, saved: unknown) => ({ t: 'call', id, feature: 'preferences', method: 'set', args: { key, value: saved } });
const mark = `${agent}-${process.pid}`;

const window = await open();
const other = await open();
other.send({ t: 'sub', id: 1, topic: 'preferences' });
const state = await other.frame();
if (state.t !== 'state') fail('a subscription starts with the state', `it started with ${state.t}`);
const before = value(state) ?? null;

window.send(save(1, mark));
const saved = await window.frame();
if (saved.t !== 'result' || saved.id !== 1) fail('a save is answered', `it answered ${JSON.stringify(saved)}`);
console.log('ok a save is answered');
const change = await other.frame();
if (change.t !== 'change' || change.key !== key || change.item?.value !== mark)
  fail("a save reaches another socket's subscription", `it got ${JSON.stringify(change)}`);
console.log("ok a save reaches another socket's subscription");

const later = await open();
later.send({ t: 'sub', id: 1, topic: 'preferences' });
if (value(await later.frame()) !== mark) fail('a later subscription gets the saved value', 'it did not');
console.log('ok a later subscription gets the saved value');

// A cancel for a save in flight: the save is kept if it had committed, and answered cancelled with nothing saved if not.
window.send(save(2, `${mark}-2`));
window.send({ t: 'cancel', id: 2 });
const cancelled = await window.frame();
const check = await open();
check.send({ t: 'sub', id: 1, topic: 'preferences' });
const now = value(await check.frame());
if (cancelled.t === 'result' ? now !== `${mark}-2` : cancelled.code !== 'cancelled' || now !== mark)
  fail('a cancelled save is all or nothing', `it answered ${JSON.stringify(cancelled)} and holds ${JSON.stringify(now)}`);
console.log(`ok a cancelled save is all or nothing (${cancelled.t === 'result' ? 'it had committed' : 'cancelled'})`);

window.send(save(3, before));
if ((await window.frame()).t !== 'result') fail('the key is put back', 'the save failed');
for (const each of [window, other, later, check]) each.socket.close();
process.exit(0);
