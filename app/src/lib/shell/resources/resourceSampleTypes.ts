export type ResourceSampleProcess = {
  pid: number;
  name: string;
  cpuPercent: number;
  physicalFootprintBytes: number;
  rssBytes: number;
};

export type ResourceSampleAppPart = {
  label: string;
  pid: number;
  cpuPercent: number;
  physicalFootprintBytes: number;
  rssBytes: number;
};

export type ResourceSampleCategory = {
  label: string;
  cpuPercent: number;
  physicalFootprintBytes: number;
  rssBytes: number;
  processCount: number;
};

export type ResourceSampleTotals = {
  cpuPercent: number;
  physicalFootprintBytes: number;
  rssBytes: number;
  processCount: number;
  activeSourceDirectoryReads: number;
};

export type ResourceDiagnostics = {
  conversations: {
    durableSessionRows: number;
    liveSessionOverlays: number;
    liveRuntimeHandles: number;
    sidecarProcesses: number;
    activeTurns: number;
    pendingPermissions: number;
    pendingInputs: number;
    liveToolCalls: number;
    backgroundWork: number;
    suspendableSessions: number;
    adapterPools?: number;
  };
  database: {
    sessionStoreOpenHandles: number;
    sessionStoreActiveReads: number;
    sessionStoreActiveWrites: number;
  };
  terminals: {
    liveSessions: number;
    transcriptProjections: number;
    userPtys: number;
    agentToolPtys: number;
    runConfigurations: number;
    browserAutomations: number;
    exitedSessionsRetained: number;
  };
  streams: {
    workers: number;
    channels: number;
    queuedFrames: number;
    queuedBytes: number;
  };
  languageServers: {
    runningProcesses: number;
  };
  browser: {
    nativeViews: number;
  };
};

export type ResourceSampleSession = {
  ownedId?: string;
  label: string;
  kind: 'terminal' | 'conversation' | 'other';
  /** The process the app started for this session; every stop request names it. */
  rootPid: number;
  processes: ResourceSampleProcess[];
};

export type ResourceSampleGroup = {
  workspace: string;
  sessions: ResourceSampleSession[];
};

export type ResourceSample = {
  generatedAtMs: number;
  totals: ResourceSampleTotals;
  diagnostics: ResourceDiagnostics;
  app: {
    parts: ResourceSampleAppPart[];
  };
  processCategories: ResourceSampleCategory[];
  groups: ResourceSampleGroup[];
};
