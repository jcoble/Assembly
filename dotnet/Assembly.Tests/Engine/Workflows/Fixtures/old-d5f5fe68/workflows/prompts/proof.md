Task ${{ task.key }}: ${{ task.title }}

${{ task.body }}

Read the whole task before anything else: `notion-fetch` ${{ task.key }} and read its sections verbatim; the owner's decisions and later corrections are there. If the task has reference screenshots, `Read` each one at the absolute path recorded in the task body (attached screenshots are also saved under `~/dev/work/reports/tsk-ui-base/screenshots/`) and judge the work against them.

What was built:
${{ steps.build.summary }}

Prove the change works. For a visible change, run the app headless as the workbox-native-ui skill describes, show the change, and save screenshots or a short video in ${{ run.proof_dir }}; describe each file with `evidence add <file> --task ${{ task.key }} --desc "<what it proves>"`. For a change you cannot see, save readable command receipts there instead. If the change touches the chat (history, turn folds, tool calls and their grouping, live versus stored rows, the composer), prove it in all four scenarios AGENTS.md requires: Claude Code and Codex, each on a local session and on a Workbox Test session (SSH alias `workbox-test`, never any other machine), and end your report with the four-row table (provider x local/remote: pass/fail + evidence). A scenario you cannot run is reported blocked with the exact error; never skip one silently. Stop every process you started.

Report with report_result: summary is each proof file and what it shows, and each command with its result; files lists the proof files. Never claim a result you did not see. If report_result rejects it, fix the field it names and call it again; the step does not finish without it.

Run commands with full paths or `git -C <dir>`; never start a command with `cd`, because that stops for approval.
