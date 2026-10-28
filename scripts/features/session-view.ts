// The session view (g0-session) over the socket, as the window uses it: the header's keyed sessions topic, the
// sessions column's working dot (the session's activeRunId) coming and going with the turn, the first rows page,
// an opened tool call's detail, the Context tab's files, and the sessions column's page.
// node scripts/features/session-view.ts, with CHECK4_API, CHECK4_TOKEN, CHECK4_SESSION and CHECK4_RUN set
// (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session, CHECK4_RUN: run } = process.env;

type Item = { sessionId: string; parentSessionId: string | null; agent: string; activeRunId: string | null };
type Frame = { t: string; id?: number; sub?: number; result?: any; message?: string; items?: { key: string; item: Item }[]; item?: Item };

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
let ids = 10;
async function call(feature: string, method: string, args: object, step: string) {
  const id = ++ids;
  socket.send(JSON.stringify({ t: 'call', id, feature, method, args }));
  const answer = await frame((got) => got.id === id && (got.t === 'result' || got.t === 'error'), 30_000, step);
  if (answer.t !== 'result') fail(step, `it failed: ${answer.message ?? JSON.stringify(answer)}`);
  return answer.result;
}

async function main() {
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = () => reject(new Error('the socket failed to open'));
  });
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  await frame((got) => got.t === 'welcome', 10_000, 'hello is answered with welcome');

  socket.send(JSON.stringify({ t: 'sub', id: 1, topic: 'sessions', key: session }));
  const sub = (got: Frame) => got.sub === 1 || got.id === 1;
  const state = await frame((got) => sub(got) && got.t === 'state', 10_000, "the header's keyed topic sends the session");
  let item = state.items?.find((entry) => entry.key === session)?.item;
  if (!item) fail("the header's keyed topic sends the session", 'its state has no item for it');
  if (item.parentSessionId !== null) fail('a session the check made has no parent session', `it has ${item.parentSessionId}`);
  console.log("ok the header's keyed topic sends the session, with no parent session");

  // The dot: the session carries its run while the turn goes, then none.
  const changed = async (done: (item: Item) => boolean, ms: number, step: string) => {
    while (!done(item!)) item = (await frame((got) => sub(got) && got.t === 'change', ms, step)).item;
  };
  await changed((item) => item.activeRunId === run, 60_000, 'a working session carries its run');
  console.log('ok a working session carries its run (the column draws its dot)');
  await changed((item) => item.activeRunId === null, 120_000, "the run goes when the turn ends");
  console.log('ok the run goes from the session when the turn ends (the dot goes)');

  const page = await call('rows', 'page', { session, limit: 100 }, 'the first rows page loads');
  if (!Array.isArray(page.rows) || typeof page.hasBefore !== 'boolean' || !('activeRunId' in page))
    fail('the first rows page has rows, hasBefore and activeRunId', JSON.stringify(Object.keys(page)));
  if (page.activeRunId !== null) fail('the first page after the turn has no active run', `it has ${page.activeRunId}`);
  console.log(`ok the first rows page has ${page.rows.length} rows, hasBefore and no active run`);

  const tool = page.rows.find((row: any) => row.runId === run && row.kind === 'tool' && row.itemId && row.payload?.hasDetail);
  if (!tool) fail("the turn's hostname call is a tool row with detail", 'the page has none');
  const detail = await call('rows', 'tool', { session, item: tool.itemId }, 'an opened tool call reads its detail');
  if (!detail.output && !detail.input) fail('an opened tool call reads its detail', 'it has no input or output');
  console.log('ok an opened tool call reads its detail');

  // The drawer's Context tab: the files the session's tool calls changed, one each, as the page's tool rows have them.
  const files = await call('rows', 'files', { session }, "the Context tab reads the session's files");
  const edited = new Set<string>(
    page.rows
      .filter((row: any) => row.kind === 'tool' && row.payload?.path && row.payload.state !== 'failed'
        && ['edit', 'delete', 'move'].includes(row.payload.kind))
      .map((row: any) => row.payload.path),
  );
  const listed = Array.isArray(files) ? files.map((file: any) => file.path) : [];
  if (!Array.isArray(files) || listed.length !== edited.size || listed.some((path: string) => !edited.has(path)))
    fail("the Context tab lists each file the session's tool calls changed", `it listed ${JSON.stringify(files)}`);
  console.log(`ok the Context tab lists the ${listed.length} files the session's tool calls changed`);

  const sessions = await call('sessions', 'page', { limit: 50 }, 'the sessions column loads a page');
  if (!sessions.sessions?.some((listed: Item) => listed.sessionId === session))
    fail('the sessions column lists the session', 'its first page does not');
  console.log('ok the sessions column lists the session');
}

main()
  .then(() => process.exit(0))
  .catch((error) => {
    if (!(error instanceof Failed)) console.log(`fail the scenario ran: ${error.message}`);
    process.exit(1);
  });
