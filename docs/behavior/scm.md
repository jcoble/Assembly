# Source control and worktrees: behavior list

Draft by Claude, not corrected by Jesse.

The drawer's Worktrees pane, then Source control's status. Diffs, staging, commits and branches get their lines when
they're ported. Sources: the old app's Worktrees panel (mac-command-bar `tauri-svelte-preview/src/lib/shell/panels/worktrees/`,
`list_project_worktrees` in docs/backend-commands.md), its Source control panel
(`shell/git/components/sourceControl/`, `project_git_status`), base#1, base#10 and g0-shape#10. Inspect, Remove and Archive
(parity map 8b to 8d) are left for later lines, since they start sessions in a worktree folder.

scm#1 The Worktrees tab lists every worktree of the selected session's project, the main checkout first, each named
by its branch, or "detached" with its short commit id when it has none.
scm#2 Each row says in short words what would be lost if the worktree went: uncommitted changes, and how many commits
aren't on any remote. It also shows git's own marks, locked and folder gone. A clean worktree whose commits are all
pushed shows no warning.
scm#3 A row grows in place (g0-shape#10) to show its folder, its head commit (short id and subject) and when that
commit was made.
scm#4 The list is read when the tab comes into view, when the selected session changes while it's shown, and from the
pane's refresh button. Nothing is read while the tab is hidden, and a read the user moved away from is stopped in the
window and in the backend (base#10).
scm#5 A filter box narrows the rows by branch or folder name, and says how many rows it hides.
scm#6 With no session selected, the pane says to pick one. A session whose folder isn't a git repository says so. A
git failure shows git's own message and a way to try again.
scm#7 For a session on another machine, the worktrees are read on that machine. (Waits on the desktop forwarding
calls to a remote's socket, plan C2.)
scm#8 The pane changes nothing on disk.
scm#9 The Source control tab shows the selected session's project's branch, or "detached" with its short commit id,
and how many commits it's ahead of and behind its upstream, or that it has no upstream.
scm#10 Changed files are in three lists, Staged, Changes and Untracked, each sorted by path. A file staged and then
edited again is in both Staged and Changes. Each file shows a letter for its change (M, A, D, R, C for a copy, ! for a
conflict, U for untracked), and a renamed one its old path too.
scm#11 At most 1000 files are listed, and the pane says how many more there are.
scm#12 The Source control tab reads as the Worktrees tab does (scm#4, scm#6), and reading changes nothing on disk.

## A changed file's diff

The old app's Changes tab (`shell/git/components/GitDiffView.svelte`, `MultiFileDiff.svelte`, `read_source_git_diff`).
It stacked every changed file in one tab and scrolled to the one picked; here the tab shows the picked file, and the
stack, side by side, the file filter and opening unchanged lines come later.

scm#13 Picking a file in the Source control tab opens the Changes tab beside Chat, one tab for every file. It shows
that file's diff, and picking another file shows that one instead.
scm#14 The diff is the file's staged changes and its working tree changes, each under its own label when both have
some, whichever list the file was picked from. A renamed file's staged changes show the rename, and an untracked
file shows every line as added.
scm#15 Each line shows its old and new line numbers, added lines in green and removed ones in red. Each run of
changes starts with git's own `@@` line.
scm#16 A binary file says there's no line-by-line comparison, and a file with no changes says so. A diff over 1 MiB
is cut there and says it was cut. A conflicted file shows git's combined diff as it prints it.
scm#17 The diff is read when the file is picked and from the tab's Refresh button. Picking another file stops a read
still on its way. Reading changes nothing on disk, and a path outside the session's folder is refused.
