// Whether a journey's rows show the provider failing (a login, a limit) rather than the app: that case is BLOCKED, not
// a failure the builder gets back.
import type { Row } from './live-rows.ts';

const PROVIDER = /authentication[ _]required|failed to authenticate|oauth|not logged in|log ?in again|sign[ -]?in|session expired|rate[ _-]?limit|usage limit|quota|too many requests|\b429\b|overloaded/i;

/** Why a provider stopped this case, or undefined when the rows show no provider failure. */
export function providerBlock(rows: Row[]): string | undefined {
  for (const row of rows) {
    const payload = row.payload ?? {};
    if (row.kind === 'turn' && payload.authFailure) return 'the provider refused the turn: authentication';
    if (row.kind === 'user' && payload.authBlocker) return 'the provider refused the message: authentication';
    if (row.kind === 'error') {
      const said = `${payload.code ?? ''} ${payload.message ?? ''}`;
      if (PROVIDER.test(said)) return String(payload.message ?? payload.code);
    }
  }
  return undefined;
}

/** A rate or usage limit, which is worth one more try; an expired login is not. */
export function isLimit(reason: string): boolean {
  return /rate[ _-]?limit|usage limit|quota|too many requests|\b429\b|overloaded/i.test(reason);
}
