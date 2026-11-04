import { promises as fs } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { command } from './dev-remote-process.ts';

// pidwait uses Linux pidfds (process exit events). No sampling interval and no running session is stopped.
// Recheck after each exit batch: a provider may have spawned another child while we waited.
export async function waitForIdle(readMembers: () => Promise<string[]>, hostPid: string, directory: string,
  wait: boolean, signal: AbortSignal) {
  for (;;) {
    signal.throwIfAborted();
    const members = await readMembers();
    if (!members.includes(hostPid)) throw Error('lane host changed while waiting for idle');
    const children = members.filter(pid => pid !== hostPid);
    if (!children.length) return;
    if (!wait) throw Error('lane host is busy; no restart performed');
    console.log('ASSEMBLY_REMOTE_SYNC ' + JSON.stringify({ reason: 'Remote is on an older backend. Lane host is busy with a running session; sync will finish when it goes idle.' }));
    const file = join(directory, '.dev-sync-wait-' + randomUUID());
    try {
      await fs.writeFile(file, children.join('\n') + '\n', { flag: 'wx', mode: 0o600 });
      // Exit 1 means all captured PIDs already ended before pidwait acquired them.
      await command('bash', ['-c', 'pidwait -F "$1" || test "$?" -eq 1', '_', file], { signal });
    } finally { await fs.rm(file, { force: true }); }
  }
}
