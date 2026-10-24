import { relaunch } from '@tauri-apps/plugin-process';
import { check } from '@tauri-apps/plugin-updater';

export type AppUpdatePhase = 'idle' | 'checking' | 'available' | 'current' | 'installing' | 'error';

export const appUpdateState = $state({
  phase: 'idle' as AppUpdatePhase,
  version: '',
  message: ''
});

let checkGeneration = 0;

function stopped(stopSignal: AbortSignal, generation: number): boolean {
  return stopSignal.aborted || generation !== checkGeneration;
}
/**
 * Ask the signed updater endpoint once. The caller owns the stop signal; a
 * released settings host cannot publish the result of a late network reply.
 */
export async function checkForAppUpdate(
  stopSignal: AbortSignal,
  automatic = false
): Promise<void> {
  if (stopSignal.aborted || appUpdateState.phase === 'installing') return;
  const generation = ++checkGeneration;
  if (import.meta.env.DEV) {
    appUpdateState.phase = 'current';
    appUpdateState.version = '';
    appUpdateState.message = 'Development build: this checkout supplies the desktop app. Pull changes; frontend edits hot reload. Restart pnpm app:dev after Rust changes. Check backend compatibility in Connections.';
    return;
  }
  appUpdateState.phase = 'checking';
  appUpdateState.message = automatic ? '' : 'Checking for updates…';

  let update: Awaited<ReturnType<typeof check>> = null;
  try {
    update = await check({ timeout: 15_000 });
    if (stopped(stopSignal, generation)) return;
    if (update) {
      appUpdateState.phase = 'available';
      appUpdateState.version = update.version;
      appUpdateState.message = `Assembly ${update.version} is ready to install.`;
    } else {
      appUpdateState.phase = 'current';
      appUpdateState.version = '';
      appUpdateState.message = 'Desktop matches the latest published release. Backend compatibility is checked separately in Connections.';
    }
  } catch (error) {
    if (stopped(stopSignal, generation)) return;
    appUpdateState.phase = automatic ? 'idle' : 'error';
    appUpdateState.message = automatic ? '' : String(error);
  } finally {
    if (update) await update.close();
  }
}

/** Download the signed artifact, let Tauri verify and install it, then relaunch. */
export async function installAppUpdate(stopSignal: AbortSignal): Promise<void> {
  if (stopSignal.aborted || appUpdateState.phase !== 'available') return;
  const generation = ++checkGeneration;
  appUpdateState.phase = 'installing';
  appUpdateState.message = 'Downloading and verifying the update…';

  let update: Awaited<ReturnType<typeof check>> = null;
  try {
    update = await check({ timeout: 15_000 });
    if (stopped(stopSignal, generation)) return;
    if (!update) {
      appUpdateState.phase = 'current';
      appUpdateState.version = '';
      appUpdateState.message = 'Desktop matches the latest published release. Backend compatibility is checked separately in Connections.';
      return;
    }
    await update.downloadAndInstall(undefined, { timeout: 120_000 });
    if (stopped(stopSignal, generation)) return;
    appUpdateState.message = 'Restarting Assembly…';
    await relaunch();
  } catch (error) {
    if (stopped(stopSignal, generation)) return;
    appUpdateState.phase = 'error';
    appUpdateState.message = String(error);
  } finally {
    if (update) await update.close();
  }
}
