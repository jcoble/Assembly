import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';

const HOST = '127.0.0.1';
const PORT = 5182;
const APP_URL = `http://${HOST}:${PORT}/`;
const VIEWPORT = { width: 1710, height: 990 };
const SESSION_TAG = 'historybound21-chrome';
const PLAYWRIGHT_PATH = '/Users/blackcolours/.nvm/versions/node/v24.12.0/lib/node_modules/@playwright/cli/node_modules/playwright/index.mjs';
const SCREENSHOT_PATH = 'output/playwright/historybound21-bounded-history.png';

interface SeedSession {
  provider: string;
  id: string;
  title: string;
  description: string;
  model: null;
  projectPath: string;
  lastActivity: string;
  resumeCommands: string[];
  messageCount: number;
  latestTurnPreview: string;
}

function seedSessions(): SeedSession[] {
  return Array.from({ length: 4_000 }, (_, index) => ({
    provider: 'local',
    id: `history-${index}`,
    title: `Historical session ${index + 1}`,
    description: `Bounded history receipt ${index + 1}`,
    model: null,
    projectPath: '/Users/dev/work/worktrees/atlas/historybound',
    lastActivity: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString(),
    resumeCommands: [],
    messageCount: index + 1,
    latestTurnPreview: `Stored turn ${index + 1}`
  }));
}

async function waitForVite(child: ChildProcess): Promise<void> {
  let output = '';
  child.stdout?.on('data', (chunk: Buffer) => { output += chunk.toString(); });
  child.stderr?.on('data', (chunk: Buffer) => { output += chunk.toString(); });
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Vite exited ${child.exitCode}:\n${output}`);
    try {
      const response = await fetch(APP_URL);
      if (response.ok) return;
    } catch {
      // Vite has not accepted its first request yet.
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
  await Promise.race([once(child, 'exit'), new Promise((resolve) => setTimeout(resolve, 3_000))]);
  if (child.exitCode === null) {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {
      child.kill('SIGKILL');
    }
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
  return result.split('\n').filter((line) => Number(line.trim().split(/\s+/, 3)[1]) === processGroupId);
}

async function main(): Promise<void> {
  const vite = spawn('node_modules/.bin/vite', ['--host', HOST, '--port', String(PORT), '--strictPort'], {
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
      args: [`--${SESSION_TAG}`]
    });
    const context = await browser.newContext({ viewport: VIEWPORT });
    const page = await context.newPage();
    await page.addInitScript((sessions: SeedSession[]) => {
      const callbacks = new Map<number, { callback: (payload: unknown) => void; once: boolean }>();
      let callbackId = 1;
      const browserWindow = window as unknown as {
        __historySeedDelivered?: boolean;
        __TAURI_EVENT_PLUGIN_INTERNALS__: { unregisterListener(): void };
        __TAURI_INTERNALS__: Record<string, unknown>;
      };
      browserWindow.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener() {} };
      browserWindow.__TAURI_INTERNALS__ = {
        transformCallback(callback: (payload: unknown) => void, once = false): number {
          const id = callbackId++;
          callbacks.set(id, { callback, once });
          return id;
        },
        unregisterCallback(id: number): void { callbacks.delete(id); },
        convertFileSrc(path: string): string { return path; },
        async invoke(command: string): Promise<unknown> {
          if (command === 'plugin:event|listen') return callbackId++;
          if (command === 'plugin:event|unlisten') return true;
          if (command === 'list_agent_conversation_sessions') return [];
          if (command === 'list_agent_sessions') {
            browserWindow.__historySeedDelivered = true;
            return sessions;
          }
          if (command === 'list_terminal_sessions') return [];
          if (command === 'backend_capabilities') return [];
          return null;
        }
      };
    }, seedSessions());

    const consoleErrors: string[] = [];
    page.on('console', (message: { type(): string; text(): string }) => {
      if (message.type() === 'error') consoleErrors.push(message.text());
    });
    await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean((window as unknown as { __historySeedDelivered?: boolean }).__historySeedDelivered));
    await page.waitForTimeout(250);

    const verifiedViewport = await page.evaluate(() => `${window.innerWidth}x${window.innerHeight}`);
    const hiddenRows = await page.locator('[data-testid="session-history-row"]').count();
    const placeholderCount = await page.locator('[data-testid="session-history-placeholder"]').count();

    const historyButton = page.getByRole('button', { name: 'Session History', exact: true });
    await historyButton.click();
    await page.locator('[data-testid="session-history-row"]').first().waitFor({ state: 'visible' });
    const shownRows = await page.locator('[data-testid="session-history-row"]').count();
    const showMore = page.locator('[data-testid="session-history-show-more"]').first();
    const showMoreText = (await showMore.textContent())?.replace(/\s+/g, ' ').trim() ?? '';
    await showMore.scrollIntoViewIfNeeded();
    await page.screenshot({ path: SCREENSHOT_PATH, fullPage: false });

    await showMore.click();
    const disclosedRows = await page.locator('[data-testid="session-history-row"]').count();

    console.log(`Viewport: ${verifiedViewport}`);
    console.log(`Seeded records: 4000`);
    console.log(`Startup hidden rows: ${hiddenRows}`);
    console.log(`Startup placeholders: ${placeholderCount}`);
    console.log(`Shown rows before disclosure: ${shownRows}`);
    console.log(`Disclosure label: ${showMoreText}`);
    console.log(`Shown rows after one disclosure: ${disclosedRows}`);
    console.log(`Screenshot: ${SCREENSHOT_PATH}`);
    console.log(`Console errors: ${consoleErrors.length}`);

    if (verifiedViewport !== '1710x990') throw new Error(`Viewport verification failed: ${verifiedViewport}`);
    if (hiddenRows !== 0 || placeholderCount !== 1) throw new Error('Inactive history rendered rows');
    if (shownRows !== 25 || disclosedRows !== 50) throw new Error('History window receipt did not match 25-row increments');
    if (showMoreText !== 'Show more — 3975 older') throw new Error(`Unexpected disclosure label: ${showMoreText}`);
    if (consoleErrors.length > 0) throw new Error(`Browser console errors:\n${consoleErrors.join('\n')}`);
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
    console.log(`Browser cleanup: ${browserLeftovers.length === 0 ? `stopped ${SESSION_TAG} (Chrome helper tree exited)` : `not stopped - ${browserLeftovers.join(' | ')}`}`);
    console.log(`Browser cleanup: ${viteLeftovers.length === 0 ? `stopped historybound21-vite (${PORT} process tree exited)` : `not stopped - ${viteLeftovers.join(' | ')}`}`);
  }
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.stack : error);
  process.exitCode = 1;
});
