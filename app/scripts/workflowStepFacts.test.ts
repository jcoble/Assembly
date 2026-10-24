// What the Workflows view shows under a step comes from the step's structured
// output, the same facts get_workflow_run gives agents. Logic only.
import assert from 'node:assert/strict';
import { workflowErrorText, workflowStepFacts } from '../src/lib/shell/workflows/workflowTypes.ts';

{ // step_facts_come_from_structured_output: a check's commands, a review's verdict, a PR step's link
  const check = workflowStepFacts({ structuredOutput: { commands: [
    { command: 'pytest', exitCode: 1, output: 'a\nb\nc\nd\ne\nf\nFAILED test_add\n' },
    { command: 'ruff check', exitCode: 0, output: '' }
  ] } });
  assert.deepEqual(check.commands, [
    { command: 'pytest', exitCode: 1, lastLines: 'c\nd\ne\nf\nFAILED test_add' },
    { command: 'ruff check', exitCode: 0, lastLines: '' }
  ]);
  assert.equal(check.verdict, null);

  const review = workflowStepFacts({ structuredOutput: { verdict: 'fail', findings: ['add() ignores b'], summary: 'x' } });
  assert.deepEqual([review.verdict, review.findings, review.commands], ['fail', ['add() ignores b'], []]);

  assert.equal(workflowStepFacts({ structuredOutput: { url: 'https://github.com/o/r/pull/7' } }).prUrl, 'https://github.com/o/r/pull/7');
  assert.deepEqual(workflowStepFacts({ structuredOutput: null }), { commands: [], verdict: null, findings: [], prUrl: null });
}

{ // workflow_errors_read_as_the_engines_sentence: the engine's refusal is shown as its sentence, not JSON
  const refused = '{"kind":"invalid-transition","detail":"Only a failed or waiting node can be retried"}';
  assert.equal(workflowErrorText(refused), 'Only a failed or waiting node can be retried');
  assert.equal(workflowErrorText(new Error(refused)), 'Only a failed or waiting node can be retried');
  const budget = '{"kind":"budget-exhausted","detail":{"code":"x","message":"Step ran out of attempts","budget":"attempts"}}';
  assert.equal(workflowErrorText(budget), 'Step ran out of attempts');
  const plain = 'The remote backend does not run workflows; update its backend first';
  assert.equal(workflowErrorText(plain), plain);
  assert.equal(workflowErrorText('{oops'), '{oops');
}

console.log('workflowStepFacts tests passed');
