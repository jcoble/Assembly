export type SourceDockPanelID =
  | 'activity'
  | 'editor'
  | 'context'
  | 'insights'
  | 'terminal'
  | 'browser'
  | 'markdown';

export type SourceDockGroupID = 'left' | 'center' | 'right' | 'bottom';
export type SourceDockPresetID = 'review' | 'code' | 'git' | 'runs' | 'sessions' | 'custom';

export type SourceDockPanelDescriptor = {
  id: SourceDockPanelID;
  label: string;
  defaultGroupID: SourceDockGroupID;
  canHide: boolean;
};

export type SourceDockGroup = {
  id: SourceDockGroupID;
  panelIDs: SourceDockPanelID[];
  size: number;
};

export type SourceDockLayout = {
  preset: SourceDockPresetID;
  groups: SourceDockGroup[];
  hiddenPanelIDs: SourceDockPanelID[];
  activePanelByGroup: Partial<Record<SourceDockGroupID, SourceDockPanelID>>;
};

export const sourceDockPanelDescriptors: SourceDockPanelDescriptor[] = [
  { id: 'activity', label: 'Activity', defaultGroupID: 'left', canHide: true },
  { id: 'editor', label: 'Editor', defaultGroupID: 'center', canHide: false },
  { id: 'context', label: 'Context', defaultGroupID: 'right', canHide: true },
  { id: 'insights', label: 'Insights', defaultGroupID: 'right', canHide: true },
  { id: 'terminal', label: 'Terminal', defaultGroupID: 'center', canHide: true },
  { id: 'browser', label: 'Browser', defaultGroupID: 'center', canHide: true },
  { id: 'markdown', label: 'Markdown', defaultGroupID: 'center', canHide: true }
];

const dockGroupIDs: SourceDockGroupID[] = ['left', 'center', 'right', 'bottom'];
const centerRuntimePanelIDs: SourceDockPanelID[] = ['terminal', 'browser', 'markdown'];
const dockPanelIDs = sourceDockPanelDescriptors.map((panel) => panel.id);
const panelDescriptorByID = new Map(sourceDockPanelDescriptors.map((panel) => [panel.id, panel]));

export function createDefaultSourceDockLayout(): SourceDockLayout {
  return normalizeSourceDockLayout({
    preset: 'code',
    groups: [
      { id: 'left', panelIDs: ['activity'], size: 360 },
      { id: 'center', panelIDs: ['editor'], size: 1 },
      { id: 'right', panelIDs: ['context', 'insights'], size: 330 },
      { id: 'bottom', panelIDs: [], size: 260 }
    ],
    hiddenPanelIDs: ['terminal', 'browser', 'markdown'],
    activePanelByGroup: {
      left: 'activity',
      center: 'editor',
      right: 'context'
    }
  });
}

export function normalizeSourceDockLayout(value: unknown): SourceDockLayout {
  const candidate = isRecord(value) ? value : {};
  const hiddenPanelIDs = parsePanelIDList(candidate.hiddenPanelIDs)
    .filter((panelID) => panelDescriptorByID.get(panelID)?.canHide)
    .filter((panelID) => panelID !== 'editor');
  const hiddenPanelIDSet = new Set(hiddenPanelIDs);
  const seenPanelIDs = new Set<SourceDockPanelID>();
  const candidateGroups = Array.isArray(candidate.groups) ? candidate.groups : [];

  const groups = dockGroupIDs.map((groupID) => {
    const candidateGroup = candidateGroups.find(
      (group): group is Partial<SourceDockGroup> =>
        isRecord(group) && group.id === groupID
    );
    const panelIDs = parsePanelIDList(candidateGroup?.panelIDs).filter((panelID) => {
      if (panelID === 'editor') return false;
      if (hiddenPanelIDSet.has(panelID) || seenPanelIDs.has(panelID)) return false;
      seenPanelIDs.add(panelID);
      return true;
    });

    return {
      id: groupID,
      panelIDs,
      size: normalizeGroupSize(candidateGroup?.size, defaultGroupSize(groupID))
    };
  });

  const centerGroup = groupByID(groups, 'center');
  const bottomGroup = groupByID(groups, 'bottom');
  const bottomRuntimePanelIDs = bottomGroup.panelIDs.filter(isCenterRuntimePanelID);

  bottomGroup.panelIDs = bottomGroup.panelIDs.filter((panelID) => !isCenterRuntimePanelID(panelID));
  centerGroup.panelIDs = [
    'editor',
    ...centerGroup.panelIDs.filter((panelID) => panelID !== 'editor'),
    ...bottomRuntimePanelIDs.filter((panelID) => !centerGroup.panelIDs.includes(panelID))
  ];
  seenPanelIDs.add('editor');

  const activePanelByGroup = normalizeActivePanels(candidate.activePanelByGroup, groups);

  return {
    preset: isSourceDockPresetID(candidate.preset) ? candidate.preset : 'code',
    groups,
    hiddenPanelIDs,
    activePanelByGroup
  };
}

