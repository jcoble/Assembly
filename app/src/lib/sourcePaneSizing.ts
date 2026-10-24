export type SourcePaneState = 'expanded' | 'rail' | 'collapsed';

export type SourcePaneSizingConfig = {
  defaultSize: number;
  minSize: number;
  maxSize: number;
  collapseThreshold: number;
  railThreshold?: number;
  railSize?: number;
};

export type SourcePaneStateInput = {
  visible: boolean;
  size: unknown;
};

export type SourcePaneRestoreOptions = {
  previousExpandedSize?: unknown;
};

export type SourcePaneSizingResult = {
  state: SourcePaneState;
  size: number;
  persistedSize: number;
};

export type SourcePaneWorkspaceItem<TID extends string = string> = {
  id: TID;
  visible: boolean;
  size: unknown;
  config: SourcePaneSizingConfig;
  collapsePriority?: number;
  previousExpandedSize?: unknown;
};

export type SourcePaneWorkspacePlanItem<TID extends string = string> = SourcePaneSizingResult & {
  id: TID;
  requestedSize: number;
  restoreSize: number;
  reason: 'requested' | 'viewport-rail' | 'viewport-collapsed';
};

export type SourcePaneWorkspacePlan<TID extends string = string> = {
  viewportSize: number;
  minEditorSize: number;
  gapSize: number;
  chromeSize: number;
  editorSize: number;
  overflowSize: number;
  items: SourcePaneWorkspacePlanItem<TID>[];
};

export type SourcePaneWorkspacePlanOptions<TID extends string = string> = {
  viewportSize: unknown;
  minEditorSize: unknown;
  gapSize?: unknown;
  chromeSize?: unknown;
  items: SourcePaneWorkspaceItem<TID>[];
};

type NormalizedSourcePaneSizingConfig = {
  defaultSize: number;
  minSize: number;
  maxSize: number;
  collapseThreshold: number;
  railThreshold?: number;
  railSize?: number;
};

export function clampSourcePaneSize(size: unknown, config: SourcePaneSizingConfig): number {
  const normalizedConfig = normalizeSourcePaneSizingConfig(config);
  return clampToConfigSize(size, normalizedConfig);
}

export function deriveSourcePaneState(
  input: SourcePaneStateInput,
  config: SourcePaneSizingConfig
): SourcePaneState {
  const normalizedConfig = normalizeSourcePaneSizingConfig(config);
  if (!input.visible) return 'collapsed';
  return visibleSourcePaneState(input.size, normalizedConfig);
}

export function resolveSourcePaneSize(
  input: SourcePaneStateInput,
  config: SourcePaneSizingConfig,
  options: SourcePaneRestoreOptions = {}
): SourcePaneSizingResult {
  const normalizedConfig = normalizeSourcePaneSizingConfig(config);

  if (!input.visible) {
    return {
      state: 'collapsed',
      size: 0,
      persistedSize: restoreExpandedSize(input.size, normalizedConfig, options)
    };
  }

  const size = clampToConfigSize(input.size, normalizedConfig);

  return {
    state: visibleSourcePaneState(size, normalizedConfig),
    size,
    persistedSize: size
  };
}

export function finishSourcePanePointerSize(
  rawSize: unknown,
  config: SourcePaneSizingConfig,
  options: SourcePaneRestoreOptions = {}
): SourcePaneSizingResult {
  const normalizedConfig = normalizeSourcePaneSizingConfig(config);
  const numericRawSize = finiteNumber(rawSize, normalizedConfig.defaultSize);

  if (numericRawSize <= normalizedConfig.collapseThreshold) {
    return {
      state: 'collapsed',
      size: 0,
      persistedSize: restoreExpandedSize(numericRawSize, normalizedConfig, options)
    };
  }

  const size = clampToConfigSize(numericRawSize, normalizedConfig);
  const railSize = finishRailSize(size, normalizedConfig);

  if (railSize !== null) {
    return {
      state: 'rail',
      size: railSize,
      persistedSize: railSize
    };
  }

  return {
    state: visibleSourcePaneState(size, normalizedConfig),
    size,
    persistedSize: size
  };
}

