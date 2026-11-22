// A session's rows as the backend publishes them, for a journey to judge from: caught up from the start and then
// followed live (live-rows.ts refuses gaps), with a wait for the first row that matches.
import { readRows, type Row } from './live-rows.ts';

export type { Row } from './live-rows.ts';

/** One line per row for a report: seq, kind, run, state and the start of its text. */
export function describe(row: Row): string {
  const text = String(row.payload?.text ?? row.payload?.summary ?? row.payload?.message ?? '').replace(/\s+/g, ' ').slice(0, 80);
  return [row.seq, row.kind, row.runId ?? '-', row.payload?.state ?? '', text].filter(part => part !== '').join(' ');
}

export type RowFeed = {
  all(): Row[];
  /** The first row, seen or still to come, that `match`es; throws `waited Ns for <what>` with the last rows. */
  waitFor(what: string, match: (row: Row) => boolean, seconds: number): Promise<Row>;
  close(): Promise<void>;
};

export function openRows(api: string, token: string, session: string): RowFeed {
  const rows: Row[] = [];
  const waiting = new Set<() => void>();
  const stop = new AbortController();
  let failure: unknown;
  const wake = () => { for (const notify of [...waiting]) notify(); };
  const reading = readRows({ api, token, session, follow: true, signal: stop.signal, onRow: row => { rows.push(row); wake(); } })
    .catch(error => { if (!stop.signal.aborted) failure = error; })
    .finally(wake);
  const last = () => rows.slice(-10).map(describe).join('\n');
  return {
    all: () => rows,
    async waitFor(what, match, seconds) {
      const until = Date.now() + seconds * 1000;
      for (;;) {
        const found = rows.find(match);
        if (found) return found;
        if (failure) throw new Error(`rows stopped while waiting for ${what}: ${(failure as Error).message}`);
        const left = until - Date.now();
        if (left <= 0) throw new Error(`waited ${seconds} s for ${what}; last rows:\n${last() || '(none)'}`);
        await new Promise<void>(resolve => {
          const timer = setTimeout(done, left);
          function done() { clearTimeout(timer); waiting.delete(done); resolve(); }
          waiting.add(done);
        });
      }
    },
    async close() { stop.abort(new Error('journey finished reading rows')); await reading; }
  };
}
