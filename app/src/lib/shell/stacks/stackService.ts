/**
 * stackService.ts — the ONLY place run configurations talk to the rest of the app.
 *
 * On screen this feature is called "Run" and one saved entry is a "run
 * configuration". The internal stack vocabulary predates that product name;
 * see the note at the top of `stackStore.svelte.ts`.
 *
 * Two kinds of outside world, kept apart on purpose:
 *
 * 1. **Reading.** `list_runtime_contexts` tells us which processes are listening
 *    on a port and which folder each was started in. That is the whole of this
 *    lane's backend surface, and it is read at exactly three moments: when the
 *    panel is opened, when the user presses refresh, and when a terminal ends.
 *    NOTHING polls.
 * 2. **Starting and stopping.** Spawning a terminal and killing one belong to the
 *    page — it owns the session rail, the terminal service and the terminal
 *    hosts. So this service does not do either: the page REGISTERS three
 *    handlers here (`registerStackHandlers`), and this service calls them and
 *    keeps the bookkeeping. The page's side of that is `registerStackHandlers`
 *    in its `onMount`.
 *
 * Every backend call is counted with `countInvoke('<command name>')` right
 * before it, so the dev invoke counter stays honest. A `null` result from a
 * `…FromTauri` wrapper means "not running in the desktop app" — nothing was
 * invoked, and the panel says so rather than showing made-up data.
 */
import { countInvoke } from '../devInvokeCounter.svelte.ts';
import {
  listRuntimeContextsFromTauri,
  readAssemblySettingFromTauri,
  writeAssemblySettingFromTauri
} from '../../tauriSource.ts';
import {
  applyStackProcesses,
  beginStackLoad,
  commandWithEnv,
  configureStackPersistence,
  failStackLoad,
  folderName,
  forgetStackRun,
  hydrateStacks,
  markStacksActivated,
  markStacksUnavailable,
  ownedIdForStack,
  recordStackExit,
  recordStackStart,
  setStackInput,
  stackIdForOwnedId,
  stacks,
  type StackDefinition,
  type StackProcess
} from './stackStore.svelte.ts';

configureStackPersistence({
  read: readAssemblySettingFromTauri,
  write: writeAssemblySettingFromTauri
});

/** Shown when the process list only exists inside the desktop app. */
const DESKTOP_ONLY = 'Running processes can only be read in the desktop app.';

/**
 * What the panel asks the page to do when the user presses Start.
 *
 * The page answers by making a fresh session in `cwd`, spawning its terminal
 * with `script` as the command it runs, and handing back the session's
 * `ownedId` — or `null` when no terminal could be opened, which the panel
 * reports as a plain sentence.
 */
export interface StackStartRequest {
  /** The saved stack being started. */
  stackId: string;
  /** The folder the command runs in. */
  cwd: string;
  /** The command line to run, exactly as the user typed it. */
  script: string;
  /** What the session should be called in the rail — the stack's name. */
  title: string;
}

/** The three things the page does on the stack runner's behalf. */
export interface StackHandlers {
  /** Make a session, start `script` in it, answer with its `ownedId`. */
  onStartStack(request: StackStartRequest): Promise<string | null>;
  /** End that session's terminal (the page's `closeTerminal`). */
  onStopStack(ownedId: string): Promise<void>;
  /** Put that session on screen (the page's `selectOwned`). */
  onSelectSession(ownedId: string): void | Promise<void>;
}

let handlers: Partial<StackHandlers> = {};

/**
 * Hand the service the page's three handlers. Called once, from the page's
 * `onMount`, after the terminal service exists. Calling it again replaces them.
 */
export function registerStackHandlers(next: Partial<StackHandlers>): void {
  handlers = { ...handlers, ...next };
}

/** Drop the handlers — the page's teardown, and the tests. */
export function clearStackHandlers(): void {
  handlers = {};
}

/** What the last `activate` was pointed at, so a repeat call costs nothing. */
let loadedRootKey: string | null = null;

/** Stacks whose Start is still in flight, so a second click cannot double-spawn. */
const startsInFlight = new Set<string>();

/** Stacks whose Stop is still in flight, for the same reason. */
const stopsInFlight = new Set<string>();

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Show the panel and read the listening processes.
 *
 * Idempotent: opening the view again with the same project does no backend work
 * at all. The saved stacks are read out of storage on the first call — that is
 * storage, not the backend, and it is what makes the list appear at all.
 */
export function activateStacks(input: { activeRoot: string | null; projectName?: string }): void {
  const key = (input.activeRoot ?? '').trim();
  setStackInput(input);
  const firstTime = !stacks.activated;
  markStacksActivated();
  void activateStacksForKey(key, firstTime);
}

async function activateStacksForKey(key: string, firstTime: boolean): Promise<void> {
  try {
    await hydrateStacks();
    if ((stacks.activeRoot ?? '').trim() !== key) return;
    if (!firstTime && key === loadedRootKey) return;
    loadedRootKey = key;
    await refreshStacks();
  } catch (error) {
    stacks.error = `Could not load run configurations: ${describeError(error)}`;
  }
}

/** Forget which project was last read — used when the shell tears the panel down. */
export function resetStackActivation(): void {
  loadedRootKey = null;
}

/**
 * Read the listening processes again. The panel's refresh button, and what every
 * start, stop and terminal exit ends with.
 */
