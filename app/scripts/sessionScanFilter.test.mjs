import assert from 'node:assert/strict';

import {
  claudeTranscriptHeadIsAgentLaunched,
  codexRolloutHeadIsSubagentThread,
  codexRolloutHeadThreadMarker,
  dropCodexSubagentSessions,
  isAgentLaunchEntrypoint,
  mergeCodexSessionMetadata,
  parseClaudeJsonl,
  parseCodexIndexJsonl,
  parseCodexRolloutJsonl
} from '../src/lib/server/localSourceFs.ts';

/**
 * Bridge parity for the Rust scanner's "is this a top-level session?" rule
 * (core/src/scanners/sessions.rs). Same fixtures, same expectations — if these
 * two ever disagree, the rail shows different sessions depending on which
 * scanner answered.
 */

const projectPath = '/Users/dev/work/mac-command-bar';

/**
 * A helper-agent transcript as Claude Code actually writes it: flat in the
 * project directory, `isSidechain: false`, `userType: "external"`, no agent
 * name anywhere — identical to a real session except for the entrypoint that
 * launched it.
 */
const helperAgentJsonl = [
  '{"type":"user","isSidechain":false,"userType":"external","entrypoint":"sdk-cli","promptSource":"sdk","sessionId":"S4","cwd":"/Users/dev/work/mac-command-bar","timestamp":"2026-07-28T11:00:00Z","message":{"role":"user","content":"You are implementing Task 1 of the v2-base plan."}}',
  '{"type":"assistant","isSidechain":false,"userType":"external","entrypoint":"sdk-cli","sessionId":"S4","timestamp":"2026-07-28T11:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Reading the brief."}]}}'
].join('\n');

/**
 * A REAL session that opens with pasted log output. The user does this often,
 * and it is why the filter reads the entrypoint rather than guessing from the
 * shape of the first message: nothing about this text distinguishes it from a
 * machine-authored dispatch prompt.
 */
const realSessionStartingWithAPastedLogJsonl = [
  '{"type":"user","isSidechain":false,"userType":"external","entrypoint":"cli","promptSource":"typed","sessionId":"S5","cwd":"/Users/dev/work/mac-command-bar","timestamp":"2026-07-28T12:00:00Z","message":{"role":"user","content":"You are seeing this in the console:\\n[vite] hmr update /src/routes/next/+page.svelte\\nERROR  Cannot read properties of undefined (reading \'api\')\\n    at mount (chunk-QK6X.js:14:9)\\nReview this change and tell me why. Your final message should name the file."}}',
  '{"type":"assistant","isSidechain":false,"entrypoint":"cli","sessionId":"S5","timestamp":"2026-07-28T12:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"That mount is running before the store exists."}]}}'
].join('\n');

/** A transcript old enough to predate the entrypoint field entirely. */
const transcriptWithoutAnEntrypointJsonl = [
  '{"type":"user","isSidechain":false,"sessionId":"S1","cwd":"/Users/dev/work/mac-command-bar","timestamp":"2026-07-28T09:00:00Z","message":{"role":"user","content":"Fix the resume rail"}}',
  '{"type":"assistant","isSidechain":false,"sessionId":"S1","timestamp":"2026-07-28T09:02:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}'
].join('\n');

/** A subagent transcript, still excluded by the older sidechain rule. */
const subagentJsonl =
  '{"parentUuid":null,"isSidechain":true,"agentId":"a0a5","type":"user","sessionId":"S2","cwd":"/Users/dev/work/mac-command-bar","timestamp":"2026-07-28T10:00:00Z","message":{"role":"user","content":"You are implementing Task 6 of the Slice 1 plan."}}';

// The entrypoint is the whole rule: `sdk-*` launched it programmatically.
assert.equal(isAgentLaunchEntrypoint('sdk-cli'), true);
assert.equal(isAgentLaunchEntrypoint('sdk-py'), true);
assert.equal(isAgentLaunchEntrypoint('cli'), false);
assert.equal(isAgentLaunchEntrypoint('claude-vscode'), false);
assert.equal(isAgentLaunchEntrypoint(''), false);

assert.deepEqual(parseClaudeJsonl(helperAgentJsonl, projectPath), []);