export function moveSourceDockPanel(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID,
  targetGroupID: SourceDockGroupID,
  targetIndex?: number
): SourceDockLayout {
  const normalizedTargetGroupID =
    isCenterRuntimePanelID(panelID) && targetGroupID === 'bottom' ? 'center' : targetGroupID;

  if (panelID === 'editor' && normalizedTargetGroupID !== 'center') return normalizeSourceDockLayout(layout);

  const normalized = normalizeSourceDockLayout(layout);
  const groups = normalized.groups.map((group) => ({
    ...group,
    panelIDs: group.panelIDs.filter((candidatePanelID) => candidatePanelID !== panelID)
  }));
  const targetGroup = groupByID(groups, normalizedTargetGroupID);
  const insertIndex = clampInsertIndex(targetIndex, targetGroup.panelIDs.length);
  targetGroup.panelIDs = [
    ...targetGroup.panelIDs.slice(0, insertIndex),
    panelID,
    ...targetGroup.panelIDs.slice(insertIndex)
  ];

  return normalizeSourceDockLayout({
    ...normalized,
    preset: 'custom',
    groups,
    hiddenPanelIDs: normalized.hiddenPanelIDs.filter((candidatePanelID) => candidatePanelID !== panelID),
    activePanelByGroup: {
      ...normalized.activePanelByGroup,
      [normalizedTargetGroupID]: panelID
    }
  });
}

export function hideSourceDockPanel(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID
): SourceDockLayout {
  const descriptor = panelDescriptorByID.get(panelID);
  if (!descriptor?.canHide) return normalizeSourceDockLayout(layout);

  const normalized = normalizeSourceDockLayout(layout);
  const hiddenPanelIDs = appendUnique(normalized.hiddenPanelIDs, panelID);
  const groups = normalized.groups.map((group) => ({
    ...group,
    panelIDs: group.panelIDs.filter((candidatePanelID) => candidatePanelID !== panelID)
  }));

  return normalizeSourceDockLayout({
    ...normalized,
    preset: 'custom',
    groups,
    hiddenPanelIDs,
    activePanelByGroup: normalized.activePanelByGroup
  });
}

export function showSourceDockPanel(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID
): SourceDockLayout {
  const descriptor = panelDescriptorByID.get(panelID);
  if (!descriptor) return normalizeSourceDockLayout(layout);

  const normalized = normalizeSourceDockLayout(layout);
  const withoutHiddenPanel = {
    ...normalized,
    hiddenPanelIDs: normalized.hiddenPanelIDs.filter((candidatePanelID) => candidatePanelID !== panelID)
  };

  return moveSourceDockPanel(withoutHiddenPanel, panelID, descriptor.defaultGroupID);
}

export function resizeSourceDockGroup(
  layout: SourceDockLayout,
  groupID: SourceDockGroupID,
  size: number
): SourceDockLayout {
  const normalized = normalizeSourceDockLayout(layout);
  const groups = normalized.groups.map((group) =>
    group.id === groupID
      ? { ...group, size: normalizeGroupSize(size, defaultGroupSize(groupID)) }
      : group
  );

  return normalizeSourceDockLayout({
    ...normalized,
    preset: 'custom',
    groups
  });
}

