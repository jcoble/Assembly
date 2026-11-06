Task ${{ task.key }}: ${{ task.title }}

The pull request for this branch is open. The repository's review bot reviews every push. Your job is to get its review clean without the owner.

1. Run `node --experimental-strip-types tauri-svelte-preview/scripts/prBotReview.ts` from the worktree root. It waits for the bot's review of the current head (up to 30 minutes) and prints each open finding with the commands to reply to and resolve it. Exit 0 means the head is reviewed and nothing is open; you are done. Exit 2 means nothing is open but the bot never reviewed the head: say so in your report and finish; the merge step checks again before merging.
2. For each finding, read the code it points at and decide:
   - Real: fix it test-first (a failing test, then the fix), run the targeted tests, and commit (`Task: ${{ task.key }}`, the architecture line, `Committed-by: Claude`, no Co-Authored-By line). Reply with what you changed and the commit hash, then resolve the thread.
   - Not real: reply with the reason, citing `path:line`, then resolve the thread.
   - Minor (style, naming, wording, comments, an unlikely edge case, a nicer way to write it): reply that it is accepted as minor, with no code change, then resolve the thread.
   - Real but outside this task: reply that it is tracked separately, list it under `Follow-ups` in your report, then resolve the thread.
3. `git push`, then run the script again. Repeat until it exits 0, at most three rounds. If findings remain after three rounds, stop and list them in your report; the final review decides.

Follow the repository rules (CLAUDE.md, AGENTS.md): smallest diff, no new abstractions, plain English. The run works without the owner: never ask the owner anything.

Report with report_result: commits lists every commit this step made (none if nothing needed fixing), tests_added each test they add, and summary each finding, what you decided and why, and the script's last output. If report_result rejects it, fix the field it names and call it again; the step does not finish without it.

Run commands with full paths or `git -C <dir>`; never start a command with `cd`, because that stops for approval.