export async function refreshStacks(): Promise<void> {
  const ticket = beginStackLoad();
  const projects = projectsToScan();
  if (projects.length === 0) {
    // Nothing saved yet: there is no folder to ask about, and asking about none
    // would come back empty anyway.
    applyStackProcesses(ticket, []);
    return;
  }
  try {
    countInvoke('list_runtime_contexts');
    const contexts = await listRuntimeContextsFromTauri(projects);
    if (contexts === null) {
      markStacksUnavailable(ticket, DESKTOP_ONLY);
      return;
    }
    applyStackProcesses(
      ticket,
      contexts.map(
        (context): StackProcess => ({
          pid: context.pid,
          port: context.port,
          command: context.command,
          cwd: context.cwd
        })
      )
    );
  } catch (error) {
    failStackLoad(ticket, `Could not read running processes: ${describeError(error)}`);
  }
}

/**
 * Start a saved run configuration: ask the page for a session running its
 * command, tag that session as this configuration's, then read the ports again.
 *
 * The port read happens straight away and will usually still show "started, no
 * port yet" — a dev server takes a second or two to bind. That is deliberate:
 * the alternative is a timer, and this lane does not poll. The refresh button
 * (and the row's own click) is how the user asks again.
 *
 * The environment variables are folded into the command line HERE rather than
 * anywhere further in, so nothing downstream has to learn about them: the page
 * gets one command line and spawns it exactly as it always has, and the honest
 * exit code that comes back still belongs to the command the user asked for.
 */
export async function startStack(stackId: string): Promise<void> {
  if (startsInFlight.has(stackId)) return;
  const definition = definitionFor(stackId);
  if (!definition) return;
  if (!handlers.onStartStack) {
    stacks.error = 'The shell has not wired up starting run configurations yet.';
    return;
  }
  startsInFlight.add(stackId);
  stacks.error = null;
  try {
    const ownedId = await handlers.onStartStack({
      stackId: definition.id,
      cwd: definition.cwd,
      script: commandWithEnv(definition.script, definition.env),
      title: definition.name
    });
    if (!ownedId) {
      stacks.error = `Could not start "${definition.name}": no terminal opened.`;
      return;
    }
    await recordStackStart(definition.id, ownedId);
    await refreshStacks();
  } catch (error) {
    stacks.error = `Could not start "${definition.name}": ${describeError(error)}`;
  } finally {
    startsInFlight.delete(stackId);
  }
}

/**
 * Stop a running stack: end the terminal its session is running in, then read
 * the ports again. The session STAYS in the rail as a finished row — ending a
 * terminal is not the same as throwing the work away.
 */
export async function stopStack(stackId: string): Promise<void> {
  if (stopsInFlight.has(stackId)) return;
  const definition = definitionFor(stackId);
  const ownedId = ownedIdForStack(stackId);
  if (!ownedId) return;
  if (!handlers.onStopStack) {
    stacks.error = 'The shell has not wired up stopping run configurations yet.';
    return;
  }
  stopsInFlight.add(stackId);
  stacks.error = null;
  try {
    await handlers.onStopStack(ownedId);
    // The page's close writes the session's own state; the run record is marked
    // ended here so the row stops claiming a terminal that is gone. A close is
    // the user's doing, so it counts as a clean end, not a failure.
    await recordStackExit(ownedId, { exitCode: 0, signal: null });
    await refreshStacks();
  } catch (error) {
    stacks.error = `Could not stop "${definition?.name ?? 'this run configuration'}": ${describeError(error)}`;
  } finally {
    stopsInFlight.delete(stackId);
  }
}

/**
 * A terminal ended. The page fans EVERY terminal exit through here; the ones
 * that are not a stack's are ignored and cost nothing.
 *
 * This is the whole reason the panel needs no timer: a stack that crashes shows
 * its exit code the moment the process dies.
 */
export function noteTerminalExit(
  ownedId: string,
  outcome: { exitCode: number | null; signal: string | null }
): void {
  void recordTerminalExit(ownedId, outcome);
}

/** A session was removed from the rail: its stack tag goes with it. */
export function noteSessionRemoved(ownedId: string): void {
  if (stackIdForOwnedId(ownedId) === null) return;
  void forgetRemovedStackSession(ownedId);
}

async function recordTerminalExit(
  ownedId: string,
  outcome: { exitCode: number | null; signal: string | null }
): Promise<void> {
  try {
    const recorded = await recordStackExit(ownedId, outcome);
    if (recorded) await refreshStacks();
  } catch (error) {
    stacks.error = `Could not save the run result: ${describeError(error)}`;
  }
}

async function forgetRemovedStackSession(ownedId: string): Promise<void> {
  try {
    await forgetStackRun(ownedId);
  } catch (error) {
    stacks.error = `Could not forget the run session: ${describeError(error)}`;
  }
}

/** Put a stack's session on screen — the row's click-through. */
export async function selectStackSession(ownedId: string): Promise<void> {
  if (!handlers.onSelectSession) return;
  await handlers.onSelectSession(ownedId);
}

/** Is this stack's Start or Stop still waiting on the page? */
export function isStackBusy(stackId: string): boolean {
  return startsInFlight.has(stackId) || stopsInFlight.has(stackId);
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function definitionFor(stackId: string): StackDefinition | null {
  return stacks.definitions.find((definition) => definition.id === stackId) ?? null;
}

/**
 * The folders to ask the backend about: one per distinct saved-stack folder.
 *
 * `list_runtime_contexts` DROPS every process whose folder is not underneath one
 * of the folders it is given, so this list is what makes a stack's ports visible
 * at all. It is built from the saved stacks rather than from the session rail on
 * purpose: a stack in a folder nobody has a session open in still has ports.
 *
 * Plain objects, not the runes proxies: a proxy cannot cross the Tauri boundary.
 */
function projectsToScan(): { id: string; name: string; path: string }[] {
  const paths = new Set<string>();
  for (const definition of stacks.definitions) {
    const path = definition.cwd.trim();
    if (path) paths.add(path);
  }
  return [...paths].map((path) => ({ id: path, name: folderName(path) || path, path }));
}
