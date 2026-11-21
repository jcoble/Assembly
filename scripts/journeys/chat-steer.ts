// Send a message and steer it: a message sent while the agent runs a tool reaches the same run, and the answer takes it.
// Run by scripts/journey.sh, once per agent and machine. The prompt forces one exact 20-second command, so the timing
// doesn't depend on the model; checks look at row kinds, run ids and one marker word, never at prose.
import { journey } from '../drive/journey.ts';
import type { Row } from '../drive/rows.ts';

const ended = (row: Row) => row.kind === 'turn' && row.payload?.state !== 'started';

await journey('chat-steer', async j => {
  const rows = await j.step('open a new session in the window', () => j.openSession());
  await j.step('type and send the first message', async () => {
    await j.type('composer.input', 'Run exactly this shell command in the foreground: python3 -c "import time; time.sleep(20); print(1)" then say what it printed.');
    await j.click('composer.send');
  });
  const first = await j.step('the message starts a run', () => rows.waitFor('the first user row with its run', row => row.kind === 'user' && Boolean(row.runId), 60));
  const run = first.runId!;
  await j.step('the agent starts the command', () => rows.waitFor('a tool row started in the run', row => row.kind === 'tool' && row.runId === run && row.payload?.state === 'started', 60));
  await j.step('the window shows the turn as running', () => j.expectVisible('composer.stop'));
  await j.step('type and send the steer while the command runs', async () => {
    await j.type('composer.input', `While that runs, also put the word ${j.marker} in your final answer.`);
    await j.click('composer.send');
  });
  const steer = await j.step('the steer reaches the session', () => rows.waitFor(`a user row with ${j.marker}`, row => row.kind === 'user' && String(row.payload?.text ?? '').includes(j.marker), 45));
  j.check('steer landed in the same run', steer.runId === run, `the steer is in run ${steer.runId}; the first message's run is ${run}`);
  j.check('the agent took the steer into its turn', steer.payload?.acknowledgment?.kind === 'steer_injected',
    `the steer row's acknowledgment is ${JSON.stringify(steer.payload?.acknowledgment ?? null)}, expected steer_injected`);
  const end = await j.step('the run ends', () => rows.waitFor(`the end of run ${run}`, row => ended(row) && row.runId === run, 120));
  j.check('the run finished', end.payload.state === 'finished', `run ${run} ended ${end.payload.state}`);
  // A new turn's start row comes before its steer row, so look from the first message onward.
  const others = rows.all().filter(row => row.seq >= first.seq && row.kind === 'turn' && row.payload?.state === 'started' && row.runId !== run);
  j.check('no second run', others.length === 0, `other runs started: ${others.map(row => row.runId).join(', ')}`);
  const answer = rows.all().filter(row => row.kind === 'message' && row.runId === run && row.seq > steer.seq)
    .map(row => JSON.stringify(row.payload)).join('\n');
  j.check('the final answer has the steer', answer.includes(j.marker), `no message after the steer in run ${run} says ${j.marker}`);
  await j.step('the window shows the turn as done', () => j.expectGone('composer.stop'));
});
