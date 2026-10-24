/**
 * The page's restart path, reachable from a surface that is not its DOM sibling.
 *
 * NOTHING CALLS `requestSessionRestart` RIGHT NOW, and that is on purpose
 * rather than an oversight. Its only caller was the rail row's Resume button,
 * which went away with the Stopped state: no session reaches the rail as
 * stopped any more, because the command-line sessions that could arrive that
 * way are switched off for now. `SessionsColumn` still registers the page's
 * handlers, so the moment a surface wants to offer restarting again it asks
 * here and it works — no wiring to rebuild.
 *
 * The session library does not go through here: it holds the page's own
 * `restartOwned` directly, being close enough in the tree to be handed it.
 *
 * If command-line sessions stay gone, this module and the registration in
 * `SessionsColumn` can both be deleted; keep them while that is still open.
 */
interface SessionRecoveryHandlers {
  restart(ownedId: string): void;
  reconnect(ownedId: string): void;
}

let activeBinding: SessionRecoveryHandlers | null = null;

/** Share the page-owned restart path with shell surfaces that are not siblings in the DOM. */
export function registerSessionRestart(
  restart: SessionRecoveryHandlers['restart'],
  reconnect: SessionRecoveryHandlers['reconnect']
): () => void {
  const binding = { restart, reconnect };
  activeBinding = binding;
  return () => {
    if (activeBinding === binding) activeBinding = null;
  };
}

export function requestSessionRestart(ownedId: string, restartable: boolean): boolean {
  if (!activeBinding) return false;
  if (restartable) activeBinding.restart(ownedId);
  else activeBinding.reconnect(ownedId);
  return true;
}
