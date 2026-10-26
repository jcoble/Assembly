---
name: work-tasks
description: Work a Notion-tracked engineering task from a reviewed plan through implementation, proof, combined code/spec review, PR, merge, and closure; use for one task or a requested run of open tasks.
---

# Work tasks

The Notion task is the live record. Use the [Task delivery record template](https://app.notion.com/p/3ea394b0689d818f8cc5e449a3f3280f) in the Command Center. Keep the task's existing context and decisions; append the marked `Task delivery record (v1)` section once if absent. Fill only facts you know. Never manufacture a review, command result, owner, or proof.

## Start one task

1. Fetch the task and read its full body, status, linked screenshots, and current branch/PR. If it is Done, report that and do not reimplement it. Create a new task only when the user's request is a genuinely separate deliverable or durable scope change.
2. Record a snapshot in the task: `YYYY-MM-DD HH:mm EDT/EST`, current status and next action, owner, harness/model, session ID, host, interface (CLI, Assembly, VS Code, Warp, etc.), cwd, repository, worktree, and branch. Write `Not recorded` for facts unavailable from the environment. Use Eastern time, for example `TZ=America/New_York date '+%Y-%m-%d %H:%M %Z'`.
3. If already in the task's recorded task-ID branch/worktree, verify its path, branch, base, and current status, then continue there. Otherwise fetch latest `main` (or the project's default branch), create a task-ID branch in a separate worktree under that project's worktree root, and record its path, branch, and base commit. Refresh the default branch before each new task; never start a new worktree from an old task's branch. Follow the repository's worktree location and cleanup rules.
4. Write a small plan in the task: outcome, acceptance, exact files or boundaries, rough diff size, meaningful verification commands and UI cases, and any real dependency. Include the expected proof: for observable UI behavior, at most 4 screenshots and at most one video of 2 minutes or less; otherwise readable command/artifact receipts. Plan more only for a massive implementation, and say why. Use verification commands relative to the repository root. Label absolute paths with their host and give a counterpart or stable shared link for readers on another host. Prefer stable GitHub file-and-line links. Do not invent an Assembly file URI; use one only after its handler is verified.
5. Have one reviewer check the plan/spec before edits. Record reviewer/model, dated PASS or REWORK, and material findings in the task. Resolve realistic findings and request another review before implementation. KISS/YAGNI are review criteria: added layers, flags, shims, and unrelated changes fail review.

## Implement and keep the record current

6. Make the smallest change that meets acceptance. Follow repository rules. Verify the behavior with meaningful tests and, when UI is involved, the running UI and its happy, edge, and order-sensitive paths. Keep visual proof in a durable location and link it from the task. A failed required check is recorded accurately, fixed, and rerun; do not substitute a different check and claim success.
7. Before each commit read `git status --short` and the exact diff being committed. On a task branch include `Task: TSK-<id> https://app.notion.com/p/<task-page-id>` in the commit body. End the body with `Committed-by: <actual committer>`. Never add `Co-Authored-By`. Include the repository's architecture acknowledgement when required.
8. Immediately after each implementation commit, append one Eastern-dated progress row to the task: commit permalink, what changed and why, verification state, and next action. Do not turn the task into a transcript; one or two sentences per commit suffice. Use a stable commit permalink, not a branch-relative link.
9. After implementation, run one combined **code and spec review** against the task's acceptance and actual diff. Use the plan reviewer/model when available; a separate review is warranted only for a large task. Record dated PASS/REWORK and material findings. Fix accepted findings, rerun affected checks and UI proof, and record the rework and reverification. Reject far-out hypothetical issues and unrequested complexity.

## Prove and deliver

10. In the task's Verification section, list each required command and result by host, direct links to proof, and what the proof actually shows. Keep required proof reachable from both hosts through Notion, GitHub, or a verified shared location; host-local scratch paths are supplemental only. For observable UI changes, keep proof small: at most 4 screenshots and at most one video of 2 minutes or less (owner, 2026-10-07: proof takes disk space and long videos go unwatched). Exceed that only for a massive implementation, and say why. If a recording cannot be attached, link the screenshots and say why. For nonvisual work, use readable logs, command output, or artifacts. The PR body links the task and its Verification section so proof is one click away.
11. Open the PR with task, change/why, verification/proof, and combined review links. Confirm required checks and the diff. Merge only with Jesse's permission. If he says "I merge" for a PR, leave it to him. After a merge, verify the change is on the default branch. Record PR and merge commit links in the task. If a post-merge phase remains, keep the task Doing even if an automatic sync briefly marks it Done.
12. Remove and prune the task's clean worktree as soon as its work is merged or otherwise handed off under repository rules. Confirm it is absent from `git worktree list`. Verify the Notion task ends Done only when all acceptance and required follow-up work are complete. Record any unfinished next action instead of closing early.

## Work a queue

When asked to keep going, refresh the live open-task list after each completion, then repeat with a fresh default-branch worktree. If a task is genuinely blocked, record the exact blocker and move to another authorized task; do not mark it Done or silently skip it. Do not change old task bodies just to narrate progress. The initial migration to this format is append-only: refetch every open page before writing, add only a missing marked section, preserve existing content and properties, skip Done and already-converted pages, and keep a page-ID/result ledger. Never infer old proof or reviews.
