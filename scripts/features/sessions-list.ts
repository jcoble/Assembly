// The sessions list's groups (docs/behavior/sessions-list.md) over the socket, as the window's row menu uses them:
// sessions/mark moves the case's session to Done, Settled and back to Working, and each move reaches the keyed
// sessions topic with its stamps. It ends with the session back in Working, where it started. The rail
// (sessions/rail) lists it under its provider and location with totals that count it, under Running while its turn
// runs and not once it has ended, drops it while it's settled, and the sessionRail topic names it when it moves. On the remote, Connect
// (machines/connect) reaches it and answers it reachable; on this machine, there's nothing to connect to. node
// scripts/features/sessions-list.ts, with CHECK4_API, CHECK4_TOKEN and CHECK4_SESSION set (scripts/features/README.md).
// The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session, CHECK4_MACHINE: machine, CHECK4_AGENT: agent } =
  process.env;

type Item = { sessionId: string; completedAt: number | null; settledAt: number | null };
type Frame = { t: string; id?: number; sub?: number; message?: string; items?: { key: string; item: Item }[]; item?: Item; result?: unknown };

class Failed extends Error {}
function fail(step: string, why: string): never {
  console.log(`fail ${step}: ${why}`);
  throw new Failed();
}

const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
const frames: Frame[] = [];
/** Whether the sessionRail topic (sub 2) has named the session since the last `railChange`, and who waits for it. */
let railNamed = false;
let railHeard: (() => void) | undefined;
let next: (() => void) | undefined;
socket.onmessage = (event) => {
  const got = JSON.parse(String(event.data)) as Frame & { key?: string };
  if (got.sub === 2 && got.t === 'change' && got.key === session) {
    railNamed = true;
    railHeard?.();
  }
  frames.push(got);
  next?.();
};
/** The next frame `wanted` takes, within `ms`; frames it doesn't take are dropped. */
function frame(wanted: (frame: Frame) => boolean, ms: number, step: string) {
  return new Promise<Frame>((resolve) => {
    const timer = setTimeout(() => {
      next = undefined;
      console.log(`fail ${step}: nothing within ${ms / 1000} s`);
      process.exit(1);
    }, ms);
    next = () => {
      while (frames.length) {
        const got = frames.shift()!;
        if (wanted(got)) {
          clearTimeout(timer);
          next = undefined;
          resolve(got);
          return;
        }
      }
    };
    next();
  });
}
const sub = (got: Frame) => got.sub === 1 || got.id === 1;
let ids = 10;
/** Sends the mark and waits for the change `moved` takes, which the backend sends before the call's answer; the
 * turn's own changes come on the same topic and are passed over. A failed call ends the wait at once. */
async function mark(state: string, moved: (item: Item) => boolean, step: string) {
  const id = ++ids;
  socket.send(JSON.stringify({ t: 'call', id, feature: 'sessions', method: 'mark', args: { session, state } }));
  const got = await frame(
    (got) => (sub(got) && got.t === 'change' && !!got.item && moved(got.item)) || (got.id === id && got.t === 'error'),
    10_000,
    step,
  );
  if (got.t === 'error') fail(step, `it failed: ${got.message ?? JSON.stringify(got)}`);
  const answer = await frame((got) => got.id === id && (got.t === 'result' || got.t === 'error'), 10_000, `${step} is answered`);
  if (answer.t !== 'result') fail(step, `it failed: ${answer.message ?? JSON.stringify(answer)}`);
  return got.item!;
}

/** The ids on the newest page of one group, `open` or `settled`. */
async function page(group: string, step: string): Promise<string[]> {
  const id = ++ids;
  socket.send(JSON.stringify({ t: 'call', id, feature: 'sessions', method: 'page', args: { group, limit: 200 } }));
  const answer = await frame((got) => got.id === id && (got.t === 'result' || got.t === 'error'), 10_000, step);
  if (answer.t !== 'result') fail(step, `it failed: ${answer.message ?? JSON.stringify(answer)}`);
  const result = answer.result as { sessions: Item[] };
  return result.sessions.map((item) => item.sessionId);
}

type Rail = { totals: { all: number; running: number; needsYou: number }; sessions: { sessionId: string }[] };
/** The case's provider and location in one tab of the rail. */
async function rail(tab: string, step: string): Promise<Rail> {
  const id = ++ids;
  const locations = [machine === 'local' ? 'local' : 'remote'];
  const args = { tab, providers: [agent], locations, limit: 200 };
  socket.send(JSON.stringify({ t: 'call', id, feature: 'sessions', method: 'rail', args }));
  const answer = await frame((got) => got.id === id && (got.t === 'result' || got.t === 'error'), 10_000, step);
  if (answer.t !== 'result') fail(step, `it failed: ${answer.message ?? JSON.stringify(answer)}`);
  return answer.result as Rail;
}
const listed = (page: Rail) => page.sessions.some((entry) => entry.sessionId === session);

/** Waits for sessionRail to name the session, at once if it has since the last wait, within `ms`. */
function railChange(step: string, ms: number) {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(() => {
      console.log(`fail ${step}: sessionRail didn't name the session within ${Math.round(ms / 1000)} s`);
      process.exit(1);
    }, Math.max(ms, 0));
    railHeard = () => {
      if (!railNamed) return;
      railNamed = false;
      railHeard = undefined;
      clearTimeout(timer);
      resolve();
    };
    railHeard();
  });
}
/** Reads the tab again at each sessionRail change naming the session until `holds` takes the page, within `ms`. */
async function untilRail(tab: string, step: string, ms: number, holds: (page: Rail) => boolean) {
  const end = Date.now() + ms;
  while (!holds(await rail(tab, step))) {
    if (Date.now() >= end) fail(step, `the rail didn't show it within ${Math.round(ms / 1000)} s`);
    await railChange(step, end - Date.now());
  }
}

