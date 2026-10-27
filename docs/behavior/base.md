# Base: behavior list

Draft by Claude, not corrected by Jesse.

base#1 A project is the folder you choose or create; if it's a git repo, that's its main git folder. A session runs in that folder or in one of its worktrees, not in a copy made per session. Switching worktree, branch or checkout keeps it the same project, and deleting a worktree never removes the project.
base#2 On launch the app connects to every saved machine by itself. If one is down, the app still opens, shows it offline and keeps trying.
base#3 Machines can be added or removed while the app runs.
base#4 Sessions on the left, chat in the middle, tabs on the right. The right drawer pushes things over instead of covering them, there's an optional bottom dock, and divider positions are kept.
base#5 The top tab row remembers each session's tabs.
base#6 Assembly's look and theme switching, with the new chat restyled to match.
base#7 Settings are saved as they change (each keystroke, the end of a drag), and a change is never lost, even when the app quits.
base#8 The session list shows sessions made while the window is open, without a restart.
base#9 A session on Workbox Test shows the same Mode and Model menus and agent name as one on the Mac.
base#10 A stale load is stopped, in the page and the backend, when a newer one replaces it.
