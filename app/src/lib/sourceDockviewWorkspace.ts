import type {
  DockviewApi,
  DockviewIDisposable,
  IDockviewPanel,
  GroupPanelPartInitParameters,
  IContentRenderer,
  IPanePart,
  PanePanelComponentInitParameter,
  PaneviewApi,
  SerializedDockview,
  SerializedPaneview
} from 'dockview-core';
import {
  activateSourceDockPanel,
  hideSourceDockPanel,
  normalizeSourceDockLayout,
  sourceDockPanelDescriptors,
  type SourceDockGroupID,
  type SourceDockLayout,
  type SourceDockPanelID
} from './sourceDockLayout.ts';

export const sourceDockviewStorageKey = 'mac-command-bar.source-browser.dockview-layout';
export const sourceDockviewComponentID = 'source-panel';
export const sourcePaneviewComponentID = 'source-pane-panel';

export type SourceDockviewMigrationSliceID =
  | 'activity-only'
  | 'insights-only'
  | 'context-insights'
  | 'center-runtime'
  | 'bottom-runtime';

export type SourceDockviewMigrationSlice = {
  id: SourceDockviewMigrationSliceID;
  rootPanelID: SourceDockPanelID;
  panelIDs: SourceDockPanelID[];
  groupID?: SourceDockGroupID;
};

export type SourceDockviewPanelDescriptor = {
  id: SourceDockPanelID;
  title: string;
  component: typeof sourceDockviewComponentID;
};

export type SourceDockviewPanelPlan = {
  id: SourceDockPanelID;
  title: string;
  component: typeof sourceDockviewComponentID;
  params: {
    panelID: SourceDockPanelID;
  };
  renderer: 'onlyWhenVisible' | 'always';
  initialWidth?: number;
  initialHeight?: number;
  position?: {
    referencePanel: SourceDockPanelID;
    direction: 'left' | 'right' | 'above' | 'below' | 'within';
  };
};

export type SourceDockviewWorkspace = {
  api: DockviewApi;
  setPanelElement(panelID: SourceDockPanelID, element: HTMLElement | null): void;
  syncLayout(layout: SourceDockLayout): void;
  toJSON(): SerializedDockview;
  dispose(): void;
};

export type SourceDockviewTabStackPanel<PanelID extends string> = {
  id: PanelID;
  title: string;
};

export type SourceDockviewTabStackWorkspace<PanelID extends string> = {
  api: DockviewApi;
  setPanelElement(panelID: PanelID, element: HTMLElement | null): void;
  syncPanels(panels: SourceDockviewTabStackPanel<PanelID>[], rootPanelID?: PanelID): void;
  toJSON(): SerializedDockview;
  dispose(): void;
};

export type SourceDockviewTabStackWorkspaceOptions<PanelID extends string> = {
  panels: SourceDockviewTabStackPanel<PanelID>[];
  rootPanelID?: PanelID;
  storedLayout?: SerializedDockview | null;
  restoreClosedPanels?: boolean;
  onDidLayoutChange?: (layout: SerializedDockview) => void;
  onDidPanelClose?: (panelID: PanelID, layout: SerializedDockview) => void;
  onDidActivePanelChange?: (panelID: PanelID | null, layout: SerializedDockview) => void;
  onDidPanelMove?: (panelID: PanelID, layout: SerializedDockview) => void;
};

type SourceDockviewTabStackPanelPosition<PanelID extends string> = {
  referencePanel: PanelID;
  direction: 'left' | 'right' | 'above' | 'below' | 'within';
};

export type SourcePaneviewStackPanel<PanelID extends string> = {
  id: PanelID;
  title: string;
  size?: number;
  minimumBodySize?: number;
  maximumBodySize?: number;
  isExpanded?: boolean;
};

export type SourcePaneviewStackWorkspace<PanelID extends string> = {
  api: PaneviewApi;
  setPanelElement(panelID: PanelID, element: HTMLElement | null): void;
  syncPanels(panels: SourcePaneviewStackPanel<PanelID>[]): void;
  toJSON(): SerializedPaneview;
  dispose(): void;
};

export type SourcePaneviewStackWorkspaceOptions<PanelID extends string> = {
  panels: SourcePaneviewStackPanel<PanelID>[];
  storedLayout?: SerializedPaneview | null;
  onDidLayoutChange?: (layout: SerializedPaneview) => void;
};

export type SourceDockviewWorkspaceOptions = {
  layout: SourceDockLayout;
  panelIDs?: SourceDockPanelID[];
  rootPanelID?: SourceDockPanelID;
  groupID?: SourceDockGroupID;
  storedLayout?: SerializedDockview | null;
  restoreStoredLayout?: boolean;
  onDidLayoutChange?: (layout: SerializedDockview) => void;
  onDidPanelClose?: (panelID: SourceDockPanelID, layout: SerializedDockview) => void;
  onDidActivePanelChange?: (panelID: SourceDockPanelID | null, layout: SerializedDockview) => void;
  onDidPanelMove?: (panelID: SourceDockPanelID, layout: SerializedDockview) => void;
};