// A real session that opens with pasted log output and the very phrases a
// dispatch prompt uses ("You are ", "Review this change", "Your final message")
// must survive. Excluding one of the user's own sessions is strictly worse than
// listing a helper.
const pastedLog = parseClaudeJsonl(realSessionStartingWithAPastedLogJsonl, projectPath);
assert.equal(pastedLog.length, 1);
assert.equal(pastedLog[0].id, 'S5');

// A missing entrypoint is not evidence of a helper, and the cost of guessing
// wrong is losing a session the user wanted.
const noEntrypoint = parseClaudeJsonl(transcriptWithoutAnEntrypointJsonl, projectPath);
assert.equal(noEntrypoint.length, 1);
assert.equal(noEntrypoint[0].id, 'S1');

assert.deepEqual(parseClaudeJsonl(subagentJsonl, projectPath), []);

/**
 * The shape that flooded the rail: an API job's transcripts, hundreds of them,
 * written into a temporary directory. Every record carries the launch
 * entrypoint — but the opening records do not, so the probe has to read past
 * them.
 */
const apiBatchJobJsonl = [
  '{"type":"queue-operation","operation":"enqueue","timestamp":"2026-07-27T15:25:58.319Z","sessionId":"S6","content":"Extract the fields below from the following document text."}',
  '{"type":"queue-operation","operation":"dequeue","timestamp":"2026-07-27T15:25:58.319Z","sessionId":"S6"}',
  '{"type":"user","isSidechain":false,"userType":"external","entrypoint":"sdk-cli","promptSource":"sdk","sessionId":"S6","cwd":"/private/var/folders/rp/T","timestamp":"2026-07-27T15:25:59.000Z","message":{"role":"user","content":"You are a friendly assistant for extracting rental documents."}}'
].join('\n');

// Reading past the opening records is the whole point: the launch entrypoint
// appears only once the first real turn is written.
assert.equal(claudeTranscriptHeadIsAgentLaunched(apiBatchJobJsonl), true);
assert.equal(claudeTranscriptHeadIsAgentLaunched(helperAgentJsonl), true);

// A session the user typed into, and one old enough to carry no entrypoint at
// all, both stay in the scan.
assert.equal(claudeTranscriptHeadIsAgentLaunched(realSessionStartingWithAPastedLogJsonl), false);
assert.equal(claudeTranscriptHeadIsAgentLaunched(transcriptWithoutAnEntrypointJsonl), false);

// A head cut mid-line, and one holding nothing but the opening records, are
// both kept — the parse of the full file settles them.
assert.equal(claudeTranscriptHeadIsAgentLaunched('{"type":"user","entrypoint":"sdk-c'), false);
assert.equal(
  claudeTranscriptHeadIsAgentLaunched(
    '{"type":"queue-operation","operation":"enqueue","sessionId":"S6"}'
  ),
  false
);
assert.equal(claudeTranscriptHeadIsAgentLaunched(''), false);

// And the rule the probe applies is the one the parse applies.
assert.deepEqual(parseClaudeJsonl(apiBatchJobJsonl, '/private/var/folders/rp/T'), []);

/**
 * A hook-spawned security reviewer, the exact text Claude Code writes: "Review
 * this change for security vulnerabilities" followed by the file list and the
 * diff. 186 of these exist here and every one is `sdk-py`.
 */
const securityReviewAgentJsonl = [
  '{"type":"user","isSidechain":false,"userType":"external","entrypoint":"sdk-py","promptSource":"sdk","sessionId":"S7","cwd":"/Users/dev/work/mac-command-bar","timestamp":"2026-07-28T13:00:00Z","message":{"role":"user","content":"Review this change for security vulnerabilities.\\n\\nChanged files (you may Read these and any other file in the repo):\\n  - tauri-svelte-preview/src/lib/shell/terminal/terminalService.ts\\n\\nUnified diff (only + lines are new):"}}',
  '{"type":"assistant","isSidechain":false,"entrypoint":"sdk-py","sessionId":"S7","timestamp":"2026-07-28T13:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"No vulnerabilities found."}]}}'
].join('\n');

/**
 * A REAL session resumed after running out of context. Claude Code opens it
 * with a machine-written summary, so it reads exactly like a dispatch prompt —
 * and this one is 22770 lines of the user's own work.
 */
