Task ${{ task.key }}: ${{ task.title }}

The reviewed spec to implement:

${{ task.body }}

Read the whole Notion task too: `notion-fetch` ${{ task.key }} and read its sections verbatim; the owner's decisions and later corrections are there, and they win over anything older.

Implement exactly this spec, test-first: write each listed test, show it failing, then make it pass. Do only what the spec names. If the change needs about twice the spec's stated size, or a file the spec doesn't name, stop: commit nothing, and report a one-paragraph proposal instead.

On redo, fix these review findings: ${{ redo.findings }}
and this failing check output: ${{ redo.output }}

Follow the repository rules (CLAUDE.md, AGENTS.md): KISS and YAGNI (no new abstraction, flag, fallback, compatibility path or drive-by refactor), SQL work in SQL (one query, never load rows and filter or group them in code), the async lifecycle rule, no CSS :has(), and the architecture page in the same commit (or `Architecture: unchanged`). Run the targeted tests yourself before committing, one cargo job at a time. If your change raises `PROTOCOL_VERSION`, put your backend on Workbox Test before any remote proof with `node --experimental-strip-types tauri-svelte-preview/scripts/deployRemoteBackendDev.ts workbox-test` (always name `workbox-test`; never any other machine).

Commit only your changes on this branch. The commit body ends with `Task: ${{ task.key }}`, the architecture line, and `Committed-by: Claude`, and has no Co-Authored-By line. Plain English in code, comments and commits; never mention AI tools.

The run works without the owner. Answer questions about how the code works by reading it. For small decisions the spec leaves open, make the best choice, state it in one line, and keep going. If one part of the spec truly cannot be done without the owner, build the rest, create a follow-up Notion task for that part (what, the exact reason, what would unblock it), and list it under `Follow-ups` in your report.

Report with report_result: commits lists every commit this step made, tests_added each test they add, and summary each changed file with line ranges, each test shown failing then passing, and each command you ran with its result. Commit everything first; the tree must be clean. Never claim a result you did not see. If report_result rejects it, fix the field it names and call it again; the step does not finish without it.

Run commands with full paths or `git -C <dir>`; never start a command with `cd`, because that stops for approval.