export function restoreSourcePaneExpandedSize(
  currentSize: unknown,
  config: SourcePaneSizingConfig,
  options: SourcePaneRestoreOptions = {}
): number {
  return restoreExpandedSize(currentSize, normalizeSourcePaneSizingConfig(config), options);
}

export function resolveSourcePaneWorkspacePlan<TID extends string = string>(
  options: SourcePaneWorkspacePlanOptions<TID>
): SourcePaneWorkspacePlan<TID> {
  type InternalSourcePaneWorkspacePlanItem = SourcePaneWorkspacePlanItem<TID> & {
    collapsePriority: number;
    normalizedConfig: NormalizedSourcePaneSizingConfig;
  };

  const viewportSize = Math.max(0, Math.round(finiteNumber(options.viewportSize, 0)));
  const minEditorSize = Math.max(0, Math.round(finiteNumber(options.minEditorSize, 0)));
  const gapSize = Math.max(0, Math.round(finiteNumber(options.gapSize, 0)));
  const chromeSize = Math.max(0, Math.round(finiteNumber(options.chromeSize, 0)));
  const items: InternalSourcePaneWorkspacePlanItem[] = options.items.map((item, index) => {
    const normalizedConfig = normalizeSourcePaneSizingConfig(item.config);
    const resolved = resolveSourcePaneSize(
      { visible: item.visible, size: item.size },
      item.config,
      { previousExpandedSize: item.previousExpandedSize }
    );
    const requestedSize = resolved.size;
    const restoreSize = restoreExpandedSize(item.size, normalizedConfig, {
      previousExpandedSize: item.previousExpandedSize
    });

    const reason: SourcePaneWorkspacePlanItem<TID>['reason'] = 'requested';

    return {
      id: item.id,
      state: resolved.state,
      size: resolved.size,
      persistedSize: resolved.persistedSize,
      requestedSize,
      restoreSize,
      reason,
      collapsePriority: finiteNumber(item.collapsePriority, index),
      normalizedConfig
    };
  });

  let overflowSize = workspaceOverflowSize(items, viewportSize, minEditorSize, gapSize, chromeSize);
  if (overflowSize > 0) {
    for (const item of shrinkCandidates(items)) {
      if (overflowSize <= 0) break;
      const railSize = item.normalizedConfig.railSize ?? item.normalizedConfig.minSize;
      if (item.state !== 'expanded' || railSize >= item.size) continue;

      item.state = 'rail';
      item.size = railSize;
      item.persistedSize = railSize;
      item.reason = 'viewport-rail';
      overflowSize = workspaceOverflowSize(items, viewportSize, minEditorSize, gapSize, chromeSize);
    }
  }

  if (overflowSize > 0) {
    for (const item of shrinkCandidates(items)) {
      if (overflowSize <= 0) break;
      if (item.state === 'collapsed' || item.size <= 0) continue;

      item.state = 'collapsed';
      item.size = 0;
      item.persistedSize = item.restoreSize;
      item.reason = 'viewport-collapsed';
      overflowSize = workspaceOverflowSize(items, viewportSize, minEditorSize, gapSize, chromeSize);
    }
  }

  const publicItems = items.map(({ normalizedConfig, collapsePriority, ...item }) => item);
  const usedSize = workspaceUsedSize(publicItems, gapSize, chromeSize);
  return {
    viewportSize,
    minEditorSize,
    gapSize,
    chromeSize,
    editorSize: Math.max(0, viewportSize - usedSize),
    overflowSize: Math.max(0, minEditorSize + usedSize - viewportSize),
    items: publicItems
  };
}