const realSessionResumedFromASummaryJsonl = [
  '{"type":"user","isSidechain":false,"userType":"external","entrypoint":"cli","sessionId":"S8","cwd":"/Users/dev/work/EdiPlatform","timestamp":"2026-07-28T14:00:00Z","message":{"role":"user","content":"This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion of the conversation.\\n\\nSummary:\\n1. Primary Request and Intent:\\n   Make the rule-extraction pipeline leaner and faster."}}',
  '{"type":"assistant","isSidechain":false,"entrypoint":"cli","sessionId":"S8","timestamp":"2026-07-28T14:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Picking up where that left off."}]}}'
].join('\n');

// Locks in why there is no "does this read like a dispatch prompt?" rule. The
// security reviewers that fill the rail are launched programmatically like
// every other helper, so the entrypoint already answers for them. Guessing from
// the text instead would cost real sessions: the second fixture opens with a
// machine-written summary and IS the user's own work, and the third opens with
// the words "Review this change" typed by the user.
assert.equal(claudeTranscriptHeadIsAgentLaunched(securityReviewAgentJsonl), true);
assert.deepEqual(parseClaudeJsonl(securityReviewAgentJsonl, '/Users/dev/work/mac-command-bar'), []);

for (const [fixture, id] of [
  [realSessionResumedFromASummaryJsonl, 'S8'],
  [realSessionStartingWithAPastedLogJsonl, 'S5']
]) {
  assert.equal(claudeTranscriptHeadIsAgentLaunched(fixture), false);
  const kept = parseClaudeJsonl(fixture, '/Users/dev/work/EdiPlatform');
  assert.equal(kept.length, 1);
  assert.equal(kept[0].id, id);
}

/**
 * A thread Codex spawned for itself. Both markers are present, as they are on
 * 618 of the 619 helper threads on this machine.
 */
const codexSubagentRolloutJsonl = [
  '{"timestamp":"2026-07-28T17:24:36.972Z","type":"session_meta","payload":{"id":"019fa9c1","parent_thread_id":"019fa964","cwd":"/Users/dev/work/rental-management","originator":"codex-tui","thread_source":"subagent","agent_role":"executor","source":{"subagent":{"parent_thread_id":"019fa964","depth":1}}}}',
  '{"timestamp":"2026-07-28T17:25:00.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Implement the year simulation runner."}]}}'
].join('\n');

/**
 * A session the user started in the terminal. `source` is a plain string, and
 * the first two turns are the ones Codex writes for itself before the user has
 * typed anything.
 */
const codexTopLevelRolloutJsonl = [
  '{"timestamp":"2026-07-28T16:42:17.000Z","type":"session_meta","payload":{"id":"019fa964","cwd":"/Users/dev/work/rental-management","originator":"codex-tui","thread_source":"user","source":"cli"}}',
  '{"timestamp":"2026-07-28T16:42:18.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for /Users/dev/work/rental-management\\n\\n<INSTRUCTIONS>\\nEvery reply MUST open with a plain-English summary.\\n</INSTRUCTIONS>"}]}}',
  '{"timestamp":"2026-07-28T16:42:19.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\\n  <cwd>/Users/dev/work/rental-management</cwd>\\n</environment_context>"}]}}',
  '{"timestamp":"2026-07-28T16:42:30.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Please plan out an entire year of scans and entries for the 2027 simulation."}]}}'
].join('\n');

/** An older Codex build that wrote no thread marker at all. */
const codexUnmarkedRolloutJsonl = [
  '{"timestamp":"2026-04-14T13:53:07.000Z","type":"session_meta","payload":{"id":"019d8d20","cwd":"/Users/dev/work/EdiPlatform","originator":"codex_cli_rs","source":"unknown"}}',
  '{"timestamp":"2026-04-14T13:54:00.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Help me trace where the 810 mapping loses the invoice date."}]}}'
].join('\n');

// Either marker on its own is enough — they disagreed on one real file.
assert.equal(
  codexRolloutHeadIsSubagentThread(
    '{"type":"session_meta","payload":{"thread_source":"subagent","source":"vscode"}}'
  ),
  true
);
assert.equal(
  codexRolloutHeadIsSubagentThread(
    '{"type":"session_meta","payload":{"source":{"subagent":{"depth":1}}}}'
  ),
  true
);

