// The driver's input layer: finds the window's controls by their accessibility id (desktop/src/core/ui/access/
// control_ids.rs) through `agent-desktop`, never by a snapshot ref, which changes with each snapshot. The app must be
// running with accessibility on (DISPLAY and DBUS_SESSION_BUS_ADDRESS set, org.a11y.Status IsEnabled true; bench.sh up
// does this). AGENT_DESKTOP names the binary (default `agent-desktop`). Every command carries --app assembly-desktop.
import { execFileSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const APP = 'assembly-desktop';
const AGENT_DESKTOP = process.env.AGENT_DESKTOP ?? 'agent-desktop';

type Bounds = { x: number; y: number; width: number; height: number };
export type Node = { role: string; name?: string; ref_id?: string; states?: string[]; native_id?: { value?: string }; bounds?: Bounds; children?: Node[] };
type Answer = { ok: boolean; data?: unknown; error?: { code: string; message: string } };

export function run(args: string[]): Answer {
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
export function snapshot(bounds = false, all = false): Node {
  const answer = run(['snapshot', '--app', APP, ...(all ? [] : ['-i', '--compact']), ...(bounds ? ['--include-bounds'] : [])]);
  if (!answer.ok) throw new Error(`snapshot: ${answer.error?.code} ${answer.error?.message}`);
  return (answer.data as { tree: Node }).tree;
}

/** Waits for the control with `id` to be in the tree (or gone), looking at the tree again until the deadline. */
export async function waitFor(id: string, present = true, all = false, seconds = 15): Promise<Node | undefined> {
  const until = Date.now() + seconds * 1000;
  for (;;) {
    const node = nodeWithId(snapshot(false, all), id);
    if (Boolean(node) === present) return node;
    if (Date.now() > until) throw new Error(`${id} ${present ? 'never appeared' : 'never went away'} within ${seconds} s`);
    await delay(250);
  }
}

function windowBounds(): Bounds {
  const window = (run(['list-windows', '--app', APP]).data as { bounds: Bounds }[] | undefined)?.[0]?.bounds;
  if (!window) throw new Error('no Assembly window');
  return window;
}

function pressAt(x: number, y: number): void {
  const at = `--xy=${Math.round(x)},${Math.round(y)}`;
  // The pointer moves there first: the window hit-tests a click against where the pointer last was.
  run(['--headed', 'mouse-move', at]);
  const answer = run(['--headed', 'mouse-click', at]);
  if (!answer.ok) throw new Error(`press ${at}: ${answer.error?.code} ${answer.error?.message}`);
}

/** Presses the real pointer on the middle of `id`'s bounds, for a control that offers no click action to the tree (a
 * popover's trigger) or that takes focus only from a pointer (a pane). */
export function press(id: string, tree: Node): void {
  const node = nodeWithId(tree, id);
  const window = windowBounds();
  if (!node?.bounds) throw new Error(`${id} has no bounds to press`);
  pressAt(window.x + node.bounds.x + node.bounds.width / 2, window.y + node.bounds.y + node.bounds.height / 2);
}

/** Presses the pointer at a place in the window: `x` from its left edge, `y` from its top (from its bottom when
 * negative), for a pane whose controls are not in the tree until the pane has focus. */
export function pressWindow(x: number, y: number): void {
  const window = windowBounds();
  pressAt(window.x + x, window.y + (y < 0 ? window.height + y : y));
}

/** Finds `id` in a fresh snapshot and clicks it with that snapshot's ref; `headed` presses the pointer instead. */
export async function click(id: string, headed = false): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    const node = await waitFor(id);
    if (headed) return press(id, snapshot(true));
    const answer = run(['click', node!.ref_id!]);
    if (answer.ok) return;
    // A window that is still laying out refuses a click as unstable; look again.
    if (attempt >= 4) throw new Error(`click ${id}: ${answer.error?.code} ${answer.error?.message}`);
    await delay(400);
  }
}

/** Presses the tab `id` until it says it is picked (a press that lands while the window is still laying out is lost). */
export async function pick(id: string): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    await click(id, true);
    const until = Date.now() + 3000;
    while (Date.now() < until) {
      if (nodeWithId(snapshot(), id)?.states?.includes('selected')) return;
      await delay(250);
    }
    if (attempt >= 3) throw new Error(`${id} never became picked`);
  }
}

/** Puts `text` into the field `id`. */
export async function type(id: string, text: string): Promise<void> {
  const node = await waitFor(id);
  const answer = run(['type', node!.ref_id!, text]);
  if (!answer.ok) throw new Error(`type into ${id}: ${answer.error?.code} ${answer.error?.message}`);
}

/** Gives the chat pane focus, so its composer is in the tree (the kit's panels publish only the focused pane): the one
 * pointer press at a place, the composer's, 72 px above the window's bottom. */
export async function focusChat(): Promise<void> {
  pressWindow(1000, -72);
  await waitFor('composer.input');
}

/** Gives the sessions column focus, so its rows are in the tree. A tab that is already picked does not take focus from
 * another pane, so the Files tab is picked first and then Sessions. */
export async function focusSessions(): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    await pick('side-tabs.files');
    await pick('side-tabs.sessions');
    try {
      await waitFor('sessions.new-session', true, false, 4);
      return;
    } catch (error) {
      if (attempt >= 2) throw error;
    }
  }
}

export function focusWindow(): void {
  run(['focus-window', '--app', APP]);
}