export function sourceDockGroupSize(
  layout: SourceDockLayout,
  groupID: SourceDockGroupID
): number {
  return groupByID(normalizeSourceDockLayout(layout).groups, groupID).size;
}

export function resizeSourceDockGroups(
  layout: SourceDockLayout,
  sizesByGroupID: Partial<Record<SourceDockGroupID, number>>
): SourceDockLayout {
  const normalized = normalizeSourceDockLayout(layout);
  let nextLayout = normalized;

  for (const groupID of dockGroupIDs) {
    const size = sizesByGroupID[groupID];
    if (size === undefined) continue;

    nextLayout = resizeSourceDockGroup(nextLayout, groupID, size);
  }

  return normalizeSourceDockLayout({
    ...nextLayout,
    preset: normalized.preset
  });
}

export function activateSourceDockPanel(
  layout: SourceDockLayout,
  panelID: SourceDockPanelID
): SourceDockLayout {
  const normalized = normalizeSourceDockLayout(layout);
  const group = normalized.groups.find((candidateGroup) => candidateGroup.panelIDs.includes(panelID));
  if (!group) return normalized;

  return normalizeSourceDockLayout({
    ...normalized,
    preset: 'custom',
    activePanelByGroup: {
      ...normalized.activePanelByGroup,
      [group.id]: panelID
    }
  });
}

export function visibleSourceDockPanelIDs(layout: SourceDockLayout): SourceDockPanelID[] {
  return normalizeSourceDockLayout(layout).groups.flatMap((group) => group.panelIDs);
}

function normalizeActivePanels(
  value: unknown,
  groups: SourceDockGroup[]
): Partial<Record<SourceDockGroupID, SourceDockPanelID>> {
  const candidate = isRecord(value) ? value : {};
  const activePanelByGroup: Partial<Record<SourceDockGroupID, SourceDockPanelID>> = {};

  for (const group of groups) {
    const candidatePanelID = candidate[group.id];
    activePanelByGroup[group.id] =
      isSourceDockPanelID(candidatePanelID) && group.panelIDs.includes(candidatePanelID)
        ? candidatePanelID
        : group.panelIDs[0];
  }

  return activePanelByGroup;
}

function parsePanelIDList(value: unknown): SourceDockPanelID[] {
  return Array.isArray(value)
    ? value.filter((panelID): panelID is SourceDockPanelID => isSourceDockPanelID(panelID))
    : [];
}

function isSourceDockPanelID(value: unknown): value is SourceDockPanelID {
  return typeof value === 'string' && dockPanelIDs.includes(value as SourceDockPanelID);
}

function isCenterRuntimePanelID(value: SourceDockPanelID) {
  return centerRuntimePanelIDs.includes(value);
}

function isSourceDockPresetID(value: unknown): value is SourceDockPresetID {
  return (
    value === 'review' ||
    value === 'code' ||
    value === 'git' ||
    value === 'runs' ||
    value === 'sessions' ||
    value === 'custom'
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function groupByID(groups: SourceDockGroup[], groupID: SourceDockGroupID): SourceDockGroup {
  const group = groups.find((candidateGroup) => candidateGroup.id === groupID);
  if (!group) throw new Error(`Missing source dock group ${groupID}`);
  return group;
}

function defaultGroupSize(groupID: SourceDockGroupID) {
  switch (groupID) {
    case 'left':
      return 360;
    case 'center':
      return 1;
    case 'right':
      return 330;
    case 'bottom':
      return 260;
  }
}

function normalizeGroupSize(value: unknown, fallback: number) {
  return typeof value === 'number' && Number.isFinite(value) && value > 0
    ? Math.round(value)
    : fallback;
}

function clampInsertIndex(value: unknown, length: number) {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.min(length, Math.max(0, Math.round(value)))
    : length;
}

function appendUnique<T>(values: T[], value: T) {
  return values.includes(value) ? values : [...values, value];
}
