// Files acceptance uses the case's real source-qualified owner, normal calls and bounded bulk IO.
// Only the explicitly prepared check4 disposable fixture is edited. No provider or remote connection is created here.
import { createHash } from 'node:crypto';
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session,
  CHECK4_MACHINE: machine, CHECK4_RUN: run } = process.env;
type Frame = { t: string; id?: number; result?: unknown; message?: string; code?: string };
type Source = { machine: string; generation: number; routeMachine: string | null };
type Owner = { machine: string; checkoutRoot: string; remoteSessionId: string };
type Entry = { path: string; name: string; isDirectory: boolean; excluded: boolean };
type Cursor = { generation: string; after: string };
type Listing = { entries: Entry[]; nextCursor: Cursor | null };
type Search = { searchId: string; names: Entry[]; content: { path: string; excerpt: string }[]; more: boolean; truncated: boolean };
type Chunk = { content: string; revision: string; size: number; binary: boolean; nextOffset: number | null };
type Saved = { state: string; revision: string | null; error: { code: string; message: string } | null };
class Failed extends Error {}
function assert(ok: unknown, step: string): asserts ok {
  if (!ok) throw new Failed(step);
}
const hash = (text: string) => createHash('sha256').update(text, 'utf8').digest('hex');
const sockets: Calls[] = [];
class Calls {
  socket: WebSocket;
  next = 0;
  pending = new Map<number, { resolve: (frame: Frame) => void; reject: (error: Error) => void }>();
  ready: Promise<void>;
  stop = () => {};
  constructor(bulk: boolean) {
    this.socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket${bulk ? '/bulk' : ''}`);
    sockets.push(this);
    this.ready = new Promise((resolve, reject) => {
      this.socket.onopen = () => this.socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
      this.socket.onmessage = event => {
        const frame = JSON.parse(String(event.data)) as Frame;
        if (frame.t === 'welcome') { resolve(); return; }
        if (frame.id !== undefined) {
          const call = this.pending.get(frame.id);
          this.pending.delete(frame.id);
          call?.resolve(frame);
        }
      };
      const closed = () => {
        const error = new Error('The Files scenario socket closed before completion.');
        reject(error);
        for (const call of this.pending.values()) call.reject(error);
        this.pending.clear();
      };
      this.stop = closed;
      this.socket.onclose = closed;
      this.socket.onerror = closed;
    });
  }
  async call(feature: string, method: string, args: object): Promise<Frame> {
    await this.ready;
    if (this.socket.readyState !== WebSocket.OPEN) throw new Error("The Files scenario socket is unavailable.");
    const id = ++this.next;
    return await new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try { this.socket.send(JSON.stringify({ t: 'call', id, feature, method, args })); }
      catch (error) { this.pending.delete(id); reject(error); }
    });
  }
  close() { this.stop(); this.socket.close(); }
}
function result<T>(frame: Frame, step: string): T {
  assert(frame.t === 'result' && frame.result !== undefined, `${step}: ${frame.code ?? frame.t} ${frame.message ?? ''}`);
  return frame.result as T;
}
function sameSource(a: Source, b: Source) {
  return a.machine === b.machine && a.generation === b.generation && a.routeMachine === b.routeMachine;
}
let normal: Calls | undefined;
let bulk: Calls | undefined;
let source: Source;
let owner: Owner;
let upload: string | undefined;
let searchId: string | undefined;
let snapshot: { path: string; revision: string } | undefined;
let expired = false;
let deadline: ReturnType<typeof setTimeout> | undefined;
const args = (value: object) => ({ session, expectedSource: source, expectedOwner: owner, ...value });
async function file<T>(connection: Calls, method: string, value: object): Promise<T> {
  return result<T>(await connection.call('files', method, args(value)), `files/${method}`);
}
async function read(path: string): Promise<{ content: string; revision: string }> {
  let offset = 0;
  let content = '';
  let revision: string | undefined;
  for (;;) {
    const part = await file<Chunk>(bulk!, 'read', { path, offset, revision: revision ?? null });
    assert(!part.binary && /^[a-f0-9]{64}$/.test(part.revision), 'Fixture reads as revisioned UTF-8 text.');
    revision ??= part.revision;
    snapshot = { path, revision };
    assert(part.revision === revision && part.size <= 32768, 'Bounded fixture snapshot retains its original revision.');
    content += part.content;
    assert(Buffer.byteLength(content, 'utf8') <= 32768, 'Fixture read remains bounded.');
    if (part.nextOffset === null) {
      snapshot = undefined; // The final part automatically releases the immutable snapshot.
      assert(Buffer.byteLength(content, 'utf8') === part.size && hash(content) === revision, 'Full fixture bytes match the real revision and size.');
      return { content, revision };
    }
    assert(part.nextOffset > offset && part.nextOffset === Buffer.byteLength(content, 'utf8'), 'Snapshot cursor advances by actual UTF-8 bytes.');
    offset = part.nextOffset;
  }
}
async function save(path: string, expectedRevision: string, text: string): Promise<Saved> {
  const bytes = Buffer.from(text, 'utf8');
  const begun = await file<{ upload: string }>(bulk!, 'beginSave', { path, expectedRevision, size: bytes.length });
  assert(typeof begun.upload === 'string' && begun.upload.length > 0, 'The actual owner allocates the save upload.');
  upload = begun.upload;
  const chunk = await file<{ accepted: boolean }>(bulk!, 'saveChunk', { upload, offset: 0, data: bytes.toString('base64') });
  assert(chunk.accepted === true, 'The scoped bulk upload accepts the exact fixture bytes.');
  const saved = await file<Saved>(bulk!, 'commitSave', { upload });
  upload = undefined; // Commit consumes the staged upload, including a revision conflict.
  return saved;
}
async function search(query: string, mode: 'name' | 'content', path: string) {
  let more = true;
  let found = false;
  while (more) {
    const page = await file<Search>(normal!, 'search', { query, mode, includeExcluded: true, searchId: searchId ?? null, pageSize: 100 });
    assert(typeof page.searchId === 'string' && !page.truncated, 'Search returns a bounded, complete fixture scan.');
    searchId = page.searchId;
    found ||= (mode === 'name' ? page.names : page.content).some(match => match.path === path);
    more = page.more;
  }
  await file(normal!, 'releaseSearch', { searchId });
  searchId = undefined;
  assert(found, `${mode} search finds the declared disposable fixture on its real owner.`);
}
async function runScenario() {
  assert(api && token && session && machine && run, 'check4 must supply the actual API, token, session, machine and run.');
  // Existing check4 setup creates this exclusive file under its case Git root, on the actual owning host.
  const path = process.env.CHECK4_FILE_RELATIVE;
  const baseline = process.env.CHECK4_FILE_CONTENT;
  assert(path && baseline !== undefined, 'check4 must explicitly prepare and declare its disposable Files fixture.');
  assert(/^assembly-check4-fixtures\/[A-Za-z0-9_-]+\.txt$/.test(path) && path.split('/').every(part => part && part !== '.' && part !== '..')
    && !path.includes('\\') && !path.includes('\0'), 'Only the declared task-owned disposable relative file may be edited.');
  assert(/^assembly check4 [A-Za-z0-9_-]+ fixture\n$/.test(baseline) && Buffer.byteLength(baseline, 'utf8') < 16000,
    'The declared baseline must be the exact bounded check4 case fixture content.');
  normal = new Calls(false);
  await normal.ready;
  const discovered = result<Source>(await normal.call('sessions', 'resolve_source', { routeMachine: machine === 'local' ? null : machine }), 'Resolve the case route');
  const descriptor = result<Source & { sessionId: string; readOnly: boolean; checkoutRoot: string | null; originalKey: { machine: string; id: string }; requestId: number }>(
    await normal.call('sessions', 'open_target', { kind: 'engine', machine: discovered.machine, session, requestId: 1 }), 'Validate the actual Engine session target');
  assert(sameSource(discovered, descriptor) && descriptor.sessionId === session && !descriptor.readOnly
    && descriptor.checkoutRoot && descriptor.originalKey.machine === discovered.machine && descriptor.originalKey.id === session
    && descriptor.requestId === 1, 'The descriptor must retain the original source and writable session identity.');
  source = { machine: descriptor.machine, generation: descriptor.generation, routeMachine: descriptor.routeMachine };
  owner = result<Owner>(await normal.call('files', 'owner', { session, expectedSource: source }), 'Resolve the real owning checkout');
  assert(owner.machine === source.machine && owner.checkoutRoot === descriptor.checkoutRoot && owner.remoteSessionId === descriptor.sessionId,
    'Files ownership matches the validated descriptor without inferring a root.');
  bulk = new Calls(true);
  await bulk.ready;
  const root = await file<Listing>(normal, 'list', { dir: '', includeExcluded: true });
  assert(root.entries.some(entry => entry.name === '.git' && entry.excluded), 'The real checkout lists .git as excluded.');
  const dir = path.slice(0, path.lastIndexOf('/'));
  let cursor: Cursor | null = null;
  let found = false;
  do {
    const page: Listing = await file<Listing>(normal, 'list', { dir, includeExcluded: true, cursor });
    found ||= page.entries.some(entry => entry.path === path && !entry.isDirectory);
    cursor = page.nextCursor;
  } while (cursor && !found);
  assert(found, 'The owning checkout lists the explicit disposable file.');
  console.log(`ok ${machine} source-bound owner and directory paging`);
  await search(path.slice(path.lastIndexOf('/') + 1).toUpperCase(), 'name', path);
  const original = await read(path);
  assert(original.content === baseline, 'The disposable fixture baseline must match exactly before any edit.');
  await search(baseline.trimEnd(), 'content', path);
  console.log(`ok ${machine} name/content search and full revisioned bulk read`);
  const outside = await normal.call('files', 'list', args({ dir: '..' }));
  assert(outside.t === 'error', 'Directory traversal must be refused.');
  const outsideRead = await bulk.call('files', 'read', args({ path: '../outside', offset: 0, revision: null }));
  assert(outsideRead.t === 'error', 'Bulk reads must refuse paths outside the owning checkout.');
  const stale = await bulk.call('files', 'read', { ...args({ path, offset: 0, revision: null }), expectedSource: { ...source, generation: source.generation + 1 } });
  assert(stale.t === 'error' && stale.code === 'source_changed', 'A stale source must be refused rather than silently rebound.');
  console.log(`ok ${machine} path and source refusal`);
  const edited = `${baseline}\nTSK-1649 saved ${run}\n`;
  const saved = await save(path, original.revision, edited);
  assert(saved.state === 'saved' && saved.revision === hash(edited), 'Atomic save must acknowledge the exact new fixture bytes.');
  const after = await read(path);
  assert(after.content === edited && after.revision === saved.revision, 'Readback must see all of our atomic replacement.');
  const conflict = await save(path, original.revision, `${baseline}\nstale write must never apply\n`);
  assert(conflict.state === 'conflict' && conflict.error?.code === 'revision_changed', 'A stale revision must conflict without overwriting the saved fixture.');
  const protectedFile = await read(path);
  assert(protectedFile.content === edited && protectedFile.revision === after.revision, 'The stale save preserves the confirmed file bytes.');
  // Restore only against our acknowledged revision. A concurrent writer causes a refusal, never an overwrite/retry.
  const restored = await save(path, saved.revision!, baseline);
  assert(restored.state === 'saved' && restored.revision === original.revision, 'CAS restore must match our exact saved version.');
  const final = await read(path);
  assert(final.content === baseline && final.revision === original.revision, 'The disposable fixture baseline is restored exactly.');
  console.log(`ok ${machine} atomic edit/save/readback, stale-revision refusal and CAS restore`);
}
try {
  deadline = setTimeout(() => { expired = true; for (const socket of sockets) socket.close(); }, 30000);
  await runScenario();
} catch (error) {
  console.log(`fail files: ${expired ? 'The owned scenario deadline expired.' : error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
} finally {
  try {
    if (!expired && bulk && upload) await file(bulk, 'dropSave', { upload });
    if (!expired && bulk && snapshot) await file(bulk, 'release', snapshot);
    if (!expired && normal && searchId) await file(normal, 'releaseSearch', { searchId });
  } catch { console.log('fail files: scoped resource cleanup was not acknowledged; closing both owning sockets.'); process.exitCode = 1; }
  for (const socket of sockets) socket.close();
  if (deadline) clearTimeout(deadline);
}
