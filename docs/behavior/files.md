# Files and Search: behavior list

Draft by Claude, not corrected by Jesse.

The left column's Files and Search tabs in the GPUI window, and the backend reads behind them. Sources: the coordinator's
brief for lane files-search (2026-10-28), the old app's `list_source_directory` and `search_source_tree`
(docs/backend-commands.md:149-150, FilesPanel.svelte), and Jesse's type scale (`tree` 15/26). Opening a file into a
read-only view (files#11-#16) comes from the coordinator's brief of 2026-10-28; editing and language servers come later.

files#1 The Files tab shows the selected session's folder as a tree: folders first, then files, each by name. A
folder's entries load the first time it's opened, one folder at a time, never the whole tree at once.
files#2 Clicking a folder opens or closes it; clicking any row selects it. Closing a folder that's still loading stops
its load.
files#3 Entries git ignores, and `.git`, are shown dimmed, so the tree matches what's on disk while what matters
stands out.
files#4 A folder with more entries than one answer holds shows the first ones and says there are more.
files#5 Switching sessions shows the new session's folder and stops any load for the old one. A hidden Files tab
loads nothing.
files#6 A session whose folder isn't there yet (it's made on the first message), or that runs on another machine,
says so in the tab instead of showing an empty tree.
files#7 The Search tab finds files and folders in the selected session's folder whose name holds what's typed,
ignoring case, leaving out what git ignores. It searches names only, as the old app's Search tab did.
files#8 Typing waits a moment before searching, and each new search stops the one before it, on the backend too.
files#9 Results come a page at a time; "More" loads the next page, with no result shown twice.
files#10 Nothing outside the session's folder can be listed or searched: paths that climb out of it, absolute paths
and paths through a link are refused. The folder itself ("" or ".") is allowed. A link to a folder is listed as a
folder, and opening it says it goes through a link instead of listing anything.
files#11 Clicking a file in the Files tree, or a file in Search's results, opens it as a tab beside Chat, read-only,
with line numbers. Opening a file that's already open brings its tab forward instead of opening a second one.
files#12 A large file shows its first part at once and loads the rest a part at a time as it's scrolled to. A file
past the view's size limit (8 MiB) says how big it is and what the limit is, and isn't shown.
files#13 A file that isn't UTF-8 text (it holds a NUL byte, or bytes that aren't UTF-8) says it can't be shown as
text, and nothing else is read.
files#14 If the file changes on disk between two parts, the view starts again from the top, so it never shows half
of one version and half of another.
files#15 Closing a file's tab stops any part still loading, on the backend too.
files#16 A file's tab comes back when the window opens again, read afresh.
files#17 A session folder that isn't a git repo lists normally, with nothing marked ignored.