async function main() {
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = () => reject(new Error('the socket failed to open'));
  });
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  await frame((got) => got.t === 'welcome', 10_000, 'hello is answered with welcome');

  socket.send(JSON.stringify({ t: 'sub', id: 1, topic: 'sessions', key: session }));
  const state = await frame((got) => sub(got) && got.t === 'state', 10_000, 'the keyed topic sends the session');
  const first = state.items?.find((entry) => entry.key === session)?.item;
  if (!first) fail('the keyed topic sends the session', 'its state has no item for it');
  if (first.completedAt !== null || first.settledAt !== null)
    fail('a new session is in Working', `it has ${JSON.stringify([first.completedAt, first.settledAt])}`);
  console.log('ok a new session is in Working');

  socket.send(JSON.stringify({ t: 'sub', id: 2, topic: 'sessionRail' }));
  await frame((got) => got.sub === 2 && got.t === 'state', 10_000, 'sessionRail sends its state');
  let step = 'the rail lists the session under its provider and location, and its totals count it';
  const all = await rail('all', step);
  if (!listed(all)) fail(step, "All's page doesn't have it");
  if (all.totals.all < all.sessions.length) fail(step, `All counts ${all.totals.all} for ${all.sessions.length} listed`);
  const running = await rail('running', step);
  if (running.totals.running !== running.sessions.length || running.totals.all !== all.totals.all)
    fail(step, `Running lists ${running.sessions.length} of ${running.totals.running}; All is ${running.totals.all}`);
  console.log(`ok ${step}`);
  railNamed = false;

  const done = await mark('done', (item) => item.completedAt !== null, 'Mark done stamps completedAt');
  if (done.settledAt !== null) fail('Mark done leaves it unsettled', `settledAt is ${done.settledAt}`);
  console.log('ok Mark done stamps completedAt');
  await railChange('sessionRail names the session when it moves', 5_000);
  console.log('ok sessionRail names the session when it moves');

  const settled = await mark('settled', (item) => item.settledAt !== null, 'Settle stamps settledAt');
  if (settled.completedAt !== done.completedAt) fail('Settle keeps the done time', `it moved to ${settled.completedAt}`);
  console.log('ok Settle stamps settledAt and keeps the done time');

  step = 'a settled session pages with Settled, not with the open ones';
  if (!(await page('settled', step)).includes(session)) fail(step, "Settled's page doesn't have it");
  if ((await page('open', step)).includes(session)) fail(step, 'the open page still has it');
  console.log(`ok ${step}`);
  step = 'a settled session leaves the rail';
  if (listed(await rail('all', step))) fail(step, 'All still has it');
  console.log(`ok ${step}`);

  await mark('working', (item) => item.completedAt === null && item.settledAt === null, 'Back to working clears both stamps');
  console.log('ok Back to working clears both stamps');
  step = 'back in Working, it is in the rail again';
  if (!listed(await rail('all', step))) fail(step, "All's page doesn't have it");
  console.log(`ok ${step}`);

  const connect = machine === 'local' ? 'Connect has no remote to reach here' : 'Connect reaches the remote';
  const id = ++ids;
  socket.send(JSON.stringify({ t: 'call', id, feature: 'machines', method: 'connect', args: { machine } }));
  const answer = await frame((got) => got.id === id && (got.t === 'result' || got.t === 'error'), 15_000, connect);
  if (machine === 'local') {
    if (answer.t !== 'error' || !answer.message?.includes('unknown remote machine')) fail(connect, JSON.stringify(answer));
  } else if (answer.t !== 'result' || (answer.result as { reachable: boolean }).reachable !== true) {
    fail(connect, JSON.stringify(answer));
  }
  console.log(`ok ${connect}`);

  step = 'once its turn has ended, Running no longer lists the session';
  await untilRail('running', step, 180_000, (page) => !listed(page));
  console.log(`ok ${step}`);

  // The case's turn started before this scenario could watch the rail, so it starts one of its own to see in Running.
  step = 'while a turn it starts runs, Running lists the session and counts it';
  // A remote machine can still be closing the case's run after its turn has ended; until it has, the send is refused.
  for (const end = Date.now() + 30_000; ; await new Promise((resolve) => setTimeout(resolve, 1_000))) {
    const sent = ++ids;
    socket.send(JSON.stringify({ t: 'call', id: sent, feature: 'send', method: 'send', args: { session, text: 'Reply with only the word ok.' } }));
    const started = await frame((got) => got.id === sent && (got.t === 'result' || got.t === 'error'), 30_000, step);
    if (started.t === 'result') break;
    if (Date.now() >= end || !started.message?.includes("can't take a message yet"))
      fail(step, `the turn didn't start: ${started.message ?? JSON.stringify(started)}`);
  }
  await untilRail('running', step, 30_000, (page) => listed(page) && page.totals.running >= 1);
  console.log(`ok ${step}`);
  step = 'once that turn has ended, Running no longer lists the session';
  await untilRail('running', step, 180_000, (page) => !listed(page));
  console.log(`ok ${step}`);
}

main()
  .then(() => process.exit(0))
  .catch((error) => {
    if (!(error instanceof Failed)) console.log(`fail the scenario ran: ${error.message}`);
    process.exit(1);
  });
