// Files and Search over the socket (docs/behavior/files.md): for a local session, files/list answers the checkout's
// top folder and files/search finds one of its files by name; for a session on Workbox Test, both say plainly that its
// files can't be shown here yet. The desktop serves files in every case, so this talks to it (CHECK4_API).
// node scripts/features/files.ts, with CHECK4_API, CHECK4_TOKEN, CHECK4_SESSION and CHECK4_MACHINE set
// (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session, CHECK4_MACHINE: machine } = process.env;

type Frame = { t: string; id?: number; message?: string; result?: { entries?: Entry[]; matches?: Entry[]; nextCursor?: number | null } };
type Entry = { path: string; name: string; isDirectory: boolean; excluded?: boolean };

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
const frame = () =>
  new Promise<Frame>((resolve) => {
    next = () => {
      if (!frames.length) return;
      next = undefined;
      resolve(frames.shift()!);
    };
    next();
  });
let id = 0;
async function call(method: string, args: object) {
  socket.send(JSON.stringify({ t: 'call', id: ++id, feature: 'files', method, args: { session, ...args } }));
  return await frame();
}

async function run() {
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = () => reject(new Error('the socket failed to open'));
  });
  socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
  if ((await frame()).t !== 'welcome') fail('hello is answered with welcome', 'it was not');

  const listed = await call('list', { dir: '', includeExcluded: true });
  if (machine !== 'local') {
    if (listed.t !== 'error' || !listed.message?.includes("can't show yet"))
      fail("a remote session's files say they can't be shown yet", JSON.stringify(listed));
    console.log("ok a remote session's files say they can't be shown yet");
    return;
  }
  const entries = listed.result?.entries ?? fail('the top folder is listed', JSON.stringify(listed));
  if (!entries.some((entry) => entry.name === '.git' && entry.excluded)) fail('.git is listed as excluded', JSON.stringify(entries));
  const file = entries.find((entry) => !entry.isDirectory && !entry.excluded) ?? fail('the top folder holds a file', JSON.stringify(entries));
  console.log(`ok the top folder is listed (${entries.length} entries)`);

  const found = await call('search', { query: file.name.toUpperCase(), cursor: 0, pageSize: 20 });
  if (!found.result?.matches?.some((match) => match.path === file.path))
    fail(`search finds ${file.path} by name, ignoring case`, JSON.stringify(found));
  console.log(`ok search finds ${file.path} by name, ignoring case`);

  const out = await call('list', { dir: '..' });
  if (out.t !== 'error') fail('a path outside the folder is refused', JSON.stringify(out));
  console.log('ok a path outside the folder is refused');
}

try {
  await run();
} catch (error) {
  if (!(error instanceof Failed)) console.log(`fail files: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
} finally {
  socket.close();
}
