// The sessions list's groups (docs/behavior/sessions-list.md) over the socket, as the window's row menu uses them:
// sessions/mark moves the case's session to Done, Settled and back to Working, and each move reaches the keyed
// sessions topic with its stamps. It ends with the session back in Working, where it started. node
// scripts/features/sessions-list.ts, with CHECK4_API, CHECK4_TOKEN and CHECK4_SESSION set (scripts/features/README.md).
// The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session } = process.env;

type Item = { sessionId: string; completedAt: number | null; settledAt: number | null };
type Frame = { t: string; id?: number; sub?: number; message?: string; items?: { key: string; item: Item }[]; item?: Item };

class Failed extends Error {}
function fail(step: string, why: string): never {
  console.log(`fail ${step}: ${why}`);
  throw new Failed();
}

const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
const frames: Frame[] = [];
let next: (() => void) | undefined;
socket.onmessage = (event) => {
  frames.push(JSON.parse(String(event.data)));
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

  const done = await mark('done', (item) => item.completedAt !== null, 'Mark done stamps completedAt');
  if (done.settledAt !== null) fail('Mark done leaves it unsettled', `settledAt is ${done.settledAt}`);
  console.log('ok Mark done stamps completedAt');

  const settled = await mark('settled', (item) => item.settledAt !== null, 'Settle stamps settledAt');
  if (settled.completedAt !== done.completedAt) fail('Settle keeps the done time', `it moved to ${settled.completedAt}`);
  console.log('ok Settle stamps settledAt and keeps the done time');

  await mark('working', (item) => item.completedAt === null && item.settledAt === null, 'Back to working clears both stamps');
  console.log('ok Back to working clears both stamps');
}

main()
  .then(() => process.exit(0))
  .catch((error) => {
    if (!(error instanceof Failed)) console.log(`fail the scenario ran: ${error.message}`);
    process.exit(1);
  });
