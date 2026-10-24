/**
 * Current development-only resource counts.
 *
 * This intentionally keeps no samples or history. It only mirrors the resources
 * whose lifecycle is explicit enough to count from create/dispose points.
 */
export const resourceDiagnostics = $state({
  loadedConversationProjections: 0,
  loadedConversationEventCount: 0,
  conversationSnapshotReadsInFlight: 0,
  conversationSnapshotReadsInvalidated: 0,
  loadedChildTranscriptBytes: 0,
  conversationRenderedRows: 0,
  conversationVirtualRows: 0,
  conversationMeasuredElementCacheEntries: 0,
  conversationMeasuredSizeCacheEntries: 0,
  conversationTimelineItems: 0,
  conversationMessageRenderers: 0,
  sentAttachmentMapEntries: 0,
  sentAttachmentCount: 0,
  tauriRootListeners: 0,
  tauriEventSubscribers: 0,
  tauriChannels: 0,
  fileWatchers: 0,
  objectUrls: 0,
  attachmentObjectUrls: 0,
  animationFrames: 0,
  loadedTreeNodes: 0,
  loadedGitHistoryRows: 0,
  activeDiffs: 0,
  terminalViews: 0,
  codeMirrorEditorViews: 0,
  codeMirrorEditorStates: 0,
  codeMirrorDocBytes: 0,
  codeMirrorUndoDepth: 0,
  openTabDocumentBytes: 0,
  editorSourceReadsInFlight: 0,
  editorSourceReadsInvalidated: 0,
  editorSourceReadBytesInFlight: 0,
  elementVisibilityWatchers: 0,
  elementVisibilityObserverActive: false,
  railElapsedWatchers: 0,
  railElapsedIntervalMs: null as number | null,
  semanticWaitingSpots: 0,
  semanticSchedulerWaiting: 0,
  semanticSchedulerInFlight: 0,
  semanticReferenceRequests: 0,
  semanticRetryTimers: 0,
  semanticHeldUntilReady: 0,
  semanticRememberedTargets: 0,
  sourcePreviewCacheEntries: 0,
  rememberedReferenceCountEntries: 0
});

type ProfileMetadata = Record<string, string | number | boolean | null>;

type AssemblyResourceProfiler = {
  enable(): void;
  disable(): void;
  sample(label?: string): void;
  heap(label?: string): void;
  isEnabled(): boolean;
};

const resourceDiagnosticsDev =
  (import.meta as ImportMeta & { env?: { DEV?: boolean } }).env?.DEV === true;
let resourceProfilingEnabled = resourceDiagnosticsDev;
let resourceProfileSequence = 0;

function profileLine(label: string, metadata: ProfileMetadata = {}): string {
  return JSON.stringify({
    sequence: ++resourceProfileSequence,
    label,
    ...metadata,
    resources: resourceDiagnostics
  });
}

/**
 * A development-only lifecycle checkpoint. The console receives one serialized
 * string rather than live objects, so Web Inspector cannot keep an old session
 * projection reachable through its console history.
 */
export function profileResourceLifecycle(
  label: string,
  metadata: ProfileMetadata = {}
): void {
  if (!resourceProfilingEnabled) return;
  console.info(`[Assembly profile] ${profileLine(label, metadata)}`);
}

/**
 * WebKit performs a full GC before this snapshot. The call is inert when Web
 * Inspector is closed, and the app stores no snapshots or sample history.
 */
export function takeResourceHeapSnapshot(label = 'manual'): void {
  if (!resourceProfilingEnabled) return;
  profileResourceLifecycle(`heap:${label}`);
  const inspectorConsole = console as Console & {
    takeHeapSnapshot?: (snapshotLabel?: string) => void;
  };
  inspectorConsole.takeHeapSnapshot?.(`Assembly ${resourceProfileSequence}: ${label}`);
}

if (typeof window !== 'undefined') {
  const profiler: AssemblyResourceProfiler = Object.freeze({
    enable() {
      resourceProfilingEnabled = true;
      profileResourceLifecycle('profiling:enabled');
    },
    disable() {
      profileResourceLifecycle('profiling:disabled');
      resourceProfilingEnabled = false;
    },
    sample(label = 'manual') {
      profileResourceLifecycle(`sample:${label}`);
    },
    heap(label = 'manual') {
      takeResourceHeapSnapshot(label);
    },
    isEnabled() {
      return resourceProfilingEnabled;
    }
  });
  Object.defineProperty(window, '__assemblyProfile', {
    value: profiler,
    configurable: true
  });
}