function normalizeSourcePaneSizingConfig(
  config: SourcePaneSizingConfig
): NormalizedSourcePaneSizingConfig {
  const minSize = Math.max(0, Math.round(finiteNumber(config.minSize, 0)));
  const maxSize = Math.max(minSize, Math.round(finiteNumber(config.maxSize, minSize)));
  const defaultSize = clampNumber(finiteNumber(config.defaultSize, minSize), minSize, maxSize);
  const collapseThreshold = Math.round(finiteNumber(config.collapseThreshold, minSize));
  const railThreshold = finiteOptionalNumber(config.railThreshold);
  const normalizedRailThreshold =
    railThreshold !== undefined && railThreshold > collapseThreshold ? Math.round(railThreshold) : undefined;
  const railSize = finiteOptionalNumber(config.railSize);
  const normalizedRailSize =
    normalizedRailThreshold !== undefined && railSize !== undefined
      ? clampNumber(railSize, minSize, maxSize)
      : undefined;

  const normalizedConfig: NormalizedSourcePaneSizingConfig = {
    defaultSize,
    minSize,
    maxSize,
    collapseThreshold
  };

  if (normalizedRailThreshold !== undefined) {
    normalizedConfig.railThreshold = normalizedRailThreshold;
    if (normalizedRailSize !== undefined && normalizedRailSize <= normalizedRailThreshold) {
      normalizedConfig.railSize = normalizedRailSize;
    }
  }

  return normalizedConfig;
}

function visibleSourcePaneState(
  size: unknown,
  config: NormalizedSourcePaneSizingConfig
): SourcePaneState {
  const numericSize = finiteNumber(size, config.defaultSize);
  if (config.railThreshold !== undefined && numericSize <= config.railThreshold) return 'rail';
  return 'expanded';
}

function restoreExpandedSize(
  currentSize: unknown,
  config: NormalizedSourcePaneSizingConfig,
  options: SourcePaneRestoreOptions
): number {
  const previousSize = expandedSize(options.previousExpandedSize, config);
  if (previousSize !== null) return previousSize;

  const currentExpandedSize = expandedSize(currentSize, config);
  return currentExpandedSize ?? config.defaultSize;
}

function expandedSize(size: unknown, config: NormalizedSourcePaneSizingConfig): number | null {
  const clampedSize = clampToConfigSize(size, config);
  return visibleSourcePaneState(clampedSize, config) === 'expanded' ? clampedSize : null;
}

function finishRailSize(size: unknown, config: NormalizedSourcePaneSizingConfig): number | null {
  if (config.railSize === undefined) return null;
  return visibleSourcePaneState(size, config) === 'rail' ? config.railSize : null;
}

function workspaceOverflowSize(
  items: Array<{ size: number }>,
  viewportSize: number,
  minEditorSize: number,
  gapSize: number,
  chromeSize: number
): number {
  return Math.max(0, workspaceUsedSize(items, gapSize, chromeSize) + minEditorSize - viewportSize);
}

function workspaceUsedSize(
  items: Array<{ size: number }>,
  gapSize: number,
  chromeSize: number
): number {
  const visibleItems = items.filter((item) => item.size > 0);
  return (
    visibleItems.reduce((total, item) => total + item.size, 0) +
    visibleItems.length * (gapSize + chromeSize)
  );
}

function shrinkCandidates<TItem extends { collapsePriority: number; id: string }>(
  items: TItem[]
): TItem[] {
  return [...items].sort((a, b) => {
    const priorityDelta = a.collapsePriority - b.collapsePriority;
    return priorityDelta === 0 ? a.id.localeCompare(b.id) : priorityDelta;
  });
}

function clampToConfigSize(size: unknown, config: NormalizedSourcePaneSizingConfig): number {
  return clampNumber(finiteNumber(size, config.defaultSize), config.minSize, config.maxSize);
}

function clampNumber(size: number, minSize: number, maxSize: number): number {
  return Math.min(maxSize, Math.max(minSize, Math.round(size)));
}

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function finiteOptionalNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}