export const sourceDockviewPanelDescriptors: SourceDockviewPanelDescriptor[] =
  sourceDockPanelDescriptors.map((panel) => ({
    id: panel.id,
    title: panel.label,
    component: sourceDockviewComponentID
  }));

export const sourceDockviewMigrationSlices: SourceDockviewMigrationSlice[] = [
  { id: 'activity-only', rootPanelID: 'activity', panelIDs: ['activity'] },
  { id: 'insights-only', rootPanelID: 'insights', panelIDs: ['insights'] },
  { id: 'context-insights', rootPanelID: 'context', panelIDs: ['context', 'insights'] },
  {
    id: 'center-runtime',
    rootPanelID: 'editor',
    panelIDs: ['editor', 'terminal', 'browser'],
    groupID: 'center'
  },
  {
    id: 'bottom-runtime',
    rootPanelID: 'terminal',
    panelIDs: ['terminal', 'browser'],
    groupID: 'bottom'
  }
];

export type SourceDockviewPanelPlanOptions = {
  panelIDs?: SourceDockPanelID[];
  rootPanelID?: SourceDockPanelID;
  groupID?: SourceDockGroupID;
};

type SourceDockviewPanelPlanRequestOptions = {
  panelIDs?: unknown;
  rootPanelID?: unknown;
  groupID?: unknown;
};

export function sourceDockviewMigrationSliceStorageKey(
  sliceID: SourceDockviewMigrationSliceID
): string {
  return `${sourceDockviewStorageKey}.${sliceID}`;
}

export function sourceDockviewMigrationSlicePlanOptions(
  sliceID: SourceDockviewMigrationSliceID
): SourceDockviewPanelPlanOptions {
  const slice = sourceDockviewMigrationSlice(sliceID);
  return normalizeSourceDockviewPanelPlanOptions({
    panelIDs: [...slice.panelIDs],
    rootPanelID: slice.rootPanelID,
    groupID: slice.groupID
  });
}

export function normalizeSourceDockviewPanelPlanOptions(
  options: SourceDockviewPanelPlanRequestOptions = {}
): SourceDockviewPanelPlanOptions {
  const hasRootPanelID = options.rootPanelID !== undefined;
  const rootPanelID = normalizeSourceDockviewPanelID(
    hasRootPanelID ? options.rootPanelID : 'editor',
    'rootPanelID'
  );
  const groupID = normalizeSourceDockviewGroupID(options.groupID, 'groupID');

  if (options.panelIDs === undefined) {
    if (hasRootPanelID || groupID) {
      return {
        ...(hasRootPanelID ? { rootPanelID } : {}),
        ...(groupID ? { groupID } : {})
      };
    }
    return {};
  }
  if (!Array.isArray(options.panelIDs)) {
    throw new Error('Dockview panelIDs must be an array');
  }

  const panelIDs: SourceDockPanelID[] = [];
  for (const [index, panelID] of options.panelIDs.entries()) {
    const normalizedPanelID = normalizeSourceDockviewPanelID(panelID, `panelIDs[${index}]`);
    if (!panelIDs.includes(normalizedPanelID)) {
      panelIDs.push(normalizedPanelID);
    }
  }

  if (!panelIDs.includes(rootPanelID)) {
    panelIDs.unshift(rootPanelID);
  }

  return {
    panelIDs,
    rootPanelID,
    ...(groupID ? { groupID } : {})
  };
}

function sourceDockviewMigrationSlice(sliceID: SourceDockviewMigrationSliceID): SourceDockviewMigrationSlice {
  const slice = sourceDockviewMigrationSlices.find((candidate) => candidate.id === sliceID);
  if (!slice) {
    throw new Error(`Unknown Dockview migration slice: ${sliceID}`);
  }
  return slice;
}

