# Sessions list: Working, Done and Settled, by project

Draft by Claude, not corrected by Jesse. From the old app (mac-command-bar 2089290, `myWorkViewOptions.ts`,
`sessionRailStore.svelte.ts`) and parity map section 1.

- sessions-list#1: Every top-level session is in one of three groups: Working, Done or Settled. A new session is in Working.
- sessions-list#2: The user moves a session with its row's menu: Mark done, Settle, Back to working (and Unsettle, which puts it back in Done).
- sessions-list#3: Only that menu moves a session. Nothing is inferred from its processes, a finished turn or a new prompt.
- sessions-list#4: Done keeps when it was first marked done; settling or unsettling doesn't change that time. Back to working clears both times.
- sessions-list#5: The backend keeps the mark and stamps the time from its own clock, so every window shows the change at once. A remote session's mark is kept on this desktop.
- sessions-list#6: Inside each group, sessions are grouped by project, A to Z, newest first inside a project. Until sessions carry a project folder, a session's project is its repo or folder on its machine, so the same folder on two machines is two projects.
- sessions-list#7: Settled starts folded; its header opens and folds it.
- sessions-list#8: Each project's header has a New session button that opens the new-session form with that project's folder and machine filled in.
- sessions-list#9: The list is the sessions loaded so far (the newest 200, then older pages as you scroll near its end), grouped; an older session shows in its group once its page loads. While the list ends in a folded Settled, older pages wait until Settled is opened.