// A session the user started: `source` is a plain string, not an object.
assert.equal(
  codexRolloutHeadIsSubagentThread(
    '{"type":"session_meta","payload":{"thread_source":"user","source":"cli"}}'
  ),
  false
);
// No marker, unreadable, or not a metadata line at all: keep the file.
assert.equal(
  codexRolloutHeadIsSubagentThread('{"type":"session_meta","payload":{"source":"unknown"}}'),
  false
);
assert.equal(codexRolloutHeadIsSubagentThread('{"type":"sessi'), false);
assert.equal(codexRolloutHeadIsSubagentThread(''), false);

assert.deepEqual(parseCodexRolloutJsonl(codexSubagentRolloutJsonl), []);

const codexTopLevel = parseCodexRolloutJsonl(codexTopLevelRolloutJsonl);
assert.equal(codexTopLevel.length, 1);
assert.equal(codexTopLevel[0].id, '019fa964');

// Older rollouts predate the marker. Keep them: losing a session the user wants
// back is worse than listing a helper thread.
const codexUnmarked = parseCodexRolloutJsonl(codexUnmarkedRolloutJsonl);
assert.equal(codexUnmarked.length, 1);
assert.equal(codexUnmarked[0].id, '019d8d20');

// The repository instructions and the environment block are written by Codex,
// not the user, so neither may become the title.
assert.equal(
  codexTopLevel[0].title,
  'Please plan out an entire year of scans and entries for the...'
);

// Nothing typed inside the scanned window leaves the generic label.
assert.equal(
  parseCodexRolloutJsonl(
    '{"type":"session_meta","payload":{"id":"019fa964","cwd":"/Users/dev","source":"cli"}}'
  )[0].title,
  'Codex session'
);

// The index file names some sessions. That name must survive a merge with the
// rollout file whichever record carries the later timestamp.
assert.equal(
  mergeCodexSessionMetadata(
    parseCodexIndexJsonl(
      '{"id":"019fa964","thread_name":"Year simulation planning","updated_at":"2026-07-28T16:00:00.000000Z"}'
    ),
    parseCodexRolloutJsonl(codexTopLevelRolloutJsonl)
  )[0].title,
  'Year simulation planning'
);

// And an unnamed index row must not overwrite a real rollout title.
assert.equal(
  mergeCodexSessionMetadata(
    parseCodexIndexJsonl('{"id":"019fa964","updated_at":"2026-07-29T16:00:00.000000Z"}'),
    parseCodexRolloutJsonl(codexTopLevelRolloutJsonl)
  )[0].title,
  'Please plan out an entire year of scans and entries for the...'
);

// The index file names sub-agent threads as plausibly as it names the user's
// own — "Audit inventory gaps", "Review mobile steppers" — so the name is no
// help at all and the rollout files have to answer.
{
  // The id travels with the verdict, so an archived helper thread can be
  // recognised from its rollout file and struck off the index.
  assert.deepEqual(codexRolloutHeadThreadMarker(codexSubagentRolloutJsonl), {
    id: '019fa9c1',
    spawnedByCodex: true
  });
  assert.deepEqual(codexRolloutHeadThreadMarker(codexTopLevelRolloutJsonl), {
    id: '019fa964',
    spawnedByCodex: false
  });

  // A metadata record with no id still answers the question it can.
  assert.deepEqual(
    codexRolloutHeadThreadMarker('{"type":"session_meta","payload":{"thread_source":"subagent"}}'),
    { id: null, spawnedByCodex: true }
  );

  // Nothing readable: no verdict either way.
  assert.equal(codexRolloutHeadThreadMarker('{"type":"sessi'), null);
  assert.equal(codexRolloutHeadThreadMarker(''), null);

  const index = parseCodexIndexJsonl(
    [
      '{"id":"019fa9c1","thread_name":"Audit inventory gaps","updated_at":"2026-07-28T17:24:00.000000Z"}',
      '{"id":"019fa964","thread_name":"Year simulation planning","updated_at":"2026-07-28T16:42:00.000000Z"}',
      '{"id":"019c230d","thread_name":"Document EDI flow review","updated_at":"2026-03-13T21:48:02.611673Z"}'
    ].join('\n')
  );
  assert.equal(index.length, 3);

  // The helper thread goes. The session the user started stays, and so does the
  // one whose rollout file is gone from disk entirely — no evidence is not
  // evidence.
  assert.deepEqual(
    dropCodexSubagentSessions(index, new Set(['019fa9c1'])).map((record) => record.id),
    ['019fa964', '019c230d']
  );
}

console.log('session scan filter tests passed');