export function createSourceDockviewPanelPlans(
  layout: SourceDockLayout,
  options: SourceDockviewPanelPlanOptions = {}
): SourceDockviewPanelPlan[] {
  const normalizedLayout = normalizeSourceDockLayout(layout);
  const normalizedOptions = normalizeSourceDockviewPanelPlanOptions(options);
  const plans: SourceDockviewPanelPlan[] = [];
  const plannedPanelIDs = new Set<SourceDockPanelID>();
  const includedPanelIDs = normalizedOptions.panelIDs ? new Set(normalizedOptions.panelIDs) : null;
  const requestedRootPanelID = normalizedOptions.rootPanelID ?? 'editor';
  const visibleIncludedPanelIDs = sourceDockviewVisiblePanelIDs(
    normalizedLayout,
    includedPanelIDs,
    normalizedOptions.groupID
  );

  if (visibleIncludedPanelIDs.length === 0) {
    return [];
  }

  if (includedPanelIDs && !includedPanelIDs.has(requestedRootPanelID)) {
    includedPanelIDs.add(requestedRootPanelID);
  }

  const rootPanelID = visibleIncludedPanelIDs.includes(requestedRootPanelID)
    ? requestedRootPanelID
    : visibleIncludedPanelIDs[0];

  plans.push(createSourceDockviewPanelPlan(rootPanelID, sourceDockviewPanelTitle(rootPanelID)));
  plannedPanelIDs.add(rootPanelID);

  for (const group of normalizedLayout.groups) {
    if (normalizedOptions.groupID && group.id !== normalizedOptions.groupID) continue;

    const visiblePanelIDs = group.panelIDs.filter(
      (panelID) => panelID !== rootPanelID && (!includedPanelIDs || includedPanelIDs.has(panelID))
    );
    if (visiblePanelIDs.length === 0) continue;

    let previousPanelID: SourceDockPanelID | null =
      group.panelIDs.includes(rootPanelID) &&
      (rootPanelID !== 'editor' || normalizedOptions.groupID === group.id)
        ? rootPanelID
        : null;
    let previousCenterTabPanelID: SourceDockPanelID | null =
      group.id === 'center' && rootPanelID === 'editor' && group.panelIDs.includes(rootPanelID)
        ? rootPanelID
        : null;
    for (const panelID of visiblePanelIDs) {
      if (plannedPanelIDs.has(panelID)) continue;

      const shouldStackWithEditor =
        previousCenterTabPanelID !== null && isSourceDockviewCenterTabPanelID(panelID);
      const position: SourceDockviewPanelPlan['position'] = shouldStackWithEditor && previousCenterTabPanelID
        ? { referencePanel: previousCenterTabPanelID, direction: 'within' as const }
        : previousPanelID
        ? { referencePanel: previousPanelID, direction: 'within' as const }
        : firstPanelPositionForGroup(group.id, rootPanelID);
      const plan = createSourceDockviewPanelPlan(panelID, sourceDockviewPanelTitle(panelID), position);

      if (!previousPanelID || previousPanelID === rootPanelID) {
        if (group.id === 'left' || group.id === 'right') {
          plan.initialWidth = group.size;
        }
        if (group.id === 'bottom') {
          plan.initialHeight = group.size;
        }
      }

      plans.push(plan);
      plannedPanelIDs.add(panelID);
      if (shouldStackWithEditor) {
        previousCenterTabPanelID = panelID;
      } else {
        previousPanelID = panelID;
      }
    }
  }

  return plans;
}

export function sourceDockviewStoredLayoutMatchesPanelPlans(
  layout: Pick<SerializedDockview, 'panels'>,
  plans: SourceDockviewPanelPlan[]
): boolean {
  const expectedPanelIDs = plans.map((plan) => plan.id);
  return sourceDockviewStoredLayoutMatchesPanelIDs(layout, expectedPanelIDs);
}

export function sourceDockviewStoredLayoutMatchesPanelIDs(
  layout: Pick<SerializedDockview, 'panels'>,
  expectedPanelIDs: string[]
): boolean {
  const storedPanelIDs = Object.keys(layout.panels ?? {});

  return (
    expectedPanelIDs.length > 0 &&
    storedPanelIDs.length === expectedPanelIDs.length &&
    expectedPanelIDs.every((panelID) => storedPanelIDs.includes(panelID))
  );
}

export function sourcePaneviewStoredLayoutMatchesPanelIDs(
  layout: Pick<SerializedPaneview, 'views'>,
  expectedPanelIDs: string[]
): boolean {
  const storedPanelIDs = (layout.views ?? []).map((view) => view.data.id);

  return (
    expectedPanelIDs.length > 0 &&
    storedPanelIDs.length === expectedPanelIDs.length &&
    expectedPanelIDs.every((panelID) => storedPanelIDs.includes(panelID))
  );
}

export function applySourceDockviewPanelClose(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID
): SourceDockLayout {
  if (panelID === 'editor') return normalizeSourceDockLayout(layout);
  return hideSourceDockPanel(layout, panelID);
}

export function applySourceDockviewActivePanel(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID | null
): SourceDockLayout {
  if (!panelID) return normalizeSourceDockLayout(layout);
  return activateSourceDockPanel(layout, panelID);
}