export function setConversationProjectionDiagnostics(
  projections: number,
  eventCount: number,
  childTranscriptBytes: number
): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.loadedConversationProjections = Math.max(0, projections);
  resourceDiagnostics.loadedConversationEventCount = Math.max(0, Math.trunc(eventCount));
  resourceDiagnostics.loadedChildTranscriptBytes = Math.max(
    0,
    Math.trunc(childTranscriptBytes)
  );
}

export function setConversationSnapshotReadDiagnostics(count: number, invalidated: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.conversationSnapshotReadsInFlight = Math.max(0, Math.trunc(count));
  resourceDiagnostics.conversationSnapshotReadsInvalidated = Math.max(0, Math.trunc(invalidated));
}

export function setConversationTimelineDiagnostics(
  renderedRows: number,
  virtualRows: number,
  measuredElementCacheEntries: number,
  measuredSizeCacheEntries: number
): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.conversationRenderedRows = Math.max(0, Math.trunc(renderedRows));
  resourceDiagnostics.conversationVirtualRows = Math.max(0, Math.trunc(virtualRows));
  resourceDiagnostics.conversationMeasuredElementCacheEntries = Math.max(
    0,
    Math.trunc(measuredElementCacheEntries)
  );
  resourceDiagnostics.conversationMeasuredSizeCacheEntries = Math.max(
    0,
    Math.trunc(measuredSizeCacheEntries)
  );
}

function trackConversationComponent(key: 'conversationTimelineItems' | 'conversationMessageRenderers'): () => void {
  if (!resourceDiagnosticsDev) return () => {};
  resourceDiagnostics[key] += 1;
  return () => {
    resourceDiagnostics[key] = Math.max(0, resourceDiagnostics[key] - 1);
  };
}

export function trackConversationTimelineItem(): () => void {
  return trackConversationComponent('conversationTimelineItems');
}

export function trackConversationMessageRenderer(): () => void {
  return trackConversationComponent('conversationMessageRenderers');
}

export function setSentAttachmentDiagnostics(
  mapEntries: number,
  attachmentCount: number
): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.sentAttachmentMapEntries = Math.max(0, Math.trunc(mapEntries));
  resourceDiagnostics.sentAttachmentCount = Math.max(0, Math.trunc(attachmentCount));
}

export function setLoadedTreeNodes(nodes: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.loadedTreeNodes = Math.max(0, nodes);
}

export function setGitSurfaceDiagnostics(historyRows: number, activeDiffs: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.loadedGitHistoryRows = Math.max(0, historyRows);
  resourceDiagnostics.activeDiffs = Math.max(0, activeDiffs);
}

export function trackTauriListener(unlisten: () => void): () => void {
  if (!resourceDiagnosticsDev) return unlisten;
  resourceDiagnostics.tauriRootListeners += 1;
  let listening = true;
  return () => {
    if (!listening) return;
    listening = false;
    try {
      unlisten();
    } finally {
      resourceDiagnostics.tauriRootListeners = Math.max(
        0,
        resourceDiagnostics.tauriRootListeners - 1
      );
    }
  };
}

export function trackTauriSubscriber(unsubscribe: () => void): () => void {
  if (resourceDiagnosticsDev) resourceDiagnostics.tauriEventSubscribers += 1;
  let subscribed = true;
  return () => {
    if (!subscribed) return;
    subscribed = false;
    try {
      unsubscribe();
    } finally {
      if (resourceDiagnosticsDev) {
        resourceDiagnostics.tauriEventSubscribers = Math.max(
          0,
          resourceDiagnostics.tauriEventSubscribers - 1
        );
      }
    }
  };
}

export function trackTauriChannel(): () => void {
  if (resourceDiagnosticsDev) resourceDiagnostics.tauriChannels += 1;
  let active = true;
  return () => {
    if (!active) return;
    active = false;
    if (resourceDiagnosticsDev) {
      resourceDiagnostics.tauriChannels = Math.max(0, resourceDiagnostics.tauriChannels - 1);
    }
  };
}

export function trackFileWatcher(unwatch: () => void): () => void {
  if (!resourceDiagnosticsDev) return unwatch;
  resourceDiagnostics.fileWatchers += 1;
  let watching = true;
  return () => {
    if (!watching) return;
    watching = false;
    try {
      unwatch();
    } finally {
      resourceDiagnostics.fileWatchers = Math.max(0, resourceDiagnostics.fileWatchers - 1);
    }
  };
}

type ObjectUrlOwner = 'attachment' | 'other';

