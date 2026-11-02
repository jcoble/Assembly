Task ${{ task.key }}: ${{ task.title }}

${{ task.body }}

Read the whole task before anything else: `notion-fetch` ${{ task.key }} and read its sections verbatim; the owner's decisions and later corrections are there. If the task has reference screenshots, `Read` each one at the absolute path recorded in the task body (attached screenshots are also saved under `~/dev/work/reports/tsk-ui-base/screenshots/`) and judge the work against them.

Read the repository rules first (CLAUDE.md, AGENTS.md, and tauri-svelte-preview/CLAUDE.md). Then find the files this task touches. Do not edit anything.

Report with report_result: summary is your whole report (each relevant file with `path:line` and what it does for this task, the current behaviour, and any rule that constrains the change, every claim marked verified or assumed); existing and touches list `path:line` entries that exist in the checkout, or none_found_because says why nothing existing applies. If report_result rejects it, fix the field it names and call it again; the step does not finish without it.

Run commands with full paths or `git -C <dir>`; never start a command with `cd`, because that stops for approval.
