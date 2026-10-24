/**
 * panelActivation.ts — when each /next panel is allowed to load its data.
 *
 * PURE: no backend call, no Svelte, no DOM. It only decides *whether* and
 * *with which project* a panel's loader may run; the loaders themselves are
 * handed in (see `shellPanels.ts` for the real ones).
 *
 * The rule the shell is built on is "nothing loads at launch", and there are
 * two different launch noises to keep out:
 *
 * 1. **The center dock announcing a tab.** Building or restoring the tab area
 *    reports an active tab, and that report is indistinguishable from a click.
 *    So tab loads stay switched off until `allowPanelLoads()` is called, which
 *    the page does one timer tick after the frame is ready — every one of
 *    dockview's own announcements is delivered by then (they travel on
 *    microtasks, which all drain before the first timer callback). What the
 *    announcement still does, even switched off, is tell this file the tab is
 *    on screen: the tab loads nothing at launch, and the session picked next is
 *    what points it at a project.
 * 2. **Start-up re-attaching a terminal.** Re-attaching a session that survived
 *    a reload selects it, which looks exactly like the user picking it. So
 *    session loads stay switched off until `allowSessionLoads()` is called,
 *    which the page does when start-up has finished.
 *
 * After that, loading is driven by user actions only: bringing a tab to the
 * front, picking a session, and opening a view of the tool column. A panel that
 * has never been shown never loads, and changing session re-loads only the
 * panels the user has actually opened — and of those, only the ones the change
 * tells something new. A panel is already showing the folder the session you
 * just picked is in when that session shares a project or a checkout with the
 * one you left, and reading it again would throw the panel's work away and
 * rebuild the same answer. So each panel remembers the folder it was last
 * loaded for, and a pick that does not change it loads nothing.
 *
 * Four panels are neither a tab nor always on screen: the **file tree**,
 * **source control**, the **worktree manager** and the **stacks pane**. All four
 * live in the tool column on the right, as views its icon strip switches
 * between, and the shell opens on none of them. So all four follow the same principle
 * by a third route — they load when you can actually see them.
 * Picking a session loads one only while it is in view, and opening one loads it
 * if a session is already picked. Out of view they cost nothing; in view they
 * are never stale.
 *
 * What counts as "in view" is not decided here — the tool column knows both
 * halves of it (which view is open, and whether the pane inside is folded) and
 * reports the answer as one yes or no, per view.
 *
 * The **Problems panel** in the bottom dock has the same route and a harder
 * problem: the dock is on screen from the moment the shell opens, so "in view"
 * cannot mean "mounted" there. `problemsVisible(true)` must only ever be
 * reported from something the user did. Nothing in the shell reports it today —
 * the panel's own Refresh button is the only way in — but the route is here and
 * tested, so the day the dock grows tabs or a fold, one call is all it takes.
 */

/** A project folder, in the shape the backend's project-scoped commands want. */
export interface PanelProject {
  id: string;
  name: string;
  path: string;
}

/** What the shell is pointed at right now. */
export interface ProjectSelection {
  /** Folder of the session the user is looking at; `''` when there is none. */
  root: string;
  /** Every project folder the shell knows about, for the machine-wide cards. */
  projects: PanelProject[];
}

/** The loaders, one per panel. Each is safe to call more than once. */
export interface PanelActivators {
  editor(root: string | null): void;
  git(root: string | null): void;
  browser(): void;
  explorer(root: string | null, checkoutDeleted: boolean): void;
  worktrees(selection: ProjectSelection): void;
  stacks(root: string | null): void;
  problems(root: string | null): void;
}

export interface PanelActivation {
  /** Start honouring tab activations (one timer tick after the frame is ready). */
  allowPanelLoads(): void;
  /** Start honouring session picks (once start-up has finished). */
  allowSessionLoads(): void;
  /** Whether a session pick would be honoured right now. The page asks before
   * re-opening a session's files, so start-up re-attaching a terminal cannot
   * make the editor read files while everything else is still switched off. */
  loadsAllowed(): boolean;
  /** A center tab came to the front. */
  panelShown(id: string): void;
  /** The user picked a session in the rail. */
  sessionPicked(rootAvailable?: boolean): void;
  /** Files came into view, or went out of it. Same contract as source control. */
  filesVisible(visible: boolean): void;
  /** Source control came into view, or went out of it. Report where it stands
   * now; the tool column reports it once at start-up too, so this is never
   * guesswork. */
  sourceControlVisible(visible: boolean): void;
  /** The worktree manager came into view, or went out of it. Same contract. */
  worktreesVisible(visible: boolean): void;
  /** The stacks pane came into view, or went out of it. Same contract. */
  stacksVisible(visible: boolean): void;
  /** The Problems panel came into view, or went out of it. Same contract as
   * source control above — but see the note at the top of this file: the bottom
   * dock is on screen at launch, so this may only ever be called for something
   * the user did. */
  problemsVisible(visible: boolean): void;
  /** Which panels have loaded at least once — for tests and for the report. */
  loadedPanels(): string[];
}

