/**
 * The dev server's source and git bridges require a token that changes on every
 * server start (see `server/devBridgeGuard.ts`). The server hands it to its pages
 * in a cookie named for its port, and the bridge callers send it back as a header.
 */
export const BRIDGE_TOKEN_HEADER = 'x-mcb-bridge-token';

export function bridgeTokenCookieName(port: string): string {
  return `mcb-bridge-${port}`;
}

/** The header the bridges require, carrying the token from this page's cookie. */
export function bridgeTokenHeader(): Record<string, string> {
  const prefix = `${bridgeTokenCookieName(location.port)}=`;
  const cookie = document.cookie.split('; ').find((entry) => entry.startsWith(prefix));
  return { [BRIDGE_TOKEN_HEADER]: cookie?.slice(prefix.length) ?? '' };
}
