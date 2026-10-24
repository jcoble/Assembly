<script lang="ts">
  import Circle from '@lucide/svelte/icons/circle';
  import LoaderCircle from '@lucide/svelte/icons/loader-circle';
  import RotateCcw from '@lucide/svelte/icons/rotate-ccw';

  import { IconButton } from '$lib/components/ui/icon-button/index.js';
  import type { AgentRuntimeState, OwnedSessionState } from '$lib/shell/ownedSessions.ts';
  import type { ConversationConnectionState } from '$lib/shell/conversation/conversationTypes.ts';
  import {
    deriveSessionPresence,
    EMPTY_SESSION_PRESENCE_HISTORY,
    formatPresenceElapsed,
    sessionPresenceHistory,
    synchronizeSessionPresenceWork
  } from '$lib/shell/conversation/sessionPresence.ts';

  interface Props {
    ownedId: string;
    terminalState: OwnedSessionState;
    connectionState?: ConversationConnectionState | null;
    activeTurnId?: string | null;
    sending?: boolean;
    pendingApprovalCount?: number;
    runtimeState?: AgentRuntimeState | null;
    onRestart?(): void;
  }

  let {
    ownedId,
    terminalState,
    connectionState = null,
    activeTurnId = null,
    sending = false,
    pendingApprovalCount = 0,
    runtimeState = null,
    onRestart
  }: Props = $props();

  const history = $derived($sessionPresenceHistory[ownedId] ?? EMPTY_SESSION_PRESENCE_HISTORY);

  $effect(() => {
    const stopped = terminalState === 'exited';
    synchronizeSessionPresenceWork(
      ownedId,
      stopped ? null : activeTurnId,
      !stopped && (sending || runtimeState === 'working')
    );
  });

  const baseline = $derived(deriveSessionPresence(
    { terminalState, connectionState, activeTurnId, sending, pendingApprovalCount, runtimeState },
    history,
    Date.now()
  ));
  const presence = $derived(baseline);
  const elapsed = $derived(formatPresenceElapsed(presence.elapsedMs ?? 0));
</script>

<span
  class="presence"
  data-testid="session-presence"
  data-presence={presence.state}
  aria-live={presence.state === 'needs-attention' ? 'polite' : 'off'}
>
  {#if presence.state === 'working'}
    <span class="presence-spinner" aria-hidden="true"><LoaderCircle class="size-3.5" /></span>
    <span class="presence-time" data-testid="session-presence-elapsed">{elapsed}</span>
  {:else if presence.state === 'needs-attention'}
    <span class="attention-dot" aria-hidden="true"></span>
    <span>Needs attention</span>
  {:else if presence.state === 'disconnected'}
    <span data-testid="session-presence-restart">
      <IconButton
        label="Restart session"
        size="xs"
        side="bottom"
        class="text-[var(--color-attention)] hover:text-[var(--color-attention)]"
        onclick={() => onRestart?.()}
        disabled={!onRestart}
      >
        <RotateCcw class="size-3.5" aria-hidden="true" />
      </IconButton>
    </span>
  {:else}
    <span class="idle-mark" aria-hidden="true"><Circle class="size-2.5" /></span>
    <span>Idle</span>
  {/if}
</span>

<style>
  .presence {
    display: inline-flex;
    min-width: 0;
    align-items: center;
    gap: 5px;
    color: var(--color-text-2);
    font-size: 12px;
    white-space: nowrap;
  }

  .presence-spinner {
    display: inline-flex;
    color: var(--color-live);
  }

  .presence-time {
    color: var(--color-live);
    font-variant-numeric: tabular-nums;
  }

  .attention-dot {
    width: 7px;
    height: 7px;
    border-radius: 999px;
    background: var(--color-attention);
    box-shadow: 0 0 0 3px var(--color-attention-bg);
  }

  .idle-mark {
    display: inline-flex;
    color: var(--color-good);
    fill: color-mix(in srgb, var(--color-good) 32%, transparent);
  }
</style>