export async function createSourceDockviewTabStackWorkspace<PanelID extends string>(
  container: HTMLElement,
  options: SourceDockviewTabStackWorkspaceOptions<PanelID>
): Promise<SourceDockviewTabStackWorkspace<PanelID>> {
  const { createDockview, themeDracula } = await import('dockview-core');
  const panelHosts = new Map<PanelID, HTMLElement>();
  const panelElements = new Map<PanelID, HTMLElement>();
  let panels = options.panels;
  let panelIDs = panels.map((panel) => panel.id);
  const panelIDSet = new Set(panelIDs);
  const disposables: DockviewIDisposable[] = [];
  let synchronizingDockview = false;

  const attachPanelElement = (panelID: PanelID) => {
    const host = panelHosts.get(panelID);
    const element = panelElements.get(panelID);
    if (!host || !element) return;

    element.classList.add('source-dockview-attached-panel');
    if (element.parentElement !== host) {
      host.replaceChildren(element);
    }
    dispatchSourceDockviewLayout(element);
  };

  const api = createDockview(container, {
    className: 'source-dockview-core',
    defaultRenderer: 'onlyWhenVisible',
    dndStrategy: 'pointer',
    hideBorders: true,
    scrollbars: 'native',
    singleTabMode: 'default',
    theme: { ...themeDracula, gap: 0 },
    getTabContextMenuItems: () => [],
    createComponent: () => createSourceDockviewTabStackContentRenderer(panelIDSet, panelHosts, attachPanelElement)
  });

  container.classList.add('dockview-theme-dark', 'dockview-theme-dracula', 'source-dockview-host');
  layoutDockviewApiToContainer(api, container);

  const runDockviewSync = (sync: () => void) => {
    synchronizingDockview = true;
    try {
      sync();
    } finally {
      synchronizingDockview = false;
    }
  };

  try {
    const storedLayout = options.storedLayout;
    if (storedLayout && sourceDockviewStoredLayoutMatchesPanelIDs(storedLayout, panelIDs)) {
      runDockviewSync(() => api.fromJSON(storedLayout, { reuseExistingPanels: true }));
    } else {
      runDockviewSync(() => addSourceDockviewTabStackPanels(api, panels, options.rootPanelID));
    }
  } catch {
    runDockviewSync(() => {
      api.clear();
      addSourceDockviewTabStackPanels(api, panels, options.rootPanelID);
    });
  }

  if (options.onDidLayoutChange) {
    disposables.push(
      api.onDidLayoutChange(() => {
        if (synchronizingDockview) return;
        options.onDidLayoutChange?.(api.toJSON());
      })
    );
  }

  disposables.push(
    api.onDidRemovePanel((panel) => {
      const panelID = dockviewTabStackPanelID(panel, panelIDSet);
      if (synchronizingDockview || !panelID) return;
      options.onDidPanelClose?.(panelID, api.toJSON());
      if (options.restoreClosedPanels) {
        runDockviewSync(() => addSourceDockviewTabStackPanels(api, panels, panelID));
        options.onDidLayoutChange?.(api.toJSON());
      }
    }),
    api.onDidActivePanelChange((panel) => {
      const panelID = dockviewTabStackPanelID(panel, panelIDSet);
      if (synchronizingDockview) return;
      options.onDidActivePanelChange?.(panelID, api.toJSON());
    }),
    api.onDidMovePanel((event) => {
      const panelID = dockviewTabStackPanelID(event.panel, panelIDSet);
      if (synchronizingDockview || !panelID) return;
      options.onDidPanelMove?.(panelID, api.toJSON());
    })
  );

  return {
    api,
    setPanelElement(panelID, element) {
      if (element) {
        panelElements.set(panelID, element);
        attachPanelElement(panelID);
        return;
      }
      panelElements.delete(panelID);
    },
    syncPanels(nextPanels, rootPanelID) {
      panels = nextPanels;
      panelIDs = panels.map((panel) => panel.id);
      runDockviewSync(() =>
        syncSourceDockviewTabStackPanels(api, panels, panelIDSet, rootPanelID)
      );
      options.onDidLayoutChange?.(api.toJSON());
    },
    toJSON() {
      return api.toJSON();
    },
    dispose() {
      for (const disposable of disposables) {
        disposable.dispose();
      }
      api.dispose();
    }
  };
}

export async function createSourceDockviewWorkspace(
  container: HTMLElement,
  options: SourceDockviewWorkspaceOptions
): Promise<SourceDockviewWorkspace> {
  const { createDockview, themeDracula } = await import('dockview-core');
  const panelHosts = new Map<SourceDockPanelID, HTMLElement>();
  const panelElements = new Map<SourceDockPanelID, HTMLElement>();
  const disposables: DockviewIDisposable[] = [];
  let synchronizingDockview = false;

  const attachPanelElement = (panelID: SourceDockPanelID) => {
    const host = panelHosts.get(panelID);
    const element = panelElements.get(panelID);
    if (!host || !element) return;

    element.classList.add('source-dockview-attached-panel');
    if (element.parentElement !== host) {
      host.replaceChildren(element);
    }
    dispatchSourceDockviewLayout(element);
  };

  const api = createDockview(container, {
    className: 'source-dockview-core',
    defaultRenderer: 'always',
    dndStrategy: 'pointer',
    hideBorders: true,
    scrollbars: 'native',
    singleTabMode: 'default',
    theme: { ...themeDracula, gap: 0 },
    getTabContextMenuItems: () => ['close', 'closeOthers', 'separator', 'closeAll'],
    createComponent: () => createSourceDockviewContentRenderer(panelHosts, attachPanelElement)
  });

  container.classList.add('dockview-theme-dark', 'dockview-theme-dracula', 'source-dockview-host');
  layoutDockviewApiToContainer(api, container);

  const runDockviewSync = (sync: () => void) => {
    synchronizingDockview = true;
    try {
      sync();
    } finally {
      synchronizingDockview = false;
    }
  };

  try {
    const storedLayout = options.storedLayout;
    const restorePlans = createSourceDockviewPanelPlans(options.layout, options);
    if (
      options.restoreStoredLayout !== false &&
      storedLayout &&
      sourceDockviewStoredLayoutMatchesPanelPlans(storedLayout, restorePlans)
    ) {
      runDockviewSync(() => api.fromJSON(storedLayout, { reuseExistingPanels: true }));
    } else {
      runDockviewSync(() => addSourceDockviewPanels(api, options.layout, options));
    }
  } catch {
    runDockviewSync(() => {
      api.clear();
      addSourceDockviewPanels(api, options.layout, options);
    });
  }

  if (options.onDidLayoutChange) {
    disposables.push(
      api.onDidLayoutChange(() => {
        if (synchronizingDockview) return;
        options.onDidLayoutChange?.(api.toJSON());
      })
    );
  }

  disposables.push(
    api.onDidRemovePanel((panel) => {
      const panelID = sourceDockviewPanelID(panel);
      if (synchronizingDockview || !panelID) return;
      options.onDidPanelClose?.(panelID, api.toJSON());
    }),
    api.onDidActivePanelChange((panel) => {
      const panelID = sourceDockviewPanelID(panel);
      if (synchronizingDockview) return;
      options.onDidActivePanelChange?.(panelID, api.toJSON());
    }),
    api.onDidMovePanel((event) => {
      const panelID = sourceDockviewPanelID(event.panel);
      if (synchronizingDockview || !panelID) return;
      options.onDidPanelMove?.(panelID, api.toJSON());
    })
  );

  return {
    api,
    setPanelElement(panelID, element) {
      if (element) {
        panelElements.set(panelID, element);
        attachPanelElement(panelID);
        return;
      }
      panelElements.delete(panelID);
    },
    syncLayout(layout) {
      runDockviewSync(() => syncSourceDockviewPanels(api, layout, options));
    },
    toJSON() {
      return api.toJSON();
    },
    dispose() {
      for (const disposable of disposables) {
        disposable.dispose();
      }
      api.dispose();
    }
  };
}

