import assert from 'node:assert/strict';
import {
  defaultSourceScanLimit,
  expandedSourceScanLimit,
  nativeSourceScanProgressEvent,
  validateProjectRootFromTauri,
  fetchGitRepositoryFromTauri,
  createSourceScanId,
  findSourceDefinitionsFromTauri,
  findSourceReferencesFromTauri,
  findSourceReferencesInRootFromTauri,
  findSourceLspDefinitionsFromTauri,
  findSourceLspCompletionsFromTauri,
  findSourceLspHoverFromTauri,
  findSourceLspReferencesFromTauri,
  findSourceLspSymbolsFromTauri,
  readSourceLspDiagnosticsFromTauri,
  readSourceLspReadinessFromTauri,
  readSourceLspStatusFromTauri,
  terminalOutputEvent,
  listenToTerminalOutput,
  listAgentSessionsFromTauri,
  listGitRepositorySummariesFromTauri,
  listOrchestrationRunsFromTauri,
  listProjectWorktreesFromTauri,
  removeProjectWorktreeFromTauri,
  archiveProjectWorktreeFromTauri,
  listTerminalSessionsFromTauri,
  listRuntimeContextsFromTauri,
  openTerminalCommandFromTauri,
  openTerminalPathFromTauri,
  readTerminalSessionScrollbackFromTauri,
  startTerminalSessionFromTauri,
  writeTerminalSessionFromTauri,
  resizeTerminalSessionFromTauri,
  closeTerminalSessionFromTauri,
  openPathFromTauri,
  readGitCommitHistoryFromTauri,
  readProjectGitStatusFromTauri,
  readSourceGitDiffFromTauri,
  revealPathFromTauri,
  searchSourceFilesFromTauri,
  stageGitPathsFromTauri,
  commitGitRepositoryFromTauri,
  pullGitRepositoryFromTauri,
  pushGitRepositoryFromTauri,
  unstageGitPathsFromTauri,
  writeSourceToTauri
} from '../src/lib/tauriSource.ts';

