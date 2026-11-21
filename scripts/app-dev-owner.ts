// app:dev records its own process identity per profile. A later launch can retire only that exact owner.
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTimeout } from 'node:timers/promises';

function identity(pid: number): string | undefined {
  try { return execFileSync('ps', ['-p', String(pid), '-o', 'lstart=', '-o', 'command='], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim() || undefined; }
  catch (error) {
    if ((error as { status?: number }).status === 1) return undefined;
    throw error;
  }
}

export async function claimHome(home: string, signal: AbortSignal): Promise<() => void> {
  mkdirSync(home, { recursive: true });
  const file = join(home, 'app-dev-owner.json');
  const current = { pid: process.pid, home: resolve(home), identity: identity(process.pid) };
  if (!current.identity || !current.identity.includes('app-dev.ts')) throw Error('Cannot establish app:dev process identity');
  for (let attempt = 0; attempt < 3; attempt++) {
    signal.throwIfAborted();
    try {
      writeFileSync(file, JSON.stringify(current), { flag: 'wx', mode: 0o600 });
      return () => {
        try { if (readFileSync(file, 'utf8') === JSON.stringify(current)) unlinkSync(file); }
        catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
      };
    } catch (error) { if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error; }
    const recorded = readFileSync(file, 'utf8');
    const owner = JSON.parse(recorded);
    if (!Number.isSafeInteger(owner.pid) || owner.pid <= 1 || owner.pid === process.pid ||
      owner.home !== current.home || typeof owner.identity !== 'string' || !owner.identity.includes('app-dev.ts')) {
      throw Error('Cannot verify previous app:dev owner; close that instance first');
    }
    if (identity(owner.pid) === owner.identity) {
      process.kill(owner.pid, 'SIGTERM');
      const deadline = Date.now() + 15_000;
      while (identity(owner.pid) === owner.identity) {
        if (Date.now() >= deadline) throw Error('Previous app:dev did not exit; close that instance first');
        await setTimeout(50, undefined, { signal });
      }
    }
    // Its shutdown may already have removed the record. Never remove another launch's claim.
    try { if (readFileSync(file, 'utf8') === recorded) unlinkSync(file); }
    catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
  }
  throw Error('Another app:dev is starting for this data folder');
}
