import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';

import { createPerfHarnessMockPayload, type PerfHarnessMockPayload } from './perfHarnessMock.ts';

const HOST = '127.0.0.1';
const PORT = Number(process.env.MCB_PERF_PORT ?? '5181');
const APP_URL = `http://${HOST}:${PORT}/`;
const VIEWPORT = { width: 1710, height: 990 };
const PLAYWRIGHT_PATH = '/Users/blackcolours/.nvm/versions/node/v24.12.0/lib/node_modules/@playwright/cli/node_modules/playwright/index.mjs';
const SESSION_TAG = process.env.MCB_PERF_SESSION_TAG ?? 'perfharness20-chrome';
const CIRCUIT_SESSION_COUNT = 12;
const CIRCUIT_COUNT = Number(process.env.MCB_PERF_CIRCUITS ?? '10');

interface MemoryReceipt {
  phase: string;
  circuit: number;
  heapMiB: number;
  documents: number;
  nodes: number;
  listeners: number;
  tauriCallbacks: number;
}

interface SamplingNode {
  callFrame: { functionName: string; url: string; lineNumber: number };
  selfSize: number;
  children: SamplingNode[];
}

interface HeapSnapshot {
  snapshot: {
    meta: {
      node_fields: string[];
      node_types: unknown[][];
    };
  };
  nodes: number[];
  strings: string[];
}

interface HeapCount {
  count: number;
  selfSize: number;
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

function printTable<T extends Record<string, unknown>>(title: string, rows: T[]): void {
  process.stdout.write(`\n${title}\n`);
  console.table(rows);
}

function retainedAllocationSites(head: SamplingNode): Array<Record<string, unknown>> {
  const rows: Array<Record<string, unknown> & { bytes: number }> = [];
  const visit = (node: SamplingNode): void => {
    if (node.selfSize > 0) {
      rows.push({
        function: node.callFrame.functionName || '(anonymous)',
        file: node.callFrame.url
          ? `${node.callFrame.url.split('/').at(-1)}:${node.callFrame.lineNumber + 1}`
          : '(runtime)',
        bytes: node.selfSize
      });
    }
    for (const child of node.children) visit(child);
  };
  visit(head);
  return rows.sort((left, right) => right.bytes - left.bytes).slice(0, 20).map((row) => ({
    function: row.function,
    file: row.file,
    'retained KiB': round(row.bytes / 1024)
  }));
}

async function takeHeapSnapshot(cdp: { on(event: string, handler: (payload: { chunk: string }) => void): void; off(event: string, handler: (payload: { chunk: string }) => void): void; send(method: string, params?: Record<string, unknown>): Promise<unknown> }): Promise<HeapSnapshot> {
  const chunks: string[] = [];
  const onChunk = (payload: { chunk: string }): void => { chunks.push(payload.chunk); };
  cdp.on('HeapProfiler.addHeapSnapshotChunk', onChunk);
  try {
    await cdp.send('HeapProfiler.takeHeapSnapshot', { reportProgress: false });
  } finally {
    cdp.off('HeapProfiler.addHeapSnapshotChunk', onChunk);
  }
  return JSON.parse(chunks.join('')) as HeapSnapshot;
}

function aggregateHeapSnapshot(snapshot: HeapSnapshot): Map<string, HeapCount> {
  const fields = snapshot.snapshot.meta.node_fields;
  const fieldCount = fields.length;
  const typeOffset = fields.indexOf('type');
  const nameOffset = fields.indexOf('name');
  const selfSizeOffset = fields.indexOf('self_size');
  const typeNames = snapshot.snapshot.meta.node_types[typeOffset] as string[];
  const rows = new Map<string, HeapCount>();
  for (let offset = 0; offset < snapshot.nodes.length; offset += fieldCount) {
    const type = typeNames[snapshot.nodes[offset + typeOffset]] ?? 'unknown';
    const name = snapshot.strings[snapshot.nodes[offset + nameOffset]] ?? '';
    const key = `${type}:${name || '(anonymous)'}`;
    const row = rows.get(key) ?? { count: 0, selfSize: 0 };
    row.count += 1;
    row.selfSize += snapshot.nodes[offset + selfSizeOffset] ?? 0;
    rows.set(key, row);
  }
  return rows;
}

function heapCountDiff(before: Map<string, HeapCount>, after: Map<string, HeapCount>): Array<Record<string, unknown>> {
  const keys = new Set([...before.keys(), ...after.keys()]);
  return [...keys].map((key) => {
    const left = before.get(key) ?? { count: 0, selfSize: 0 };
    const right = after.get(key) ?? { count: 0, selfSize: 0 };
    return {
      node: key,
      'count delta': right.count - left.count,
      'self KiB delta': round((right.selfSize - left.selfSize) / 1024)
    };
  })
    .filter((row) => row['count delta'] !== 0 || row['self KiB delta'] !== 0)
    .sort((left, right) => Math.abs(Number(right['self KiB delta'])) - Math.abs(Number(left['self KiB delta'])))
    .slice(0, 30);
}

async function waitForVite(child: ChildProcess): Promise<void> {
  let output = '';
  let announcedReady = false;
  const deadline = Date.now() + 30_000;
  child.stdout?.on('data', (chunk: Buffer) => {
    output += chunk.toString();
    announcedReady ||= output.includes(`http://${HOST}:${PORT}/`);
  });
  child.stderr?.on('data', (chunk: Buffer) => { output += chunk.toString(); });
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Vite exited ${child.exitCode}:\n${output}`);
    if (announcedReady) {
      try {
        const response = await fetch(APP_URL);
        if (response.ok && child.exitCode === null) return;
      } catch {
        // This Vite announced first, but has not accepted its first request yet.
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Vite did not become ready on ${APP_URL}:\n${output}`);
}