export async function createSourcePaneviewStackWorkspace<PanelID extends string>(
  container: HTMLElement,
  options: SourcePaneviewStackWorkspaceOptions<PanelID>
): Promise<SourcePaneviewStackWorkspace<PanelID>> {
  const { createPaneview } = await import('dockview-core');
  const panelHosts = new Map<PanelID, HTMLElement>();
  const panelElements = new Map<PanelID, HTMLElement>();
  let panels = options.panels;
  const panelIDSet = new Set(panels.map((panel) => panel.id));
  const disposables: DockviewIDisposable[] = [];
  let synchronizingPaneview = false;

  const attachPanelElement = (panelID: PanelID) => {
    const host = panelHosts.get(panelID);
    const element = panelElements.get(panelID);
    if (!host || !element) return;

    element.classList.add('source-paneview-attached-panel', 'source-dockview-attached-panel');
    if (element.parentElement !== host) {
      host.replaceChildren(element);
    }
    dispatchSourceDockviewLayout(element);
  };

  const api = createPaneview(container, {
    className: 'source-paneview-core',
    disableDnd: true,
    createComponent: () => createSourcePaneviewStackContentRenderer(panelIDSet, panelHosts, attachPanelElement)
  });

  container.classList.add('dockview-theme-dark', 'dockview-theme-dracula', 'source-paneview-host');
  layoutPaneviewApiToContainer(api, container);

  const runPaneviewSync = (sync: () => void) => {
    synchronizingPaneview = true;
    try {
      sync();
    } finally {
      synchronizingPaneview = false;
    }
  };

  try {
    const storedLayout = options.storedLayout;
    if (storedLayout && sourcePaneviewStoredLayoutMatchesPanelIDs(storedLayout, panels.map((panel) => panel.id))) {
      runPaneviewSync(() => api.fromJSON(storedLayout));
    } else {
      runPaneviewSync(() => addSourcePaneviewStackPanels(api, panels));
    }
  } catch {
    runPaneviewSync(() => {
      api.clear();
      addSourcePaneviewStackPanels(api, panels);
    });
  }

  if (options.onDidLayoutChange) {
    disposables.push(
      api.onDidLayoutChange(() => {
        if (synchronizingPaneview) return;
        options.onDidLayoutChange?.(api.toJSON());
      })
    );
  }

  return {
    api,
    setPanelElement(panelID, element) {
      if (element) {
        panelElements.set(panelID, element);
        attachPanelElement(panelID);
        return;
      }
      panelElements.delete(panelID);
    },
    syncPanels(nextPanels) {
      panels = nextPanels;
      runPaneviewSync(() => {
        syncSourcePaneviewStackPanels(api, panels, panelIDSet);
        layoutPaneviewApiToContainer(api, container);
      });
      options.onDidLayoutChange?.(api.toJSON());
    },
    toJSON() {
      return api.toJSON();
    },
    dispose() {
      for (const disposable of disposables) {
        disposable.dispose();
      }
      api.dispose();
    }
  };
}

export function addSourceDockviewPanels(
  api: DockviewApi,
  layout: SourceDockLayout,
  options: SourceDockviewPanelPlanOptions = {}
) {
  for (const panel of [...api.panels]) {
    api.removePanel(panel);
  }

  for (const plan of createSourceDockviewPanelPlans(layout, options)) {
    api.addPanel(plan);
  }

  syncSourceDockviewActivePanels(api, layout);
}