const trackedObjectUrls = new Map<string, ObjectUrlOwner>();

function publishObjectUrlDiagnostics(): void {
  resourceDiagnostics.objectUrls = trackedObjectUrls.size;
  resourceDiagnostics.attachmentObjectUrls = [...trackedObjectUrls.values()]
    .filter((owner) => owner === 'attachment').length;
}

export function createTrackedObjectUrl(
  value: Blob | MediaSource,
  owner: ObjectUrlOwner = 'other'
): string {
  const url = URL.createObjectURL(value);
  if (resourceDiagnosticsDev) {
    trackedObjectUrls.set(url, owner);
    publishObjectUrlDiagnostics();
  }
  return url;
}

export function revokeTrackedObjectUrl(url: string): void {
  URL.revokeObjectURL(url);
  if (!resourceDiagnosticsDev) return;
  trackedObjectUrls.delete(url);
  publishObjectUrlDiagnostics();
}

export function markFrameDiagnostics(): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.animationFrames = 0;
}

export function textBytes(text: string): number {
  if (!resourceDiagnosticsDev) return 0;
  if (typeof TextEncoder === 'undefined') return text.length;
  return new TextEncoder().encode(text).byteLength;
}

export function addTerminalView(delta: 1 | -1): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.terminalViews = Math.max(0, resourceDiagnostics.terminalViews + delta);
}

export function addCodeMirrorEditorView(delta: 1 | -1): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.codeMirrorEditorViews = Math.max(
    0,
    resourceDiagnostics.codeMirrorEditorViews + delta
  );
}

export function setCodeMirrorEditorStateCount(count: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.codeMirrorEditorStates = Math.max(0, Math.trunc(count));
}

export function setCodeMirrorDocBytes(bytes: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.codeMirrorDocBytes = Math.max(0, Math.trunc(bytes));
}

export function setCodeMirrorUndoDepth(depth: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.codeMirrorUndoDepth = Math.max(0, Math.trunc(depth));
}

export function setOpenTabDocumentBytes(bytes: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.openTabDocumentBytes = Math.max(0, Math.trunc(bytes));
}

export function setEditorSourceReadDiagnostics(count: number, invalidated: number, bytes: number): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.editorSourceReadsInFlight = Math.max(0, Math.trunc(count));
  resourceDiagnostics.editorSourceReadsInvalidated = Math.max(0, Math.trunc(invalidated));
  resourceDiagnostics.editorSourceReadBytesInFlight = Math.max(0, Math.trunc(bytes));
}

export function setElementVisibilityDiagnostics(watchers: number, observerActive: boolean): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.elementVisibilityWatchers = Math.max(0, watchers);
  resourceDiagnostics.elementVisibilityObserverActive = observerActive;
}

export function setRailElapsedDiagnostics(watchers: number, intervalMs: number | null): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.railElapsedWatchers = Math.max(0, watchers);
  resourceDiagnostics.railElapsedIntervalMs = intervalMs;
}

export function setSourceIntelligenceDiagnostics(values: {
  semanticWaitingSpots: number;
  semanticSchedulerWaiting: number;
  semanticSchedulerInFlight: number;
  semanticReferenceRequests: number;
  semanticRetryTimers: number;
  semanticHeldUntilReady: number;
  semanticRememberedTargets: number;
  sourcePreviewCacheEntries: number;
  rememberedReferenceCountEntries: number;
}): void {
  if (!resourceDiagnosticsDev) return;
  resourceDiagnostics.semanticWaitingSpots = Math.max(0, values.semanticWaitingSpots);
  resourceDiagnostics.semanticSchedulerWaiting = Math.max(0, values.semanticSchedulerWaiting);
  resourceDiagnostics.semanticSchedulerInFlight = Math.max(0, values.semanticSchedulerInFlight);
  resourceDiagnostics.semanticReferenceRequests = Math.max(0, values.semanticReferenceRequests);
  resourceDiagnostics.semanticRetryTimers = Math.max(0, values.semanticRetryTimers);
  resourceDiagnostics.semanticHeldUntilReady = Math.max(0, values.semanticHeldUntilReady);
  resourceDiagnostics.semanticRememberedTargets = Math.max(0, values.semanticRememberedTargets);
  resourceDiagnostics.sourcePreviewCacheEntries = Math.max(0, values.sourcePreviewCacheEntries);
  resourceDiagnostics.rememberedReferenceCountEntries = Math.max(
    0,
    values.rememberedReferenceCountEntries
  );
}
