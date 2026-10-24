import { randomBytes, timingSafeEqual } from 'node:crypto';
import type { IncomingHttpHeaders, IncomingMessage, ServerResponse } from 'node:http';

import { BRIDGE_TOKEN_HEADER, bridgeTokenCookieName } from '../devBridgeToken.ts';

const LOCAL_HOST = /^(127\.0\.0\.1|localhost|\[::1\])(:(\d+))?$/;

/**
 * The source bridge reads and writes any file the owner can reach, and the git
 * bridge reads any repository, so a bridge request must come from a page this
 * server served:
 * - its Host header is a local address, which shuts out websites whose DNS name
 *   was pointed at 127.0.0.1;
 * - its Origin (or its Referer when a browser leaves Origin out) is this server,
 *   which shuts out pages on other origins;
 * - it carries this server start's random token, which shuts out requests forged
 *   without loading one of this server's pages first.
 */
function isOwnPageRequest(headers: IncomingHttpHeaders, token: Buffer): boolean {
  const host = headers.host?.toLowerCase() ?? '';
  const page = headers.origin ?? headers.referer;
  const sent = Buffer.from(String(headers[BRIDGE_TOKEN_HEADER] ?? ''));
  if (!LOCAL_HOST.test(host) || !page || sent.length !== token.length || !timingSafeEqual(sent, token)) return false;
  try {
    return new URL(page).origin === `http://${host}`;
  } catch {
    return false;
  }
}

/**
 * Mounted ahead of both bridges. A bridge request that is not from this server's
 * own page is refused; every other response hands the page the token cookie.
 */
export function createDevBridgeGuard(token = randomBytes(32).toString('hex')) {
  const expected = Buffer.from(token);
  return (request: IncomingMessage, response: ServerResponse, next: () => void) => {
    if (!(request.url ?? '').startsWith('/__mcb/')) {
      // Only a local address gets the token: a page reached through a rebound DNS name must not.
      const local = LOCAL_HOST.exec(request.headers.host?.toLowerCase() ?? '');
      if (local) response.setHeader('set-cookie', `${bridgeTokenCookieName(local[3] ?? '')}=${token}; Path=/; SameSite=Strict`);
      next();
      return;
    }
    if (!isOwnPageRequest(request.headers, expected)) {
      response.statusCode = 403;
      response.setHeader('content-type', 'application/json; charset=utf-8');
      response.end(JSON.stringify({ error: "The dev server's bridges only answer its own pages" }));
      return;
    }
    next();
  };
}
