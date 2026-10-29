// Worktrees and Source control over the socket (docs/behavior/scm.md): for the case's session, worktrees/list answers
// its folder's worktrees with the main checkout first (scm#1), git/status its branch and changed files (scm#9), and
// git/diff a file's staged and working tree diffs (scm#14), and git/branchDiff the branch against its base, or why it
// has none (scm#18, scm#19).
// A session on another machine is refused with why, until the desktop forwards calls to it (scm#7, plan C2).
// node scripts/features/scm.ts, with CHECK4_API, CHECK4_TOKEN, CHECK4_SESSION and CHECK4_MACHINE set
// (scripts/features/README.md). The token goes in the hello frame, never in argv or output.
const { CHECK4_API: api, CHECK4_TOKEN: token, CHECK4_SESSION: session, CHECK4_MACHINE: machine } = process.env;

type Frame = { t: string; id?: number; result?: Record<string, unknown>; message?: string };

let failed = false;
const step = (ok: boolean, name: string, why: string) => {
  console.log(ok ? `ok ${name}` : `fail ${name}: ${why}`);
  failed ||= !ok;
};

const socket = new WebSocket(`${api!.replace(/^http/, 'ws')}/socket`);
const waiting = new Map<number, (frame: Frame) => void>();
let welcomed: (frame: Frame) => void = () => {};
socket.onmessage = (event) => {
  const frame: Frame = JSON.parse(String(event.data));
  if (frame.t === 'welcome') welcomed(frame);
  else if (frame.id !== undefined) waiting.get(frame.id)?.(frame);
};
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = () => reject(new Error('the socket failed to open'));
});
const welcome = new Promise<Frame>((resolve) => (welcomed = resolve));
socket.send(JSON.stringify({ t: 'hello', token, protocol: 1 }));
await welcome;

let next = 0;
function call(feature: string, method: string, args: Record<string, unknown> = {}): Promise<Frame> {
  const id = ++next;
  const answer = new Promise<Frame>((resolve) => waiting.set(id, resolve));
  socket.send(JSON.stringify({ t: 'call', id, feature, method, args: { session, ...args } }));
  return answer;
}

const worktrees = await call('worktrees', 'list');
const status = await call('git', 'status');
const diff = await call('git', 'diff', { path: 'README' });
const branch = await call('git', 'branchDiff');
if (machine === 'local') {
  const list = (worktrees.result?.worktrees ?? []) as { isMain: boolean }[];
  step(worktrees.t === 'result' && list[0]?.isMain === true, 'worktrees/list answers the main checkout first',
    worktrees.message ?? `got ${JSON.stringify(worktrees.result)}`);
  step(status.t === 'result' && Array.isArray(status.result?.files) && 'branch' in status.result!,
    'git/status answers the branch and changed files', status.message ?? `got ${JSON.stringify(status.result)}`);
  step(diff.t === 'result' && typeof diff.result?.unstaged === 'string' && typeof diff.result?.staged === 'string',
    'git/diff answers a file\'s staged and working tree diffs', diff.message ?? `got ${JSON.stringify(diff.result)}`);
  step((branch.t === 'result' && typeof branch.result?.base === 'string' && typeof branch.result?.diff === 'string')
    || (branch.t === 'error' && branch.message!.includes('origin/HEAD')),
    'git/branchDiff answers the branch against its base, or says there is none', branch.message ?? `got ${branch.t}`);
} else {
  step(worktrees.t === 'error' && worktrees.message!.includes(`on ${machine}`),
    `worktrees/list says the session's folder is on ${machine}`, worktrees.message ?? `got ${worktrees.t}`);
  step(status.t === 'error' && status.message!.includes(`on ${machine}`),
    `git/status says the session's folder is on ${machine}`, status.message ?? `got ${status.t}`);
  step(diff.t === 'error' && diff.message!.includes(`on ${machine}`),
    `git/diff says the session's folder is on ${machine}`, diff.message ?? `got ${diff.t}`);
  step(branch.t === 'error' && branch.message!.includes(`on ${machine}`),
    `git/branchDiff says the session's folder is on ${machine}`, branch.message ?? `got ${branch.t}`);
}
socket.close();
process.exit(failed ? 1 : 0);
