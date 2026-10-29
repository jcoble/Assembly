# Run card recordings, frame by frame

Three screen recordings from the owner show the look the Build Board run cards must have. They were split at 4 frames a second into `~/dev/work/reports/assembly-2026-10-29/recordings/v1`, `v2` and `v3` (`f001.jpg`…), with 5x6 contact sheets `v{1,2,3}_sheet_NN.jpg` next to them (sheet 1 = f001–f030, sheet 2 = f031–f060, and so on). Frame numbers below are approximate to one or two frames. Three more recordings exist in `~/Documents` (5.40.45, 5.42.12 and 5.42.58 PM) and were not attached; they are not covered here.

All three share one card anatomy:

- Card header: title on the left, a status pill after it (`Done` green, `Working` purple, `Needs you` amber, `Complete` green, `Completed with issues` amber), a `…` menu at the far right. v2 adds an avatar line under the title: "Jonas Weber · Started 14:02".
- Step rows: a leading icon (check for done, spinner for active, hollow dot or dimmed icon for upcoming, red x for failed, red shield for approval), a short plain-English label, a dimmer monospace detail (a path, a query, a command) after it, and a right-aligned duration (`8.0s`, `0:12`, `2m 28`). Pills sit inline after the label (`Done`, `Failed`, `Skipped`, `Unsupported`, `Check`, `Changed`, `Closed`, `High impact`, `Stops for you`). A tool row starts with a small boxed tool name (`fly`, `linear`) before the label.
- A second, dimmer line under a row carries one sentence of explanation ("Stripe returned 429 on the replay endpoint. The limit resets in 60s", "No acknowledgement from the API after 30s").
- Upcoming rows are shown dimmed in place, with their future wording ("Editing the refund branch", "fly scale_app"), so the list length never jumps. A dimmed "1 more after these" line stands in for rows past the fold and expands later.
- Footer: a summary on the left ("Step 4 of 9 · 0:17", "2 files touched · 4 tools called", "5 settled, 2 succeeded · ran 4m 43s") and the actions on the right (`Pause`, `Pause`+`Stop`, `Rerun`, `Retry 1 failed`+`Keep what succeeded`).
- Hovering a row reveals two small icons after its detail: copy and open-in-new. Hovering a button only lightens it.
- Toasts appear above the card, centered, with a green check, a bold title, one line of detail, and an optional `Undo` button at the right. They stay for several seconds and fade.

## v3, "Approval Run Console" (5.38.59 PM, 177 frames)

The full life of one run: progress, a question answered inline, an approval, a retry, completion.