export function syncSourceDockviewPanels(
  api: DockviewApi,
  layout: SourceDockLayout,
  options: SourceDockviewPanelPlanOptions = {}
) {
  const plans = createSourceDockviewPanelPlans(layout, options);
  const nextPanelIDs = new Set(plans.map((plan) => plan.id));

  for (const panel of [...api.panels]) {
    if (!nextPanelIDs.has(panel.id as SourceDockPanelID)) {
      api.removePanel(panel);
    }
  }

  let referencePanelID = plans.find((plan) => api.getPanel(plan.id))?.id;
  for (const plan of plans) {
    const existingPanel = api.getPanel(plan.id);
    if (existingPanel) {
      if (existingPanel.title !== plan.title) {
        existingPanel.api.setTitle(plan.title);
      }
      if (existingPanel.api.renderer !== plan.renderer) {
        existingPanel.api.setRenderer(plan.renderer);
      }
      const planPosition = plan.position;
      const referencePanel = planPosition ? api.getPanel(planPosition.referencePanel) : undefined;
      if (
        planPosition &&
        referencePanel &&
        (
          referencePanel.api.group !== existingPanel.api.group ||
          planPosition.direction !== 'within'
        )
      ) {
        existingPanel.api.moveTo({
          group: referencePanel.api.group,
          position: planPosition.direction === 'within' ? undefined : dockviewMovePosition(planPosition.direction)
        });
      }
      referencePanelID = plan.id;
      continue;
    }

    api.addPanel({
      ...plan,
      position: plan.position ?? (referencePanelID
        ? { referencePanel: referencePanelID, direction: 'within' }
        : undefined)
    });
    referencePanelID = plan.id;
  }

  syncSourceDockviewActivePanels(api, layout);
}

function dockviewMovePosition(
  direction: NonNullable<SourceDockviewPanelPlan['position']>['direction']
) {
  switch (direction) {
    case 'above':
      return 'top';
    case 'below':
      return 'bottom';
    case 'within':
      return 'center';
    default:
      return direction;
  }
}

function syncSourceDockviewActivePanels(api: DockviewApi, layout: SourceDockLayout) {
  const normalized = normalizeSourceDockLayout(layout);

  for (const group of normalized.groups) {
    const activePanelID = normalized.activePanelByGroup[group.id];
    if (!activePanelID || !group.panelIDs.includes(activePanelID)) continue;

    api.getPanel(activePanelID)?.api.setActive();
  }
}

export function addSourcePaneviewStackPanels<PanelID extends string>(
  api: PaneviewApi,
  panels: SourcePaneviewStackPanel<PanelID>[]
) {
  for (const panel of [...api.panels]) {
    api.removePanel(panel);
  }

  for (const panel of panels) {
    api.addPanel(createSourcePaneviewStackPanelPlan(panel));
  }
}

export function syncSourcePaneviewStackPanels<PanelID extends string>(
  api: PaneviewApi,
  panels: SourcePaneviewStackPanel<PanelID>[],
  validPanelIDs: Set<PanelID>
) {
  const nextPanelIDs = panels.map((panel) => panel.id);
  const nextPanelIDSet = new Set(nextPanelIDs);

  for (const panel of [...api.panels]) {
    if (!nextPanelIDSet.has(panel.id as PanelID)) {
      api.removePanel(panel);
    }
  }

  validPanelIDs.clear();
  for (const panelID of nextPanelIDs) {
    validPanelIDs.add(panelID);
  }

  for (const [index, panel] of panels.entries()) {
    const existingPanel = api.getPanel(panel.id);
    if (existingPanel) {
      existingPanel.setExpanded(panel.isExpanded ?? true);

      const currentIndex = api.panels.findIndex((candidatePanel) => candidatePanel.id === panel.id);
      if (currentIndex !== -1 && currentIndex !== index) {
        api.movePanel(currentIndex, index);
      }
      continue;
    }

    api.addPanel({
      ...createSourcePaneviewStackPanelPlan(panel),
      index
    });
  }
}

export function addSourceDockviewTabStackPanels<PanelID extends string>(
  api: DockviewApi,
  panels: SourceDockviewTabStackPanel<PanelID>[],
  rootPanelID?: PanelID
) {
  for (const panel of [...api.panels]) {
    api.removePanel(panel);
  }

  const orderedPanels = sourceDockviewOrderedTabStackPanels(panels, rootPanelID);
  const [firstPanel] = orderedPanels;
  if (!firstPanel) return;

  api.addPanel(createSourceDockviewTabStackPanelPlan(firstPanel));

  let previousPanelID = firstPanel.id;
  for (const panel of orderedPanels.slice(1)) {
    api.addPanel(
      createSourceDockviewTabStackPanelPlan(panel, {
        referencePanel: previousPanelID,
        direction: 'within'
      })
    );
    previousPanelID = panel.id;
  }
}