assert.equal(defaultSourceScanLimit, 10_000);
assert.equal(expandedSourceScanLimit, 25_000);
assert.equal(nativeSourceScanProgressEvent, 'source_scan_progress');
assert.equal(terminalOutputEvent, 'terminal_output');
assert.match(createSourceScanId(), /^source-scan-\d+-[a-z0-9]+$/);
assert.equal(
  await writeSourceToTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12
    },
    'export {};'
  ),
  null
);
assert.equal(await readProjectGitStatusFromTauri('/tmp/repo'), null);
assert.equal(await readSourceGitDiffFromTauri('/tmp/repo', '/tmp/repo/src/App.ts'), null);
assert.equal(await stageGitPathsFromTauri('/tmp/repo', ['src/App.ts']), null);
assert.equal(await unstageGitPathsFromTauri('/tmp/repo', ['src/App.ts']), null);
assert.equal(await commitGitRepositoryFromTauri('/tmp/repo', 'test commit'), null);
assert.equal(await fetchGitRepositoryFromTauri('/tmp/repo'), null);
assert.equal(await pullGitRepositoryFromTauri('/tmp/repo'), null);
assert.equal(await pushGitRepositoryFromTauri('/tmp/repo'), null);
assert.equal(await readGitCommitHistoryFromTauri('/tmp/repo'), null);
assert.equal(await openPathFromTauri('/tmp/repo'), false);
assert.equal(await revealPathFromTauri('/tmp/repo'), false);
assert.equal(await openTerminalPathFromTauri('/tmp/repo'), false);
assert.equal(await openTerminalCommandFromTauri('/tmp/repo', 'codex resume session-123'), false);
assert.equal(await startTerminalSessionFromTauri({ cwd: '/tmp/repo', cols: 96, rows: 24 }), null);
assert.equal(await listTerminalSessionsFromTauri(), null);
assert.equal(await readTerminalSessionScrollbackFromTauri('terminal-1'), null);
assert.equal(await writeTerminalSessionFromTauri('terminal-1', 'echo hi\n'), false);
assert.equal(await resizeTerminalSessionFromTauri('terminal-1', 100, 32), false);
assert.equal(await closeTerminalSessionFromTauri('terminal-1'), false);
assert.equal(await listenToTerminalOutput(() => {}), null);
assert.equal(await listAgentSessionsFromTauri(), null);
assert.equal(
  await listGitRepositorySummariesFromTauri([{ id: 'repo', name: 'Repo', path: '/tmp/repo' }]),
  null
);
assert.equal(await listProjectWorktreesFromTauri('/tmp/repo'), null);
assert.equal(await removeProjectWorktreeFromTauri('/tmp/repo', '/tmp/repo-worktree'), null);
assert.equal(await archiveProjectWorktreeFromTauri('/tmp/repo', '/tmp/repo-worktree'), null);
assert.equal(await validateProjectRootFromTauri('/tmp/repo'), null);
assert.equal(
  await listRuntimeContextsFromTauri([
    {
      id: 'repo',
      name: 'Repo',
      path: '/tmp/repo'
    }
  ]),
  null
);
assert.equal(
  await listOrchestrationRunsFromTauri([
    {
      id: 'repo',
      name: 'Repo',
      path: '/tmp/repo'
    }
  ]),
  null
);
assert.equal(
  await findSourceDefinitionsFromTauri(
    [
      {
        path: '/tmp/App.ts',
        relativePath: 'App.ts',
        fileName: 'App.ts',
        language: 'typescript',
        byteCount: 12
      }
    ],
    'App'
  ),
  null
);
assert.equal(
  await readSourceLspStatusFromTauri('/tmp/repo', 'typescript'),
  null
);
assert.equal(await readSourceLspReadinessFromTauri('/tmp/repo'), null);
assert.equal(
  await findSourceLspDefinitionsFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 14,
      limit: 20
    }
  ),
  null
);
assert.equal(
  await findSourceLspCompletionsFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 14,
      limit: 50
    }
  ),
  null
);
assert.equal(
  await findSourceLspReferencesFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 14,
      limit: 50
    }
  ),
  null
);
assert.equal(
  await findSourceLspHoverFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 14
    }
  ),
  null
);
assert.equal(
  await findSourceLspSymbolsFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 1,
      limit: 100
    }
  ),
  null
);
assert.equal(
  await readSourceLspDiagnosticsFromTauri(
    {
      path: '/tmp/App.ts',
      relativePath: 'App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 12,
      content: 'export class App {}',
      lineCount: 1
    },
    {
      root: '/tmp/repo',
      line: 1,
      column: 14
    }
  ),
  null
);
assert.equal(
  await findSourceReferencesFromTauri(
    [
      {
        path: '/tmp/App.ts',
        relativePath: 'App.ts',
        fileName: 'App.ts',
        language: 'typescript',
        byteCount: 12
      }
    ],
    'App'
  ),
  null
);
assert.equal(
  await findSourceReferencesInRootFromTauri('/tmp/project', 'App'),
  null
);
assert.equal(
  await searchSourceFilesFromTauri(
    [
      {
        path: '/tmp/App.ts',
        relativePath: 'App.ts',
        fileName: 'App.ts',
        language: 'typescript',
        byteCount: 12
      }
    ],
    'app'
  ),
  null
);

const { clearMocks, mockIPC } = await import('@tauri-apps/api/mocks');
const previousWindow = globalThis.window;
const ipcCalls = [];
const runtimeContextProjects = [{ id: 'repo', name: 'Repo', path: '/tmp/repo' }];

globalThis.window = previousWindow ?? globalThis;
mockIPC((cmd, args) => {
  ipcCalls.push({ cmd, args: structuredClone(args ?? {}) });
  switch (cmd) {
    case 'list_agent_sessions':
      return [
        {
          provider: 'codex',
          id: 'session-1',
          title: 'Codex session',
          resumeCommands: ['codex resume session-1']
        }
      ];
    case 'list_project_worktrees':
      return [
        {
          repo: 'repo',
          path: '/tmp/repo-worktree',
          branch: 'cdx/tsk-283-native-activity',
          isDirty: false,
          hasUnmergedCommits: false,
          deleteEligibility: 'requires-confirmation'
        }
      ];
    case 'list_runtime_contexts':
      return [
        {
          pid: 3456,
          command: 'node',
          port: 5173,
          cwd: '/tmp/repo',
          projectName: 'Repo',
          rootLabel: 'main checkout'
        }
      ];
    case 'list_orchestration_runs':
      return [
        {
          id: 'run-1',
          title: 'TSK-283 native activity',
          status: 'running',
          phase: 'implementation',
          progress: 42,
          projectName: 'Repo',
          projectPath: '/tmp/repo',
          rootLabel: 'main checkout',
          summary: 'Guard native activity cards',
          agents: [],
          steps: [],
          artifacts: [],
          links: [],
          events: []
        }
      ];
    case 'read_source_lsp_status':
      return {
        language: args.language,
        languageID: args.language,
        available: false,
        serverName: 'mock',
        command: 'mock',
        args: [],
        reason: null
      };
    case 'list_source_lsp_statuses':
    case 'find_source_lsp_symbols':
      return [];
    default:
      throw new Error(`Unexpected Tauri command: ${cmd}`);
  }
});