| Frames | What is on screen | Transition |
|---|---|---|
| f001–f011 | Title "Reproduce webhook failure", pill `Working`. Three done rows (Traced the failing event / 41 events queued `Check` / Read 4 pages) with times. Active row "Running the failing case stripe-webhook.test.ts" with a spinner icon, a percentage at the right (0% → 100%) and a thin progress bar under the row. Below, dimmed: "Needs your answer src/api/stripe-webhook.ts `Stops for you`", "Editing the refund branch", "fly Needs your approval webhook-consumer `High impact`", "fly scale_app", "1 more after these". Footer "Step 4 of 9 · 0:17", `Pause`. | Percent steps roughly 8 points per frame; the bar fills in step with it. |
| f011 → f012 | The active row flips to a done row: spinner → check, label rewritten to "Reproduced the 500", percent replaced by the elapsed time `0:12`. The pill changes to `Needs you`. The "Needs your answer" row expands in place: label stays, a sentence appears under it ("The same guard covers disputes. Fix both branches, or refunds only?"), then a full-width text field with placeholder "Type your answer" and a `Send` button inside it at the right. "1 more after these" is replaced by the real row "linear create_issue engineering backlog". Footer becomes "Step 5 of 9 · 0:29"; the Pause button is gone while the run waits. | One frame. No animation between the two states beyond the row growing. |
| f013–f022 | Mouse moves over rows (copy/open icons show on hover). Field focused, "Answer" typed one character a frame (A, An, Ans…). The `Send` button turns white (enabled) once text exists. | Typing only. |
| f022 → f023 | On Send the row collapses back to one row: check icon, label "Answered src/api/stripe-webhook.ts", and the typed text as the dim second line ("Answer"). Pill back to `Working`. The next dimmed row "Editing the refund branch" becomes the active row with spinner, 0% and a bar. Footer "Step 6 of 9 · 0:29", `Pause` returns. | One frame. |
| f023–f033 | Editing progress 0% → 89%. | Same progress pattern. |
| f033 → f034 | The row flips to done: "+12 -3, 24 tests pass `Changed` src/api/stripe-webhook.ts 0:09". Pill `Needs you`. The approval row expands in place: "fly Needs your approval webhook-consumer `High impact`", a sentence under it ("Takes the consumer pool to 2 machines in production"), then three buttons on one line: `Approve` (filled), `Revise` (outlined), `Deny` (red text). Footer loses Pause. | One frame. |
| f035–f041 | Static while the cursor travels to Approve. | |
| f041 → f042 | Approve pressed: the row collapses to "fly Approved by you webhook-consumer" (check icon, no pill). The next row "fly scale_app webhook-consumer" becomes active with spinner, 7% and a bar. Pill `Working`. Footer "Step 8 of 9 · 0:38", `Pause` back. | One frame. |
| f042–f051 | scale_app 7% → 93%. | |
| f051 → f052 | The active row turns into a failure row: red icon, "fly Timed out on attempt 1 `Failed` webhook-consumer", time `0:30`, second line "No acknowledgement from the API after 30s". A new active row is inserted under it: "fly Retrying, attempt 2 of 3 webhook-consumer" with spinner, 0% and a bar. The list grows by one row here. | One frame. |
| f052–f062 | Retry 0% → 83%. | |
| f062 → f063 | The failure row's pill changes `Failed` → `Retried` (still red icon → neutral). The retry row flips to done: "fly Recovered on attempt 2 webhook-consumer 0:06". "linear create_issue engineering backlog" becomes active at 0%. | One frame. |
| f063–f072 | create_issue 0% → 50%. | |
| f072 → f073 | Flips to "linear Opened HAL-482 engineering backlog 0:02". A final row "Run complete" with a green check is appended. Pill `Complete`. Footer and Pause disappear. | One frame. |
| f073–f177 | Static finished card. From about f081 the macOS launcher (Raycast) is opened over it; that is not part of the app. | |

## v2, "Live Run Feed" (5.42.28 PM, 52 frames)

A finished card rerun from the start. Same card, header has avatar + "Jonas Weber · Started 14:02".

| Frames | What is on screen | Transition |
|---|---|---|
| f001–f006 | Pill `Done`. Eight done rows, each with a distinct icon (sparkle, document, code, database, pencil, code, pencil, link): "Thought for 8s the 500 on charge.refunded 8.0s", "Read 412 lines src/api/stripe-webhook.ts 5.0s", "6 files, 2 of them handlers grep -rn "charge.refunded" src 2.0s", "41 rows in 320ms events where type = 'charge.refunded' 320ms", "+12 -3 in the webhook src/api/stripe-webhook.ts 9.0s", "Exit 0 in 12.4s pnpm test … 12.4s", "+8 -1 in the worker src/queue/events-worker.ts 6.0s", "linear Created HAL-482 engineering backlog 2.0s", then "Run complete 8 steps · 44.7s". Footer "2 files touched · 4 tools called", `Rerun` (with a refresh icon). | |
| f006 → f007 | Rerun clicked. Pill `Working`. The first two rows stay done. Every later row is rewritten to its in-progress wording and dimmed: "Running grep "charge.refunded"", "Querying events", "Editing stripe-webhook.ts", "Running pnpm test", "Editing events-worker.ts", "linear create_issue". The "Run complete" row is removed. The third row is active: full brightness with a spinner at the far right instead of a duration. Footer "1 file touched · 0 tools called", buttons `Pause` + `Stop`. | One frame. The list keeps its row count; only wording, brightness and the trailing spinner change. |
| f012–f013 | Row 3 flips to done: label back to "6 files, 2 of them handlers", duration `2.0s`; row 4 "Querying events" becomes active. Footer counts step up ("1 tool called"). | One frame per step. |
| f018 | Row 4 → "41 rows in 320ms 320ms"; row 5 "Editing stripe-webhook.ts" active. | |
| f024–f025 | Row 5 → "+12 -3 in the webhook 9.0s"; pnpm test active. "2 tools called". | |
| f030 | pnpm → "Exit 0 in 12.4s"; events-worker active. | |
| f035 | → "+8 -1 in the worker 6.0s"; linear active ("2 files touched · 3 tools called"). | |
| f040 | → "linear Created HAL-482 2.0s"; "Run complete 8 steps · 44.7s" appended; pill `Done`; footer back to "2 files touched · 4 tools called", `Rerun`. | |
| f041–f052 | Static. Raycast overlay for a few frames. | |

