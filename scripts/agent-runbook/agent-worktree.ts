import { checkWorktreeCommand } from './guard.ts';
import { createWorktree, backupWorktree, sweep, status, restore, done, lease } from './worktree.ts';

const [command, first, second] = process.argv.slice(2);
try {
  const ownerIndex = process.argv.indexOf('--owner');
  const owner = ownerIndex < 0 ? undefined : process.argv[ownerIndex + 1];
  if (command === 'create') console.log(createWorktree(first, process.cwd()));
  else if (command === 'guard') console.log(JSON.stringify(checkWorktreeCommand(first, process.cwd())));
  else if (command === 'backup') console.log(JSON.stringify(backupWorktree(first || process.cwd(), owner)));
  else if (command === 'done') console.log(JSON.stringify(done(first || process.cwd(), owner)));
  else if (command === 'lease') { if (!second) throw Error('lease requires an owner identity.'); lease(first, second); console.log('{}'); }
  else if (command === 'sweep') console.log(JSON.stringify(sweep({ legacy: process.argv.includes('--legacy'), dryRun: process.argv.includes('--dry-run'), hourly: process.argv.includes('--hourly') })));
  else if (command === 'status') console.log(JSON.stringify(status()));
  else if (command === 'restore') console.log(restore(first, second));
  else throw Error('Use create, backup, done, sweep, status, restore or guard.');
} catch (error) { console.error('Worktree error: ' + (error as Error).message); process.exitCode = 1; }
