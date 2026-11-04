import { mkdtempSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { config, command, gh, status, atomic, main, privateDirectory, ci, recordedUnits, stopUnit } from './common.ts';
import { post } from './post.ts';

export function dispatch(input: string, locked=false) {
  // SSH_ORIGINAL_COMMAND is data, never a shell command. ASCII decimal fields only.
  if (!/^[1-9][0-9]*(?: [1-9][0-9]*)?$/.test(input)) throw Error('Expected PR digits, optionally comment digits');
  const [pr, comment] = input.split(' ').map(Number);
  if (![pr, ...(comment ? [comment] : [])].every(Number.isSafeInteger)) throw Error('Number out of range');
  const c = config();
  if (comment && !c.allowCommentReruns) throw Error('Comment reruns await owner approval');
  if(!locked) {
    privateDirectory(c.state);
    process.stdout.write(command('flock',['--exclusive',join(c.state,'dispatch.lock'),process.execPath,
      join(import.meta.dirname,'dispatch.ts'),'--locked'],{timeout:240000}));
    return;
  }
  const pull = gh(c, `repos/${c.repository}/pulls/${pr}`);
  if (pull.number !== pr || pull.state !== 'open' || pull.user?.login !== c.owner ||
      pull.head?.repo?.full_name !== c.repository || !/^[a-f0-9]{40}$/.test(pull.head?.sha) ||
      !/^[a-f0-9]{40}$/.test(pull.base?.sha)) throw Error('PR must be open, same repository, and owner-authored');
  if (comment) {
    const note = gh(c, `repos/${c.repository}/issues/comments/${comment}`);
    if (note.user?.login !== c.owner || note.issue_url !== `https://api.github.com/repos/${c.repository}/issues/${pr}` ||
        !/^@verify(?:\s|$)/.test(note.body || '')) throw Error('Comment does not authorize this PR');
  }
  if (!comment && c.skipDrafts && pull.draft) { console.log('Draft skipped'); return; }
  privateDirectory(c.state);
  // systemctl stop waits for ExecStopPost. Finish the old result before posting the new pending state.
  const unitName=`verify-pr-${pr}.service`, units=recordedUnits(c.state);
  if(units.includes(unitName)) stopUnit(unitName);
  else if(command('systemctl',['--user','show',unitName,'--property=LoadState','--value']).trim()!=='not-found')
    throw Error('Refusing an existing unit not recorded by this installation');
  for (const entry of readdirSync(c.state).filter(x => x.startsWith('run-'))) {
    const directory = join(c.state, entry);
    if (existsSync(join(directory, 'receipt.json')) && !existsSync(join(directory, 'posted.json'))) post(directory);
  }
  const directory = mkdtempSync(join(c.state, `run-${pr}-`));
  const sha = pull.head.sha;
  const output = join(c.workerHome, 'results', directory.split('/').at(-1)!);
  const ctx = { pr, sha, base: pull.base.sha, started: new Date().toISOString(), output,
    worktree: join(c.worktrees, `verify-pr-${pr}-${sha}`),
    task: /TSK-[0-9]+/.exec(`${pull.title}\n${pull.body}`)?.[0] || '',
    title: String(pull.title || ''), body: String(pull.body || ''),
    ci: ci(c,sha),
  };
  atomic(join(directory, 'context.json'), ctx);
  status(c, ctx, 'pending', `Started ${ctx.started}`);
  // systemd parses these properties; paths installed by install.ts must contain no whitespace/metacharacters.
  const node = process.execPath, here = import.meta.dirname;
  if (![node, here, directory].every(x => /^[A-Za-z0-9_./-]+$/.test(x))) throw Error('Unsupported service path');
  if(!units.includes(unitName)) atomic(join(c.state,'units.json'),[...units,unitName]);
  try {
    command('systemd-run', ['--user', `--unit=verify-pr-${pr}`, '--collect',
      `--property=RuntimeMaxSec=${c.runtimeSeconds}`, '--property=KillMode=control-group',
      '--property=TimeoutStopSec=120', '--property=UMask=0077',
      `--setenv=VERIFY_CONFIG=${process.env.VERIFY_CONFIG || join(here, 'config.json')}`,
      `--property=ExecStopPost=${node} ${join(here, 'post.ts')} ${directory}`,
      node, join(here, 'run.ts'), directory]);
  } catch {
    post(directory); throw Error('Could not start verifier unit; no-result recorded');
  }
  console.log(`Queued PR ${pr} at ${sha}`);
}
main(import.meta.url, () => dispatch(process.env.SSH_ORIGINAL_COMMAND || '',process.argv[2]==='--locked'));