export function syncSourceDockviewTabStackPanels<PanelID extends string>(
  api: DockviewApi,
  panels: SourceDockviewTabStackPanel<PanelID>[],
  validPanelIDs: Set<PanelID>,
  rootPanelID?: PanelID
) {
  const nextPanelIDs = panels.map((panel) => panel.id);
  const nextPanelIDSet = new Set(nextPanelIDs);

  for (const panel of [...api.panels]) {
    if (!nextPanelIDSet.has(panel.id as PanelID)) {
      api.removePanel(panel);
    }
  }

  validPanelIDs.clear();
  for (const panelID of nextPanelIDs) {
    validPanelIDs.add(panelID);
  }

  const orderedPanels = sourceDockviewOrderedTabStackPanels(panels, rootPanelID);
  if (orderedPanels.length === 0) return;

  let referencePanelID = orderedPanels.find((panel) => api.getPanel(panel.id))?.id;
  for (const panel of orderedPanels) {
    const existingPanel = api.getPanel(panel.id);
    if (existingPanel) {
      if (existingPanel.title !== panel.title) {
        existingPanel.api.setTitle(panel.title);
      }
      referencePanelID = panel.id;
      continue;
    }

    api.addPanel(
      createSourceDockviewTabStackPanelPlan(
        panel,
        referencePanelID
          ? { referencePanel: referencePanelID, direction: 'within' }
          : undefined
      )
    );
    referencePanelID = panel.id;
  }
}

function sourceDockviewOrderedTabStackPanels<PanelID extends string>(
  panels: SourceDockviewTabStackPanel<PanelID>[],
  rootPanelID?: PanelID
) {
  const rootPanel = panels.find((panel) => panel.id === rootPanelID);
  if (!rootPanel) return panels;
  return [rootPanel, ...panels.filter((panel) => panel.id !== rootPanel.id)];
}

function sourceDockviewVisiblePanelIDs(
  layout: SourceDockLayout,
  includedPanelIDs: Set<SourceDockPanelID> | null,
  groupID?: SourceDockGroupID
): SourceDockPanelID[] {
  const visiblePanelIDs: SourceDockPanelID[] = [];
  for (const group of layout.groups) {
    if (groupID && group.id !== groupID) continue;

    for (const panelID of group.panelIDs) {
      if (includedPanelIDs && !includedPanelIDs.has(panelID)) continue;
      if (!visiblePanelIDs.includes(panelID)) {
        visiblePanelIDs.push(panelID);
      }
    }
  }
  return visiblePanelIDs;
}

function createSourceDockviewTabStackContentRenderer<PanelID extends string>(
  validPanelIDs: Set<PanelID>,
  panelHosts: Map<PanelID, HTMLElement>,
  attachPanelElement: (panelID: PanelID) => void
): IContentRenderer {
  const element = document.createElement('div');
  element.className = 'source-dockview-panel-host';
  let panelID: PanelID | null = null;

  return {
    element,
    init(params: GroupPanelPartInitParameters) {
      const candidatePanelID = params.params?.panelID;
      if (typeof candidatePanelID !== 'string' || !validPanelIDs.has(candidatePanelID as PanelID)) {
        element.textContent = 'Unknown panel';
        return;
      }

      panelID = candidatePanelID as PanelID;
      panelHosts.set(panelID, element);
      attachPanelElement(panelID);
    },
    dispose() {
      if (panelID) {
        panelHosts.delete(panelID);
      }
    },
    layout() {
      dispatchSourceDockviewLayout(element);
    }
  };
}

function createSourcePaneviewStackContentRenderer<PanelID extends string>(
  validPanelIDs: Set<PanelID>,
  panelHosts: Map<PanelID, HTMLElement>,
  attachPanelElement: (panelID: PanelID) => void
): IPanePart {
  const element = document.createElement('div');
  element.className = 'source-paneview-panel-host';
  let panelID: PanelID | null = null;

  return {
    element,
    init(params: PanePanelComponentInitParameter) {
      const candidatePanelID = params.params?.panelID;
      if (typeof candidatePanelID !== 'string' || !validPanelIDs.has(candidatePanelID as PanelID)) {
        element.textContent = 'Unknown panel';
        return;
      }

      panelID = candidatePanelID as PanelID;
      panelHosts.set(panelID, element);
      attachPanelElement(panelID);
    },
    update() {
      dispatchSourceDockviewLayout(element);
    },
    dispose() {
      if (panelID) {
        panelHosts.delete(panelID);
      }
    }
  };
}

function createSourceDockviewContentRenderer(
  panelHosts: Map<SourceDockPanelID, HTMLElement>,
  attachPanelElement: (panelID: SourceDockPanelID) => void
): IContentRenderer {
  const element = document.createElement('div');
  element.className = 'source-dockview-panel-host';
  let panelID: SourceDockPanelID | null = null;

  return {
    element,
    init(params: GroupPanelPartInitParameters) {
      const candidatePanelID = params.params?.panelID;
      if (!isSourceDockviewPanelID(candidatePanelID)) {
        element.textContent = 'Unknown panel';
        return;
      }

      panelID = candidatePanelID;
      panelHosts.set(panelID, element);
      attachPanelElement(panelID);
    },
    dispose() {
      if (panelID) {
        panelHosts.delete(panelID);
      }
    },
    layout() {
      dispatchSourceDockviewLayout(element);
    }
  };
}