## v1, "Run Outcome Report" (6.05.33 PM, 80 frames)

A finished run with failures, shown as a report with recovery actions. The demo plays twice (f001–f020 is the tail of one pass, f021 restarts).

| Frames | What is on screen | Transition |
|---|---|---|
| f021–f030 | Card title "Reproduce webhook failure", right side "Reading the report" in dim text. Body is a skeleton: a spinner row "Reading the next item" and grey placeholder bars. | The report streams in, one item at a time. |
| f031–f044 | Items appear one per few frames, each above the "Reading the next item" spinner row: 1 "Reproduce the 500 on charge.refunded `Done` 2m 28" with mono detail "pnpm test src/api/__tests__/stripe-webhook.test.ts"; 2 "Patch the refund branch in stripe-webhook.ts `Done` `Unsupported` 1m 32" with "Nothing checked this"; 3 "Drain the 41 queued events `Failed` 31" (red x) with "Stripe returned 429 on the replay endpoint. The limit resets in 60s"; 4 "Restart the events worker `Skipped` 0" with "Waiting on the queue drain above". | Each item slides the spinner row down. |
| f044 → f045 | Last item lands: "File the regression in Linear `Failed` 12" with "A workspace admin has to grant the scope. A retry alone will fail again." and, under it, a small outlined action button inside the row: `Grant issues:create`. Spinner row and skeleton gone. Header pill `Completed with issues`. Footer: `Retry 1 failed` (filled), `Keep what succeeded` (outlined), right "5 settled, 2 succeeded · ran 4m 43s". | One frame. |
| f045–f053 | Static; hover shows copy icon on rows. | |
| f053 → f054 | `Grant issues:create` clicked. Toast above the card: green check, "Blocker cleared", "File the regression in Linear is ready to retry." The in-row button disappears; the row's second line becomes "The blocker is cleared. Retry to run this item again." The footer button re-counts: `Retry 1 failed` → `Retry 2 failed`. | One frame; toast stays ~15 frames. |
| f056 → f057 | `Keep what succeeded` clicked. Toast "Kept what succeeded" / "2 finished items kept, 3 closed." with an `Undo` button at the toast's right. Every non-Done row gains a grey `Closed` pill after its existing pill (`Failed Closed`, `Skipped Closed`). The last row's second line becomes "The Linear server holds no issues:create scope". Both footer buttons disappear; only "5 settled, 2 succeeded · ran 4m 43s" remains, now at the left. | One frame. |
| f057–f080 | Static closed report; toast fades around f072. Raycast overlay for a few frames. | |

## What the port must reproduce

1. A row changes state in place (one frame, no slide), by rewriting its icon, label, pill and right-hand value. The row count only changes for an inserted retry row, an appended "Run complete" row, or report items streaming in.
2. Waiting states expand the waiting row in place: a sentence, then a text field with `Send` inside it (question) or `Approve` / `Revise` / `Deny` (approval). Answering collapses it back to a single row that keeps the answer as its second line.
3. Progress is a percentage at the right plus a thin bar under the row, only on the active row. Done rows show elapsed time there instead.
4. Upcoming rows are listed dimmed with their future wording; a "N more after these" line stands in for the rest.
5. The status pill and the footer's actions change with the run: `Pause` while working, nothing while waiting on the user, `Rerun` when done, `Retry N failed` + `Keep what succeeded` for a report with failures, with a `Closed` state after keeping.
6. Toasts above the card for blocker cleared / kept, with `Undo` where it applies.
