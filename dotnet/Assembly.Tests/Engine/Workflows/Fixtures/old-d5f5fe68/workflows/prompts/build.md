Task ${{ task.key }}: ${{ task.title }}

${{ task.body }}

Read the whole task before anything else: `notion-fetch` ${{ task.key }} and read its sections verbatim; the owner's decisions and later corrections are there. If the task has reference screenshots, `Read` each one at the absolute path recorded in the task body (attached screenshots are also saved under `~/dev/work/reports/tsk-ui-base/screenshots/`) and judge the work against them.

Implement this reviewed spec, working through its checklist. Build every requirement that is not `blocked`. Leave each `blocked` one out, create a follow-up Notion task for it in the same project (what is blocked, the exact reason, what would unblock it), and list it under `Follow-ups` in your report with its link; if you cannot create Notion tasks, list it there without a link. A blocked requirement never stops the build:
${{ steps.spec.summary }}

On redo, fix these review findings: ${{ redo.findings }}
and this failing check output: ${{ redo.output }}

Follow the repository rules (CLAUDE.md, AGENTS.md): test first, the smallest diff, SQL work in SQL, the async lifecycle rule, no CSS :has(). Run the targeted tests yourself before committing. Always commit finished work, even when a proof could not run; report what you could not prove instead of stopping before the commit. If your change raises `PROTOCOL_VERSION`, put your backend on Workbox Test before the remote proof with `node --experimental-strip-types tauri-svelte-preview/scripts/deployRemoteBackendDev.ts workbox-test` (always name `workbox-test`; never run it without a host or against any other machine). Commit only your changes on this branch; the commit body ends with `Task: ${{ task.key }}`, `Architecture: unchanged` (or the staged architecture page), and `Committed-by: Claude`, and has no Co-Authored-By line.

The run works without the owner. Answer questions about how the code works by reading it. For small decisions the task leaves open, make the best choice, state it in one line, and keep going. Mark something `blocked` only when you truly cannot do it without the owner.

Report with report_result: commits lists every commit this step made, tests_added each test they add, and summary the files changed and each command you ran with its result. Commit everything first; the tree must be clean. If report_result rejects it, fix the field it names and call it again; the step does not finish without it.

Run commands with full paths or `git -C <dir>`; never start a command with `cd`, because that stops for approval.
