import { invoke } from '@tauri-apps/api/core';

export type CliProvider = 'codex' | 'claude';
export type CliStatus = { provider: CliProvider; version: string | null; error: string | null };
export type CliUpdateState = {
  checking: boolean; updating: CliProvider | null; statuses: CliStatus[]; message: string;
};

export async function checkProviderClis(state: CliUpdateState, owner: AbortSignal, profileId?: string): Promise<void> {
  if (owner.aborted || state.checking || state.updating) return;
  state.checking = true;
  state.message = '';
  try {
    const statuses = await invoke<CliStatus[]>('check_provider_clis', { profileId: profileId ?? null });
    if (!owner.aborted) state.statuses = statuses;
  } catch (error) {
    if (!owner.aborted) state.message = String(error);
  } finally { state.checking = false; }
}

export async function updateProviderCli(provider: CliProvider, state: CliUpdateState, owner: AbortSignal, profileId?: string): Promise<void> {
  if (owner.aborted || state.checking || state.updating) return;
  state.updating = provider;
  state.message = '';
  try {
    const status = await invoke<CliStatus>('update_provider_cli', { provider, profileId: profileId ?? null });
    if (owner.aborted) return;
    state.statuses = state.statuses.map(entry => entry.provider === provider ? status : entry);
    state.message = `${provider === 'codex' ? 'Codex' : 'Claude'} updater finished; installed ${status.version}. New turns use the installed CLI.`;
  } catch (error) {
    if (!owner.aborted) state.message = `${provider} CLI update failed: ${String(error)}`;
  } finally { state.updating = null; }
}
