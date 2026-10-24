<script lang="ts">
  import WorkingSpinner from '$lib/shell/components/feedback/WorkingSpinner.svelte';
  /** A single, lazy PTY for the workspace shown in the right panel. */
  import { onDestroy, onMount } from 'svelte';

  import {
    closeTerminalSessionFromTauri,
    listTerminalSessionsFromTauri,
    readTerminalSessionScrollbackFromTauri,
    resizeTerminalSessionFromTauri,
    startTerminalSessionFromTauri,
    writeTerminalSessionFromTauri,
    type TerminalOutputEnvelope,
    type TerminalScrollbackSnapshot,
    type TerminalSessionInfo
  } from '$lib/tauriSource';
  import type { TerminalView } from '$lib/liveConversationTerminals';
  import {
    createTerminalInput,
    subscribeToTerminalOutput,
    type TerminalOutputSubscription
  } from '$lib/shell/terminal/terminalService';
  import { loadGhosttyModules, makeTerminalView } from '$lib/shell/terminal/ghosttyFactory';

  interface Props {
    root: string;
    ownedId: string | null;
  }
  let { root, ownedId }: Props = $props();

  const REPLAY_MAX_BYTES = 4 * 1024 * 1024;

  let host: HTMLElement;
  let status = $state<'starting' | 'ready' | 'ended' | 'error'>('starting');
  let error = $state('');
  let sessionId: string | null = null;
  let view: TerminalView | null = null;
  let output: TerminalOutputSubscription | null = null;
  let sizes: ResizeObserver | null = null;
  const stop = new AbortController();
  let lastSize = '';
  let resizeReady = false;
  let replaying = true;
  let replayWatermark: ({ sessionId: string } & Pick<
    TerminalScrollbackSnapshot,
    'generation' | 'sequence'
  >) | null = null;
  let queuedOutput: TerminalOutputEnvelope[] = [];
  const sendInput = createTerminalInput(writeTerminalSessionFromTauri);

  async function writeInput(data: string, signal: AbortSignal): Promise<void> {
    const id = sessionId;
    if (!id || signal.aborted) return;
    try {
      await sendInput(id, data);
    } catch (cause) {
      if (signal.aborted) return;
      output?.unsubscribe();
      output = null;
      status = 'error';
      error = cause instanceof Error ? cause.message : String(cause);
    }
  }

  async function resize(cols: number, rows: number, signal: AbortSignal): Promise<void> {
    const id = sessionId;
    const size = `${cols}x${rows}`;
    if (!id || signal.aborted || lastSize === size) return;
    lastSize = size;
    try {
      await resizeTerminalSessionFromTauri(id, cols, rows);
    } catch (cause) {
      if (signal.aborted) return;
      output?.unsubscribe();
      output = null;
      status = 'error';
      error = cause instanceof Error ? cause.message : String(cause);
    }
  }

  function deliverOutput(envelope: TerminalOutputEnvelope, signal: AbortSignal): void {
    const payload = envelope.chunk;
    if (payload.sessionId !== sessionId || signal.aborted) return;
    if (
      replayWatermark &&
      payload.sessionId === replayWatermark.sessionId &&
      envelope.generation === replayWatermark.generation &&
      envelope.sequence <= replayWatermark.sequence
    ) {
      return;
    }
    if (payload.data) view?.write(payload.data);
    if (payload.terminated) status = 'ended';
  }

  function receiveOutput(envelope: TerminalOutputEnvelope, signal: AbortSignal): void {
    if (signal.aborted) return;
    if (replaying) {
      if (sessionId && envelope.chunk.sessionId !== sessionId) return;
      queuedOutput.push(envelope);
      return;
    }
    deliverOutput(envelope, signal);
  }

  function finishReplay(
    replayedSessionId: string,
    snapshot: TerminalScrollbackSnapshot | null,
    signal: AbortSignal
  ): void {
    const queued = queuedOutput;
    queuedOutput = [];
    replayWatermark = snapshot
      ? {
          sessionId: replayedSessionId,
          generation: snapshot.generation,
          sequence: snapshot.sequence
        }
      : null;
    replaying = false;
    for (const envelope of queued) {
      if (envelope.chunk.sessionId !== replayedSessionId) continue;
      deliverOutput(envelope, signal);
    }
  }

  async function replayScrollback(id: string, signal: AbortSignal): Promise<void> {
    const snapshot = await readTerminalSessionScrollbackFromTauri(id, REPLAY_MAX_BYTES);
    if (signal.aborted) return;
    if (snapshot?.data) view?.write(snapshot.data);
    finishReplay(id, snapshot, signal);
  }

  function ownedUserTerminal(sessions: TerminalSessionInfo[]): TerminalSessionInfo | null {
    const owned = sessions.filter(
      (session) => session.kind === 'user-pty' && session.ownedId === ownedId
    );
    return owned.find((session) => !session.exited) ?? owned.find((session) => session.exited) ?? null;
  }

  async function start(signal: AbortSignal): Promise<void> {
    if (!ownedId) {
      status = 'error';
      error = 'Pick an Assembly session to open its terminal.';
      return;
    }
    if (!root.trim()) {
      status = 'error';
      error = 'Pick a local workspace to open its terminal.';
      return;
    }

    try {
      const modules = await loadGhosttyModules();
      if (signal.aborted) return;

      let measured = { cols: 80, rows: 24 };
      view = makeTerminalView(modules, host, {
        onData(data): void {
          void writeInput(data, signal);
        },
        onResize(cols, rows): void {
          measured = { cols, rows };
          if (resizeReady) void resize(cols, rows, signal);
        }
      });
      view.setVisible(true);
      view.fit();

      output = subscribeToTerminalOutput((envelope) => receiveOutput(envelope, signal), signal);
      await output.ready;
      if (signal.aborted) return;

      const existingSessions = await listTerminalSessionsFromTauri();
      if (signal.aborted) return;
      const existing = ownedUserTerminal(existingSessions ?? []);
      if (existing?.exited) {
        sessionId = existing.sessionId;
        await replayScrollback(existing.sessionId, signal);
        if (signal.aborted) return;
        await closeTerminalSessionFromTauri(existing.sessionId);
        if (signal.aborted) return;
        sessionId = null;
        replaying = true;
      }

      const session = await startTerminalSessionFromTauri({
        cwd: root,
        ownedId,
        cols: measured.cols,
        rows: measured.rows
      }, signal);
      if (signal.aborted) return;
      if (!session) {
        status = 'error';
        error = 'The workspace terminal is available in the desktop app.';
        return;
      }

      sessionId = session.sessionId;
      lastSize = `${session.cols}x${session.rows}`;
      status = 'ready';
      view.fit();
      await resize(measured.cols, measured.rows, signal);
      if (signal.aborted) return;
      resizeReady = true;
      await replayScrollback(session.sessionId, signal);
      if (signal.aborted) return;
      view.focus();

      sizes = new ResizeObserver(() => view?.fit());
      sizes.observe(host);
    } catch (cause) {
      if (signal.aborted) return;
      close();
      status = 'error';
      error = cause instanceof Error ? cause.message : String(cause);
    }
  }

  function close(): void {
    stop.abort();
    sizes?.disconnect();
    sizes = null;
    output?.unsubscribe();
    output = null;
    view?.dispose();
    view = null;
    sessionId = null;
    replayWatermark = null;
    queuedOutput = [];
  }

  onMount(() => {
    void start(stop.signal);
  });

  onDestroy(() => {
    close();
  });
</script>

<div class="terminal-surface" data-testid="workspace-terminal">
  <div class="terminal-host" bind:this={host}></div>
  {#if status !== 'ready'}
    <div class="terminal-status" class:error={status === 'error'}>
      {#if status === 'starting'}<WorkingSpinner size={12} /> Opening terminal…{:else if status === 'ended'}Terminal exited{:else}{error}{/if}
    </div>
  {/if}
</div>

<style>
  .terminal-surface {
    position: relative;
    height: 100%;
    min-height: 0;
    overflow: hidden;
    background: var(--color-surface);
  }

  .terminal-host {
    position: absolute;
    inset: 0;
    padding: 6px 8px;
  }

  .terminal-status {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    background: var(--color-surface);
    color: var(--color-text-2);
    font-size: 12px;
  }

  .terminal-status.error {
    color: var(--color-bad);
  }
</style>