try {
  ipcCalls.length = 0;
  const nativeSessions = await listAgentSessionsFromTauri();
  const nativeWorktrees = await listProjectWorktreesFromTauri('/tmp/repo');
  const nativeRuntimeContexts = await listRuntimeContextsFromTauri(runtimeContextProjects);
  const nativeOrchestrationRuns = await listOrchestrationRunsFromTauri(runtimeContextProjects);

  assert.equal(nativeSessions.length, 1);
  assert.equal(nativeSessions[0].id, 'session-1');
  assert.equal(nativeWorktrees.length, 1);
  assert.equal(nativeWorktrees[0].branch, 'cdx/tsk-283-native-activity');
  assert.equal(nativeRuntimeContexts.length, 1);
  assert.equal(nativeRuntimeContexts[0].port, 5173);
  assert.equal(nativeOrchestrationRuns.length, 1);
  assert.equal(nativeOrchestrationRuns[0].id, 'run-1');
  assert.deepEqual(
    ipcCalls.map(({ cmd, args }) => ({ cmd, args })),
    [
      { cmd: 'list_agent_sessions', args: {} },
      { cmd: 'list_project_worktrees', args: { root: '/tmp/repo' } },
      { cmd: 'list_runtime_contexts', args: { projects: runtimeContextProjects } },
      { cmd: 'list_orchestration_runs', args: { projects: runtimeContextProjects } }
    ]
  );

  const targetStatusLanguages = ['svelte', 'typescript', 'tsx', 'javascript', 'jsx', 'rust'];
  ipcCalls.length = 0;
  for (const language of targetStatusLanguages) {
    await readSourceLspStatusFromTauri('/tmp/repo', language);
  }
  assert.deepEqual(
    ipcCalls.map(({ cmd, args }) => ({ cmd, root: args.root, language: args.language })),
    targetStatusLanguages.map((language) => ({
      cmd: 'read_source_lsp_status',
      root: '/tmp/repo',
      language
    }))
  );

  ipcCalls.length = 0;
  await readSourceLspReadinessFromTauri('/tmp/repo');
  assert.deepEqual(ipcCalls, [
    { cmd: 'list_source_lsp_statuses', args: { root: '/tmp/repo' } }
  ]);

  ipcCalls.length = 0;
  const targetPreviews = [
    {
      path: '/tmp/repo/src/App.svelte',
      relativePath: 'src/App.svelte',
      fileName: 'App.svelte',
      language: 'svelte',
      byteCount: 20,
      content: '<script>let value = 1;</script>',
      lineCount: 1
    },
    {
      path: '/tmp/repo/src/App.ts',
      relativePath: 'src/App.ts',
      fileName: 'App.ts',
      language: 'typescript',
      byteCount: 20,
      content: 'export const value = 1;',
      lineCount: 1
    },
    {
      path: '/tmp/repo/src/App.tsx',
      relativePath: 'src/App.tsx',
      fileName: 'App.tsx',
      language: 'tsx',
      byteCount: 20,
      content: 'export const value = <div />;',
      lineCount: 1
    },
    {
      path: '/tmp/repo/src/App.js',
      relativePath: 'src/App.js',
      fileName: 'App.js',
      language: 'javascript',
      byteCount: 20,
      content: 'export const value = 1;',
      lineCount: 1
    },
    {
      path: '/tmp/repo/src/App.jsx',
      relativePath: 'src/App.jsx',
      fileName: 'App.jsx',
      language: 'jsx',
      byteCount: 20,
      content: 'export const value = <div />;',
      lineCount: 1
    },
    {
      path: '/tmp/repo/src/lib.rs',
      relativePath: 'src/lib.rs',
      fileName: 'lib.rs',
      language: 'rust',
      byteCount: 20,
      content: 'pub fn value() -> i32 { 1 }',
      lineCount: 1
    }
  ];
  const lspLookupRequest = { root: '/tmp/repo', line: 1, column: 1, limit: 25 };

  for (const preview of targetPreviews) {
    await findSourceLspSymbolsFromTauri(preview, lspLookupRequest);
  }

  assert.deepEqual(
    ipcCalls.map(({ cmd, args }) => ({
      cmd,
      language: args.preview.language,
      fileName: args.preview.fileName,
      request: args.request
    })),
    targetPreviews.map((preview) => ({
      cmd: 'find_source_lsp_symbols',
      language: preview.language,
      fileName: preview.fileName,
      request: lspLookupRequest
    }))
  );
} finally {
  clearMocks();
  if (previousWindow === undefined) {
    delete globalThis.window;
  } else {
    globalThis.window = previousWindow;
  }
}