function dispatchSourceDockviewLayout(element: HTMLElement) {
  element.dispatchEvent(new CustomEvent('source-dockview-layout', { bubbles: true }));
  const attachedElement = element.firstElementChild;
  if (attachedElement instanceof HTMLElement) {
    attachedElement.dispatchEvent(new CustomEvent('source-dockview-layout', { bubbles: true }));
  }
}

function layoutDockviewApiToContainer(api: DockviewApi, container: HTMLElement) {
  const rect = container.getBoundingClientRect();
  const width = Math.max(0, Math.round(rect.width || container.clientWidth));
  const height = Math.max(0, Math.round(rect.height || container.clientHeight));
  if (width === 0 || height === 0) return;

  api.layout(width, height, true);
}

function layoutPaneviewApiToContainer(api: PaneviewApi, container: HTMLElement) {
  const rect = container.getBoundingClientRect();
  const width = Math.max(0, Math.round(rect.width || container.clientWidth));
  const height = Math.max(0, Math.round(rect.height || container.clientHeight));
  if (width === 0 || height === 0) return;

  api.layout(width, height);
}

function createSourcePaneviewStackPanelPlan<PanelID extends string>(
  panel: SourcePaneviewStackPanel<PanelID>
) {
  return {
    id: panel.id,
    title: panel.title,
    component: sourcePaneviewComponentID,
    params: { panelID: panel.id },
    headerSize: 30,
    minimumBodySize: panel.minimumBodySize ?? 64,
    maximumBodySize: panel.maximumBodySize,
    isExpanded: panel.isExpanded ?? true,
    size: panel.size
  };
}

function createSourceDockviewTabStackPanelPlan<PanelID extends string>(
  panel: SourceDockviewTabStackPanel<PanelID>,
  position?: SourceDockviewTabStackPanelPosition<PanelID>
) {
  return {
    id: panel.id,
    title: panel.title,
    component: sourceDockviewComponentID,
    params: { panelID: panel.id },
    renderer: 'always' as const,
    position
  };
}

function createSourceDockviewPanelPlan(
  panelID: SourceDockPanelID,
  title: string,
  position?: SourceDockviewPanelPlan['position']
): SourceDockviewPanelPlan {
  return {
    id: panelID,
    title,
    component: sourceDockviewComponentID,
    params: { panelID },
    renderer: 'onlyWhenVisible',
    position
  };
}

function sourceDockviewPanelTitle(panelID: SourceDockPanelID) {
  return sourceDockPanelDescriptors.find((panel) => panel.id === panelID)?.label ?? panelID;
}

function firstPanelPositionForGroup(
  groupID: SourceDockGroupID,
  rootPanelID: SourceDockPanelID = 'editor'
): SourceDockviewPanelPlan['position'] {
  if (rootPanelID !== 'editor') {
    return { referencePanel: rootPanelID, direction: 'within' };
  }

  switch (groupID) {
    case 'left':
      return { referencePanel: 'editor', direction: 'left' };
    case 'right':
      return { referencePanel: 'editor', direction: 'right' };
    case 'bottom':
      return { referencePanel: 'editor', direction: 'below' };
    case 'center':
      return { referencePanel: 'editor', direction: 'above' };
  }
}

function isSourceDockviewPanelID(value: unknown): value is SourceDockPanelID {
  return sourceDockviewPanelDescriptors.some((panel) => panel.id === value);
}

function isSourceDockviewGroupID(value: unknown): value is SourceDockGroupID {
  return value === 'left' || value === 'center' || value === 'right' || value === 'bottom';
}

function isSourceDockviewCenterTabPanelID(panelID: SourceDockPanelID) {
  return panelID === 'terminal' || panelID === 'browser';
}

function normalizeSourceDockviewPanelID(value: unknown, fieldName: string): SourceDockPanelID {
  if (isSourceDockviewPanelID(value)) return value;
  throw new Error(`Unknown Dockview panel ID for ${fieldName}: ${String(value)}`);
}

function normalizeSourceDockviewGroupID(
  value: unknown,
  fieldName: string
): SourceDockGroupID | undefined {
  if (value === undefined) return undefined;
  if (isSourceDockviewGroupID(value)) return value;
  throw new Error(`Unknown Dockview group ID for ${fieldName}: ${String(value)}`);
}

function sourceDockviewPanelID(panel: IDockviewPanel | undefined): SourceDockPanelID | null {
  return isSourceDockviewPanelID(panel?.id) ? panel.id : null;
}

function dockviewTabStackPanelID<PanelID extends string>(
  panel: IDockviewPanel | undefined,
  panelIDs: Set<PanelID>
): PanelID | null {
  const panelID = panel?.id;
  return typeof panelID === 'string' && panelIDs.has(panelID as PanelID)
    ? (panelID as PanelID)
    : null;
}