/** Center tabs that have something to load. "session" is the terminal: it is
 * owned by the page's own start-up and must never be re-loaded from here.
 *
 * Source control is deliberately absent: it is a view of the tool column, not a
 * tab, so nothing ever brings it to the front. Its own rule is in
 * `loadSourceControl` below. */
const LOADABLE_PANELS = new Set(['editor', 'browser']);

export function createPanelActivation(
  activators: PanelActivators,
  readSelection: () => ProjectSelection
): PanelActivation {
  /** The center tab that is actually visible. Hidden tabs do not follow session
   * roots: they are re-pointed when they come back to the front. */
  let activePanelId: string | null = null;
  let panelLoadsAllowed = false;
  let sessionLoadsAllowed = false;
  let sessionPanelsShown = false;
  let selectedRootAvailable = true;
  /** Can the user see the file tree right now? */
  let filesInView = false;
  /** The folder the file tree was last loaded for. */
  let filesLoadedFor: string | null = null;
  /** Can the user see source control right now? */
  let sourceControlInView = false;
  /** The folder source control was last loaded for, or `null` if it never has
   * been. `''` is a real value here — it means "loaded, for no folder". */
  let gitLoadedFor: string | null = null;
  /** Can the user see the worktree manager right now? */
  let worktreesInView = false;
  /** Same bookkeeping as `gitLoadedFor`, for the worktree manager. */
  let worktreesLoadedFor: string | null = null;
  /** Can the user see the stacks pane right now? */
  let stacksInView = false;
  /** Same bookkeeping as `gitLoadedFor`, for the stacks pane. */
  let stacksLoadedFor: string | null = null;
  /** Can the user see the Problems panel right now? */
  let problemsInView = false;
  /** Same bookkeeping as `gitLoadedFor`, for the Problems panel. */
  let problemsLoadedFor: string | null = null;

  /** The folder each center tab was last loaded for. A tab that has never
   * loaded is absent, which is different from one loaded for no folder. */
  const panelLoadedFor = new Map<string, string>();

  const selectionKey = (selection: ProjectSelection): string =>
    selectedRootAvailable ? selection.root.trim() : '\0checkout-deleted';

  const loadPanel = (id: string, selection: ProjectSelection): void => {
    const root = selection.root.trim();
    panelLoadedFor.set(id, selectionKey(selection));
    if (id === 'editor') activators.editor(root || null);
    else if (id === 'browser') activators.browser();
  };

  /** Point the file tree at this folder. There is nothing to list without one. */
  const loadFiles = (selection: ProjectSelection): void => {
    const root = selection.root.trim();
    filesLoadedFor = selectionKey(selection);
    activators.explorer(root || null, !selectedRootAvailable);
  };

  const currentSelection = (): ProjectSelection => {
    const selection = readSelection();
    return selectedRootAvailable ? selection : { ...selection, root: '' };
  };

  /** Point source control at this folder. A session with no folder still calls
   * it, which is what tells the panel there is no repository to show. */
  const loadSourceControl = (selection: ProjectSelection): void => {
    const root = selection.root.trim();
    gitLoadedFor = selectionKey(selection);
    activators.git(root || null);
  };

  /** Point the worktree manager at this selection. It needs the whole selection
   * rather than a bare folder: it lists the checkouts of the project the session
   * is in, and joins the shell's own sessions onto them. */
  const loadWorktrees = (selection: ProjectSelection): void => {
    worktreesLoadedFor = selectionKey(selection);
    activators.worktrees({ root: selection.root, projects: selection.projects });
  };

  /** Point the stacks pane at this folder. The saved stacks are per project. */
  const loadStacks = (selection: ProjectSelection): void => {
    const root = selection.root.trim();
    stacksLoadedFor = selectionKey(selection);
    activators.stacks(root || null);
  };

  /** Point the Problems panel at this folder. */
  const loadProblems = (selection: ProjectSelection): void => {
    const root = selection.root.trim();
    problemsLoadedFor = selectionKey(selection);
    activators.problems(root || null);
  };

  /** Load each view-gated panel that is in view and is not already showing this
   * folder — the same "coming back to it is not a reason to read it again" rule
   * the visibility reports below have always used. */
  const loadSessionPanels = (selection: ProjectSelection): void => {
    const key = selectionKey(selection);
    if (sourceControlInView && gitLoadedFor !== key) loadSourceControl(selection);
    if ((worktreesInView || sourceControlInView) && worktreesLoadedFor !== key) {
      loadWorktrees(selection);
    }
    if (stacksInView && stacksLoadedFor !== key) loadStacks(selection);
    if (problemsInView && problemsLoadedFor !== key) loadProblems(selection);
  };

  return {
    allowPanelLoads(): void {
      panelLoadsAllowed = true;
    },

    allowSessionLoads(): void {
      sessionLoadsAllowed = true;
    },

    loadsAllowed(): boolean {
      return sessionLoadsAllowed;
    },

    panelShown(id: string): void {
      activePanelId = id;
      if (!LOADABLE_PANELS.has(id)) return;
      // Remembered even while loads are switched off. The tab the dock puts
      // back at launch announces itself before that gate opens, and it must
      // load nothing then — but it is on screen, and forgetting it altogether
      // is what left a restored editor tab never being told which project it
      // was in: the session picked next only points the tabs it knows about.
      if (!panelLoadsAllowed) return;
      const selection = currentSelection();
      if (panelLoadedFor.get(id) !== selectionKey(selection)) loadPanel(id, selection);
    },

    sessionPicked(rootAvailable = true): void {
      if (!sessionLoadsAllowed) return;
      selectedRootAvailable = rootAvailable;
      const selection = currentSelection();
      sessionPanelsShown = true;
      loadSessionPanels(selection);
      // Re-point only the center tab that is visible. Remembering every tab
      // ever shown made a hidden editor change roots on every later session
      // switch, which woke its language-service ownership even with zero files.
      if (
        activePanelId
        && activePanelId !== 'browser'
        && LOADABLE_PANELS.has(activePanelId)
        && panelLoadedFor.get(activePanelId) !== selectionKey(selection)
      ) {
        loadPanel(activePanelId, selection);
      }
    },

    filesVisible(visible: boolean): void {
      filesInView = visible;
      if (!visible || !sessionPanelsShown) return;
      const selection = currentSelection();
      if (filesLoadedFor === selectionKey(selection)) return;
      loadFiles(selection);
    },

    sourceControlVisible(visible: boolean): void {
      sourceControlInView = visible;
      // Looking away loads nothing, and neither does looking at it before a
      // session has been picked — there is no folder to read yet, and the pick
      // that follows will load it.
      if (!visible || !sessionPanelsShown) return;
      const selection = currentSelection();
      // Already showing this folder: coming back to it is not a reason to read
      // the repository again.
      if (gitLoadedFor !== selectionKey(selection)) loadSourceControl(selection);
      if (worktreesLoadedFor !== selectionKey(selection)) loadWorktrees(selection);
    },

    worktreesVisible(visible: boolean): void {
      worktreesInView = visible;
      if (!visible || !sessionPanelsShown) return;
      const selection = currentSelection();
      if (worktreesLoadedFor === selectionKey(selection)) return;
      loadWorktrees(selection);
    },

    stacksVisible(visible: boolean): void {
      stacksInView = visible;
      if (!visible || !sessionPanelsShown) return;
      const selection = currentSelection();
      if (stacksLoadedFor === selectionKey(selection)) return;
      loadStacks(selection);
    },

    problemsVisible(visible: boolean): void {
      problemsInView = visible;
      if (!visible || !sessionPanelsShown) return;
      const selection = currentSelection();
      if (problemsLoadedFor === selectionKey(selection)) return;
      loadProblems(selection);
    },

    loadedPanels(): string[] {
      // The tabs that have actually loaded, which is not the same as the tabs
      // on screen: one the dock put back at launch is on screen without having
      // loaded anything.
      const loaded = [...panelLoadedFor.keys()];
      if (filesLoadedFor !== null) loaded.push('explorer');
      if (gitLoadedFor !== null) loaded.push('git');
      if (worktreesLoadedFor !== null) loaded.push('worktrees');
      if (stacksLoadedFor !== null) loaded.push('stacks');
      if (problemsLoadedFor !== null) loaded.push('problems');
      return loaded;
    }
  };
}
