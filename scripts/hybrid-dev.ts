import { spawn, type ChildProcess } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const DEFAULT_URL = 'http://127.0.0.1:5180/';
const READY_TIMEOUT_MS = 25_000;
const POLL_INTERVAL_MS = 150;

function parseTargetUrl(): { url: string; host: string; port: string } {
  const raw = process.env.ASSEMBLY_HYBRID_URL?.trim() || DEFAULT_URL;
  const parsed = new URL(raw);
  return {
    url: parsed.toString(),
    host: parsed.hostname || '127.0.0.1',
    port: parsed.port || '5180',
  };
}

async function isReachable(url: string): Promise<boolean> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 500);
  try {
    const res = await fetch(url, { method: 'GET', signal: controller.signal });
    return res.status < 500;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

function stopProcessGroup(child: ChildProcess | null, signal: NodeJS.Signals = 'SIGTERM'): void {
  if (!child?.pid || child.exitCode !== null || child.signalCode !== null) return;
  try {
    process.kill(-child.pid, signal);
  } catch {
    try {
      child.kill(signal);
    } catch {
      // Already exited.
    }
  }
}

async function waitUntilExit(child: ChildProcess | null, timeoutMs: number): Promise<void> {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  await new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, timeoutMs);
    child.once('close', () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

async function waitForServer(url: string, vite: ChildProcess, signal: AbortSignal): Promise<void> {
  const deadline = Date.now() + READY_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (signal.aborted) throw new Error('Cancelled before Vite dev server became ready.');
    if (vite.exitCode !== null) {
      throw new Error(`Vite dev server exited early with code ${vite.exitCode}.`);
    }
    if (await isReachable(url)) return;
    await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
  }
  throw new Error(`Timed out waiting for Vite dev server at ${url}.`);
}

async function main(): Promise<void> {
  const target = parseTargetUrl();
  const stopController = new AbortController();
  let ownedVite: ChildProcess | null = null;
  let appChild: ChildProcess | null = null;
  let shuttingDown = false;

  const shutdown = async (signal?: NodeJS.Signals): Promise<void> => {
    if (shuttingDown) return;
    shuttingDown = true;
    stopController.abort();

    stopProcessGroup(appChild, signal ?? 'SIGTERM');
    stopProcessGroup(ownedVite, 'SIGTERM');

    await Promise.all([waitUntilExit(appChild, 2500), waitUntilExit(ownedVite, 2000)]);
    stopProcessGroup(appChild, 'SIGKILL');
    stopProcessGroup(ownedVite, 'SIGKILL');
  };

  process.once('SIGINT', () => {
    void shutdown('SIGINT').finally(() => process.exit(130));
  });
  process.once('SIGTERM', () => {
    void shutdown('SIGTERM').finally(() => process.exit(143));
  });

  try {
    if (await isReachable(target.url)) {
      console.log(`hybrid:dev: reusing running Vite server at ${target.url}`);
    } else {
      console.log(`hybrid:dev: starting SvelteKit dev server at ${target.url}`);
      const viteArgs =
        target.host === '127.0.0.1' && target.port === '5180'
          ? ['--dir', join(root, 'web'), 'dev']
          : ['--dir', join(root, 'web'), 'dev', '--host', target.host, '--port', target.port, '--strictPort'];
      ownedVite = spawn('pnpm', viteArgs, {
          cwd: root,
          env: process.env,
          stdio: 'inherit',
          detached: true,
        }
      );
      await waitForServer(target.url, ownedVite, stopController.signal);
    }

    console.log('hybrid:dev: launching Assembly desktop (ASSEMBLY_HYBRID_SHELL=1)');
    appChild = spawn(process.execPath, [join(root, 'scripts/app-dev.ts'), ...process.argv.slice(2)], {
      cwd: root,
      env: {
        ...process.env,
        ASSEMBLY_HYBRID_SHELL: '1',
        ASSEMBLY_HYBRID_URL: target.url,
      },
      stdio: 'inherit',
      detached: true,
    });

    const exitCode = await new Promise<number>((resolve, reject) => {
      appChild!.once('error', reject);
      appChild!.once('close', (code) => resolve(code ?? 0));
    });

    await shutdown();
    process.exit(exitCode);
  } catch (error) {
    console.error(`hybrid:dev: ${error instanceof Error ? error.message : String(error)}`);
    await shutdown();
    process.exit(1);
  }
}

void main();