async function stopProcessGroup(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null || child.pid === undefined) return;
  try {
    process.kill(-child.pid, 'SIGTERM');
  } catch {
    child.kill('SIGTERM');
  }
  await Promise.race([
    once(child, 'exit'),
    new Promise((resolve) => setTimeout(resolve, 3_000))
  ]);
  if (child.exitCode === null) {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {
      child.kill('SIGKILL');
    }
  }
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) {
    if ((await processGroupMembers(child.pid)).length === 0) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

async function processGroupMembers(processGroupId: number | undefined): Promise<string[]> {
  if (processGroupId === undefined) return [];
  const result = await new Promise<string>((resolve) => {
    const ps = spawn('ps', ['-axo', 'pid=,pgid=,command=']);
    let output = '';
    ps.stdout.on('data', (chunk: Buffer) => { output += chunk.toString(); });
    ps.on('exit', () => resolve(output));
  });
  return result.split('\n').filter((line) => {
    const fields = line.trim().split(/\s+/, 3);
    return Number(fields[1]) === processGroupId;
  });
}

function mockInitSource(payload: PerfHarnessMockPayload): string {
  return `(() => {
    const payload = ${JSON.stringify(payload)};
    const callbacks = new Map();
    let callbackId = 1;
    window.__perfHarnessMock = { callbackCount: () => callbacks.size };
    window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener() {} };
    window.__TAURI_INTERNALS__ = {
      transformCallback(callback, once = false) {
        const id = callbackId++;
        callbacks.set(id, { callback, once });
        return id;
      },
      unregisterCallback(id) { callbacks.delete(id); },
      convertFileSrc(path) { return path; },
      async invoke(command, args = {}) {
        switch (command) {
          case 'plugin:event|listen': return callbackId++;
          case 'plugin:event|unlisten': return true;
          case 'list_terminal_sessions': return [];
          case 'list_agent_conversation_sessions': return payload.sessions;
          case 'list_agent_sessions': return [];
          case 'read_agent_conversation_snapshot': return payload.snapshots[args.ownedId] || null;
          case 'list_agent_conversation_events': return payload.snapshots[args.ownedId]?.events || [];
          case 'read_agent_conversation_draft': return null;
          case 'read_session_browser_annotations': return [];
          case 'read_agent_conversation_config': {
            const session = payload.sessions.find((item) => item.ownedId === args.ownedId);
            return {
              model: session?.model || null,
              availableModels: session ? [session.model] : [],
              reasoningEffort: session?.effort || null,
              availableEfforts: ['low', 'medium', 'high'],
              approvalPolicy: 'on-request',
              availableApprovalPolicies: ['on-request', 'never']
            };
          }
          case 'read_agent_conversation_capabilities': return null;
          case 'list_runtime_contexts': return [];
          case 'read_resource_snapshot': return null;
          case 'read_usage_snapshot': return null;
          case 'list_project_worktrees': return [];
          case 'list_git_repository_summaries': return [];
          case 'read_project_git_status': return null;
          case 'backend_capabilities': return [];
          default: return null;
        }
      }
    };
  })();`;
}

async function sweepRail(page: { locator(selector: string): { count(): Promise<number>; nth(index: number): { hover(): Promise<void> } }; mouse: { move(x: number, y: number): Promise<void> } }): Promise<void> {
  const rows = page.locator('[data-testid="worktree-agent-row"]');
  const count = await rows.count();
  if (count < CIRCUIT_SESSION_COUNT) throw new Error(`Expected ${CIRCUIT_SESSION_COUNT} rows, found ${count}`);
  for (let index = 0; index < CIRCUIT_SESSION_COUNT; index += 1) {
    await page.mouse.move(1700, 980);
    await rows.nth(index).hover();
  }
  for (let index = CIRCUIT_SESSION_COUNT - 1; index >= 0; index -= 1) {
    await page.mouse.move(1700, 980);
    await rows.nth(index).hover();
  }
  await page.mouse.move(1700, 980);
}

async function switchSessions(page: { waitForTimeout(ms: number): Promise<void>; locator(selector: string): { count(): Promise<number>; nth(index: number): { locator(selector: string): { click(): Promise<void>; waitFor(options: { state: 'attached'; timeout: number }): Promise<void> } } } }): Promise<void> {
  const rows = page.locator('[data-testid="worktree-agent-row"]');
  const count = await rows.count();
  if (count < CIRCUIT_SESSION_COUNT) throw new Error(`Expected ${CIRCUIT_SESSION_COUNT} rows, found ${count}`);
  for (let index = 0; index < CIRCUIT_SESSION_COUNT; index += 1) {
    await rows.nth(index).locator('[data-testid="worktree-agent-select"]').click();
    await rows.nth(index).locator('[data-testid="worktree-agent-select"][aria-current="true"]')
      .waitFor({ state: 'attached', timeout: 30_000 });
    await page.waitForTimeout(25);
  }
  await rows.nth(0).locator('[data-testid="worktree-agent-select"]').click();
  await rows.nth(0).locator('[data-testid="worktree-agent-select"][aria-current="true"]')
    .waitFor({ state: 'attached', timeout: 30_000 });
  await page.waitForTimeout(25);
}

async function main(): Promise<number> {
  const viteArgs = process.env.MCB_PERF_PRODUCTION === '1'
    ? ['preview', '--host', HOST, '--port', String(PORT), '--strictPort']
    : ['--host', HOST, '--port', String(PORT), '--strictPort'];
  const vite = spawn('node_modules/.bin/vite', viteArgs, {
    cwd: process.cwd(),
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let browser: { close(): Promise<void> } | null = null;
  try {
    await waitForVite(vite);
    const playwright = await import(PLAYWRIGHT_PATH);
    browser = await playwright.chromium.launch({
      headless: true,
      channel: 'chrome',
      args: [`--${SESSION_TAG}`, '--enable-precise-memory-info', '--js-flags=--expose-gc']
    });
    const context = await browser.newContext({ viewport: VIEWPORT });
    const page = await context.newPage();
    const mockPayload = createPerfHarnessMockPayload();
    if (process.env.MCB_PERF_EMPTY_SNAPSHOTS === '1') mockPayload.snapshots = {};
    if (process.env.MCB_PERF_RAIL_ONLY === '1') {
      for (const session of mockPayload.sessions) {
        (session as { provider: string }).provider = 'other';
      }
      mockPayload.snapshots = {};
    }
    await page.addInitScript({ content: mockInitSource(mockPayload) });
    const consoleErrors: string[] = [];
    page.on('console', (message: { type(): string; text(): string }) => {
      if (message.type() === 'error') consoleErrors.push(message.text());
    });
    await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
    const initialRow = page.locator('[data-testid="worktree-agent-row"]').first();
    await initialRow.waitFor({ state: 'visible', timeout: 30_000 });
    await initialRow.locator('[data-testid="worktree-agent-select"]').click();
    await page.locator('[data-testid="worktree-agent-select"][aria-current="true"]')
      .waitFor({ state: 'attached', timeout: 30_000 });
    await initialRow.locator('[data-testid="worktree-agent-select"]').click({ button: 'right' });
    await page.locator('[data-testid="session-row-context-menu"]')
      .waitFor({ state: 'visible', timeout: 30_000 });
    await page.keyboard.press('Escape');
    await page.locator('[data-testid="session-row-context-menu"]')
      .waitFor({ state: 'detached', timeout: 30_000 });
    const actualViewport = await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }));
    if (actualViewport.width !== VIEWPORT.width || actualViewport.height !== VIEWPORT.height) {
      await page.setViewportSize(VIEWPORT);
    }
    const verifiedViewport = await page.evaluate(() => ({ width: window.innerWidth, height: window.innerHeight }));
    if (verifiedViewport.width !== VIEWPORT.width || verifiedViewport.height !== VIEWPORT.height) {
      throw new Error(`Viewport verification failed: ${JSON.stringify(verifiedViewport)}`);
    }

    const cdp = await context.newCDPSession(page);
    await cdp.send('HeapProfiler.enable');
    const exceptionStacks: Array<Record<string, unknown>> = [];
    if (process.env.MCB_PERF_TRACE_EXCEPTIONS === '1') {
      await cdp.send('Debugger.enable');
      await cdp.send('Debugger.setPauseOnExceptions', { state: 'all' });
      cdp.on('Debugger.paused', async (event: {
        reason: string;
        data?: { description?: string };
        callFrames: Array<{ functionName: string; url: string; location: { lineNumber: number; columnNumber: number } }>;
      }) => {
        if (exceptionStacks.length < 12) {
          exceptionStacks.push({
            reason: event.reason,
            error: event.data?.description ?? '(no description)',
            stack: event.callFrames.slice(0, 12).map((frame) =>
              `${frame.functionName || '(anonymous)'} @ ${frame.url}:${frame.location.lineNumber + 1}:${frame.location.columnNumber + 1}`
            ).join('\n')
          });
        }
        await cdp.send('Debugger.resume');
      });
    }
    const memoryReceipts: MemoryReceipt[] = [];
    const recordMemory = async (phase: string, circuit: number): Promise<void> => {
      await cdp.send('HeapProfiler.collectGarbage');
      await page.waitForTimeout(500);
      await cdp.send('HeapProfiler.collectGarbage');
      const heap = await cdp.send('Runtime.getHeapUsage') as { usedSize: number };
      const dom = await cdp.send('Memory.getDOMCounters') as {
        documents: number;
        nodes: number;
        jsEventListeners: number;
      };
      const tauriCallbacks = await page.evaluate(() => (
        window as unknown as { __perfHarnessMock: { callbackCount(): number } }
      ).__perfHarnessMock.callbackCount());
      memoryReceipts.push({
        phase,
        circuit,
        heapMiB: round(heap.usedSize / 1024 / 1024),
        documents: dom.documents,
        nodes: dom.nodes,
        listeners: dom.jsEventListeners,
        tauriCallbacks
      });
    };
    // Acceptance starts after every session in the circuit has been visited
    // once. Otherwise the first measured circuit includes one-time lazy module
    // and component initialization that repeated switching cannot reproduce.
    await sweepRail(page);
    await switchSessions(page);
    await recordMemory('warmed baseline', 0);
    for (let circuit = 1; circuit <= CIRCUIT_COUNT; circuit += 1) {
      await sweepRail(page);
      await recordMemory('mouse sweep', circuit);
    }
    const sampleAllocations = process.env.MCB_PERF_SAMPLE === '1';
    const snapshotDiff = process.env.MCB_PERF_SNAPSHOT_DIFF === '1';
    if (sampleAllocations) {
      await cdp.send('HeapProfiler.startSampling', {
        samplingInterval: 16_384,
        includeObjectsCollectedByMajorGC: false,
        includeObjectsCollectedByMinorGC: false
      });
    }
    const heapBefore = snapshotDiff ? aggregateHeapSnapshot(await takeHeapSnapshot(cdp)) : null;
    for (let circuit = 1; circuit <= CIRCUIT_COUNT; circuit += 1) {
      await switchSessions(page);
      await recordMemory('session switch', circuit);
    }
    const heapAfter = snapshotDiff ? aggregateHeapSnapshot(await takeHeapSnapshot(cdp)) : null;
    const sampling = sampleAllocations
      ? await cdp.send('HeapProfiler.stopSampling') as { profile: { head: SamplingNode } }
      : null;

    printTable('Forced-GC memory after deterministic rail circuits', memoryReceipts.map((receipt) => ({
      phase: receipt.phase,
      circuit: receipt.circuit,
      'heap MiB': receipt.heapMiB,
      documents: receipt.documents,
      nodes: receipt.nodes,
      listeners: receipt.listeners,
      'Tauri callbacks': receipt.tauriCallbacks
    })));
    if (heapBefore && heapAfter) printTable('Heap object-count diff after session switches', heapCountDiff(heapBefore, heapAfter));
    if (sampling) printTable('Live allocation sites after forced GC', retainedAllocationSites(sampling.profile.head));
    if (exceptionStacks.length > 0) printTable('Thrown exception stacks', exceptionStacks);
    printTable('Verified viewport', [{ width: verifiedViewport.width, height: verifiedViewport.height }]);
    if (consoleErrors.length > 0) printTable('Browser console errors', consoleErrors.map((message) => ({ message })));

    const baselineHeap = memoryReceipts[0]?.heapMiB ?? 0;
    const finalHeap = memoryReceipts.at(-1)?.heapMiB ?? baselineHeap;
    const failed = finalHeap > baselineHeap * 1.1;
    return failed ? 1 : 0;
  } finally {
    await browser?.close();
    await stopProcessGroup(vite);
    const browserLeftovers = await new Promise<string[]>((resolve) => {
      const ps = spawn('ps', ['-axo', 'pid=,command=']);
      let output = '';
      ps.stdout.on('data', (chunk: Buffer) => { output += chunk.toString(); });
      ps.on('exit', () => resolve(output.split('\n').filter((line) => line.includes(`--${SESSION_TAG}`))));
    });
    const viteLeftovers = await processGroupMembers(vite.pid);
    process.stdout.write(`\nBrowser cleanup: ${browserLeftovers.length === 0 ? `stopped ${SESSION_TAG} (Chrome helper tree exited)` : `not stopped - ${browserLeftovers.join(' | ')}`}\n`);
    process.stdout.write(`Browser cleanup: ${viteLeftovers.length === 0 ? `stopped perfharness20-vite (${PORT} process tree exited)` : `not stopped - ${viteLeftovers.join(' | ')}`}\n`);
  }
}

main().then((code) => { process.exitCode = code; }).catch((error: unknown) => {
  console.error(error instanceof Error ? error.stack : error);
  process.exitCode = 1;
});
