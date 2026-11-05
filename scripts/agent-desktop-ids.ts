// Drives the running desktop through its accessibility tree with `agent-desktop`, finding every control by its id
// (desktop/src/core/ui/access/control_ids.rs), never by a snapshot ref, which changes with each snapshot. It reads the
// real tree, so it is also the check that the ids and roles reach assistive technology:
//   node scripts/agent-desktop-ids.ts            runs the walk below and exits 1 on the first control it can't find
// The app must already be running with accessibility on (DISPLAY and DBUS_SESSION_BUS_ADDRESS set, and
// org.a11y.Status IsEnabled true); AGENT_DESKTOP names the binary (default `agent-desktop`). Use it only on the
// Assembly window: every command carries --app assembly-desktop.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const APP = 'assembly-desktop';
const AGENT_DESKTOP = process.env.AGENT_DESKTOP ?? 'agent-desktop';

type Bounds = { x: number; y: number; width: number; height: number };
export type Node = { role: string; name?: string; ref_id?: string; states?: string[]; native_id?: { value?: string }; bounds?: Bounds; children?: Node[] };
type Answer = { ok: boolean; data?: unknown; error?: { code: string; message: string } };

function run(args: string[]): Answer {
  try {
    return JSON.parse(execFileSync(AGENT_DESKTOP, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
  } catch (error) {
    const out = (error as { stdout?: string }).stdout;
    if (out) return JSON.parse(out);
    throw error;
  }
}

/** Every node of a snapshot, flattened, with the id each carries. */
export function flatten(tree: Node): Node[] {
  return [tree, ...(tree.children ?? []).flatMap(flatten)];
}

export function nodeWithId(tree: Node, id: string): Node | undefined {
  return flatten(tree).find((node) => node.native_id?.value === id);
}

/** The tree of the Assembly window: its controls (`-i`), or every node, text included, when `all`. */
function snapshot(bounds = false, all = false): Node {
  const answer = run(['snapshot', '--app', APP, ...(all ? [] : ['-i', '--compact']), ...(bounds ? ['--include-bounds'] : [])]);
  if (!answer.ok) throw new Error(`snapshot: ${answer.error?.code} ${answer.error?.message}`);
  return (answer.data as { tree: Node }).tree;
}

/** Waits for the control with `id` to be in the tree (polling the tree, not sleeping a fixed time). */
async function waitFor(id: string, present = true, all = false, seconds = 15): Promise<Node | undefined> {
  const until = Date.now() + seconds * 1000;
  for (;;) {
    const node = nodeWithId(snapshot(false, all), id);
    if (Boolean(node) === present) return node;
    if (Date.now() > until) throw new Error(`${id} ${present ? 'never appeared' : 'never went away'}`);
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}

/** Presses the real pointer on the middle of `id`'s bounds (from this window's position), for a control that offers
 * no click action to the tree (a popover's trigger) or that takes focus only from a pointer (a pane). */
function press(id: string, tree: Node): void {
  const node = nodeWithId(tree, id);
  const window = (run(['list-windows', '--app', APP]).data as { bounds: Bounds }[])[0]?.bounds;
  if (!node?.bounds || !window) throw new Error(`${id} has no bounds to press`);
  const x = Math.round(window.x + node.bounds.x + node.bounds.width / 2);
  const y = Math.round(window.y + node.bounds.y + node.bounds.height / 2);
  // The pointer moves there first: the window hit-tests a click against where the pointer last was.
  run(['--headed', 'mouse-move', `--xy=${x},${y}`]);
  const answer = run(['--headed', 'mouse-click', `--xy=${x},${y}`]);
  if (!answer.ok) throw new Error(`press ${id}: ${answer.error?.code} ${answer.error?.message}`);
}

/** Presses the pointer at a place in the window: `x` from its left edge, `y` from its top (or from its bottom when
 * negative), for a pane whose controls are not in the tree until the pane has focus. */
function pressWindow(x: number, y: number): void {
  const window = (run(['list-windows', '--app', APP]).data as { bounds: Bounds }[])[0]?.bounds;
  if (!window) throw new Error('no window to press in');
  const at = `--xy=${Math.round(window.x + x)},${Math.round(window.y + (y < 0 ? window.height + y : y))}`;
  run(['--headed', 'mouse-move', at]);
  const answer = run(['--headed', 'mouse-click', at]);
  if (!answer.ok) throw new Error(`press ${at}: ${answer.error?.code} ${answer.error?.message}`);
}

/** Finds `id` in a fresh snapshot and clicks it with that snapshot's ref; `headed` presses the pointer instead. */
async function click(id: string, headed = false): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    const node = await waitFor(id);
    if (headed) return press(id, snapshot(true));
    const answer = run(['click', node!.ref_id!]);
    if (answer.ok) return;
    // A window that is still laying out refuses a click as unstable; look again.
    if (attempt >= 4) throw new Error(`click ${id}: ${answer.error?.code} ${answer.error?.message}`);
    await new Promise((resolve) => setTimeout(resolve, 400));
  }
}

/** Clicks the first node with `role` and `name`, for the kit's controls that carry a name but no id. */
async function clickNamed(role: string, name: string, seconds = 15): Promise<void> {
  const until = Date.now() + seconds * 1000;
  for (;;) {
    const node = flatten(snapshot()).find((node) => node.role === role && node.name === name);
    if (node?.ref_id && run(['click', node.ref_id]).ok) return;
    if (Date.now() > until) throw new Error(`no ${role} named ${name} to click`);
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}

/** Presses the tab `id` until it says it is picked (a press that lands while the window is still laying out is lost). */
async function pick(id: string): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    await click(id, true);
    const until = Date.now() + 3000;
    while (Date.now() < until) {
      if (nodeWithId(snapshot(), id)?.states?.includes('selected')) return;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    if (attempt >= 3) throw new Error(`${id} never became picked`);
  }
}

/** Waits for `id` (a pane's controls come and go as focus moves), then checks its role and, when given, its name. */
async function expect(id: string, role: string, name?: string, all = false): Promise<void> {
  const node = await waitFor(id, true, all);
  if (node!.role !== role) throw new Error(`${id} is a ${node!.role}, expected ${role}`);
  if (name !== undefined && node!.name !== name) throw new Error(`${id} is named ${JSON.stringify(node!.name)}, expected ${JSON.stringify(name)}`);
  console.log(`ok  ${id.padEnd(34)} ${role.padEnd(10)} ${node!.name ?? ''}`);
}

async function main(): Promise<void> {
  run(['focus-window', '--app', APP]);
  // The tree holds the controls of the pane that has focus (the kit's panels publish only that one). The chat is
  // focused by pressing its composer's place (the saved layout decides what else is open); a column's tab, once it is
  // picked, takes the column only after another tab was picked, so the sessions column is reached through the Files tab.
  pressWindow(1000, -72);
  await waitFor('composer.input');
  await expect('topbar.home', 'button', 'Home');
  await expect('topbar.board', 'button', 'Board');
  await expect('tabs.chat', 'tab', 'Chat');
  await expect('composer.input', 'textfield', 'Message');
  await expect('composer.send', 'button', 'Send message');
  // A tab that is already picked does not take focus from another pane, so pick the next tab and come back.
  for (let attempt = 0; ; attempt++) {
    await pick('side-tabs.files');
    await pick('side-tabs.sessions');
    try {
      await waitFor('sessions.new-session', true, false, 4);
      break;
    } catch (error) {
      if (attempt >= 2) throw error;
    }
  }
  await expect('side-tabs.sessions', 'tab', 'Sessions');
  await expect('side-tabs.files', 'tab', 'Files');
  await expect('sessions.new-session', 'button', 'New session');
  await expect('sessions.view-options', 'button', 'View options');
  await expect('sessions.filter.provider', 'button');
  await expect('sessions.filter.status', 'button');
  await expect('utility.settings', 'button', 'Settings');
  await expect('utility.usage', 'button', 'Usage');
  await expect('sessions.empty', 'statictext', 'No sessions here.', true);

  // Settings opens in its own tab; its sidebar is the kit's, whose entries carry names rather than ids. Connections,
  // then back to Chat.
  await click('utility.settings');
  await waitFor('tabs.settings');
  await clickNamed('treeitem', 'Connections');
  await waitFor('settings.machine-save');
  await expect('settings.machine-name', 'textfield', 'Machine name');
  await expect('settings.machine.local.name', 'statictext', 'This Mac', true);
  await pick('tabs.chat');

  // The Build Board.
  await click('topbar.board', true);
  await waitFor('board.tab.workflows');
  await expect('board.summary', 'statictext', '0 running · 0 waiting · 0 done today', true);
  await expect('board.tab.needs-you', 'tab', 'Needs you');
  await click('topbar.board', true);
  console.log('walked the main screen, Settings > Connections and the Board by id');
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main().catch((error) => {
    console.error(`FAIL ${(error as Error).message}`);
    process.exit(1);
  });
}
