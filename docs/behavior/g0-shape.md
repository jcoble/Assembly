# G0 shape: behavior list

Draft by Claude, not corrected by Jesse.

The GPUI window's shape: the sessions column, the center, the tools drawer and the bottom dock on the GPUI kit's
DockArea, the top row with the Home button, and the utility strip. Sources: notes/gpui-g0/g0-lanes.md (lane g0-shape)
and notes/gpui-g0/look-and-behavior-carryovers.md section 2, with the old app's commits. Settings and the palette are
g0-host's; what each pane shows belongs to the lane that owns that pane.

g0-shape#1 The window opens in the old app's layout. The left column is one tabbed column, Sessions, Files and Search,
and Chat is in the center (Jesse 2026-10-27, look item 20). The drawer is on the right with Source control, Agents,
Tasks, Worktrees, Run, Context and History, and it's closed at launch. The bottom dock holds Problems and Terminal, and it's closed too.
A closed bottom dock draws nothing along the foot; the Terminal button in the title row opens and closes it.
g0-shape#2 Any pane can be dragged anywhere, and any divider can be resized.
g0-shape#3 The layout is kept across relaunches, including the sessions column's width and whether it's folded
(491a4447, 5bfc3360). A layout saved by an older build, or one that can't be read, opens the default layout instead.
So does a launch with no backend, or one whose backend never gets ready: the window is never left empty.
g0-shape#4 Launching the app doesn't save the layout. Changes made together save once, and a save is never queued
behind older ones (378bc67d).
g0-shape#5 Opening the drawer pushes the panes over instead of covering them. Closing it gives the center its width
back (289c449b).
g0-shape#6 The Home button, with the old app's house icon, sits above the sessions column and keeps its width,
so the dividers line up with the top row. It follows the column while the column's edge is dragged (f4898b79,
668f7350).
g0-shape#7 The drawer's tabs, Sessions, Chat and Problems have no close button (951f2b43).
g0-shape#8 A pane shows nothing until the lane that owns it fills it, and nothing hidden is mounted (6353dc4d).
g0-shape#9 The utility strip at the foot of the sessions column has Resources and Usage (there's no bottom bar), so
folding the sessions column hides them too. Each opens a popover anchored to it, on screen and above the strip
(fa39c659, 36abf71a, a56526ec). Nothing in the strip polls.
g0-shape#10 A row or card reveals more by growing in place, never as an accordion (Jesse 2026-10-27, look items 13
and 16). A click on its title grows it to fit its body, the title stays where it was, and a second click shrinks it
back; the body stays until the row has shrunk, then goes. A row that's open when it first shows is drawn open. A body
whose height changes while the row is open grows or shrinks to it the same way. Every row that grows in place uses
this one row (Jesse 2026-10-27, "many list items from diff places get the same behavior always"): its owner hears each
click and each frame's height, can open or shut it itself, and can draw the band and the body from its own state.
g0-shape#11 The sessions column can be dragged from 220 to 560 px wide and the drawer from 240 to 640 px (the approved
look); a dragged edge stops at the limit, and the drag still saves once, when it ends.
g0-shape#12 The drawer's tabs sit in the title row, at its right: one 26 px icon button per drawer pane, the old app's
icons, the shown one lit while the drawer is open and the name on hover. A button opens the shut drawer on its pane,
shows its pane in the open drawer, or closes the drawer when its pane is the one shown. They have no drag handle, so a
drawer pane no longer drags from its tab. The sessions column's tabs are the old top tab row's text chips (Jesse
2026-10-27, "make these more like the ones in the old app, same with left side"): a click shows that pane, a chip still
drags its pane anywhere (g0-shape#2), and a row too narrow for its tabs scrolls. Chat alone in the center draws no tab
row: its 48 px title row reads "project › Session title", with a button before it that shows and hides the sessions
column, there even with no session open. Chat beside other center panes, and an open bottom dock, keep the kit's tabs.
g0-shape#13 The Context tab's Files touched card lists the files the selected session named, newest first, ties by
path, at most the newest 50, each once with its file name, its folder, a count chip when more than one call or prompt
named it, and its lines added and removed (`rows/files`, parity map 11c). A read, edit, delete or move call names its
path, every diff's paths and every location; a shell call names the files its command hands a known reader (`cat`,
`head`, `tail`, `wc`, `sed`, `grep`, `rg` and the like, through `sh -c`, `cd` and quotes) and the targets of `>`, `>>`
and `<`; a prompt, a turn's start or a steer, names any absolute or `~/` path in its text and the images it carried,
listed by the name they were attached with. Search, fetch, think and other calls name nothing. A call or prompt counts
once however many rows it wrote or times it names the file; a failed call counts but adds no lines, and a call that
changed several files lists each with its own lines. A path inside the session's folder and its whole path are one
file, as are `~/a` and the home path. A file only a shell call or a prompt named is listed while it's a file on the
session's machine, so a pattern, a flag's value or a removed file isn't; a file an edit named stays when it's gone; a
folder and the session's own folder never list. A remote session's list comes from its machine; from this machine's
copy of its rows nothing is checked on disk. A click on a file, inside the session's folder or outside it alike, opens
it through the normal open with the session and the source it was listed from; an attached image opens full size, as
in Attachments. The tab reads the list when it's shown and when another session is picked while it's
shown, and doesn't follow the live rows, as the old app's didn't, so a remote session picked before its rows are
copied here can list too few until the tab is shown again. With nothing listed it says files the session reads or
edits are listed there; a read that fails or is cancelled says why. A read inside a script (`python x.py`) isn't seen.

g0-shape#14 Under Files touched, the Context tab's Attachments card lists the images in the message box first, from the
composer's own pictures with no read and following the draft live, then the images the selected session's prompts
carried, newest prompt first, each with a thumbnail (g0-shape#18), the name it was attached with and a type chip such as
`image/png`, the draft's own type or else the name's extension, "type not reported" when neither says
(`rows/attachments`, parity map 11d: each prompt's "Attached image:" lines, from this machine's copy of the session's
rows). The heading's chip counts every row. A draft image that's also listed as sent shows once, as sent, and a prompt
sent while the tab shows reads the sent images again, keeping the list shown meanwhile, so a sent image moves from the
drafts to the sent ones. A click opens a draft's picture, or reads a sent image by its file name in the store and opens
it full size, as Chat's chips do; one that won't open says why. A remote session's images stay on its machine, so its
rows don't open and a note names the machine. With nothing attached it says images added to the message box are listed
there; a read that fails says why. It reads when Files touched does. To stay inside the socket's 4 MB answer, the read
sends only the end of each prompt, where the lines are, and the newest 50 prompts; when it leaves older ones out, the
tab says so.
g0-shape#15 The drawer's History tab lists the sessions the sessions list has loaded, by project (its folder or repo,
and its machine when it isn't this computer), the project with the newest session first (parity map 12a). Each
project starts folded, with its count; a click opens or folds it. Open, it lists its sessions newest first, 25 at a
time, with a "Show 25 more" line while more are loaded, each row with its title, agent and age ("now", "5m", "3h",
"2d", "1w", "1mo", "1y" by the backend clock, ticking only while the tab is shown). A click on a session opens it, as
the sessions column does. A line at the end loads the list's next page while there is one: older open sessions, then
Settled's (sessions-list#9), since History lists every session. With no sessions, and none left to load, it
says they appear here; before the backend starts it says it's waiting. Every session has its own checkout, so there's
no worktree level; sessions found in the agents' own folders, and resuming them, are parity map 12b to 12d.
g0-shape#16 The Context tab shows four cards in one scrolling column, Session, Context usage, Files touched and
Attachments, each a sheet lifted off the panel with a muted icon, a heading and, where it has one, a count chip. With
no session picked one message replaces them all: "No session selected", and that picking one shows what it runs with,
how much of its context it has used and the files it has touched. The Session card's three rows, Model, Effort and
Access mode, read the session's agent config in the composer's own words, as its pills do, the model falling back to
the newest turn's reported model. A value nobody sent, a config not yet read, or a session the composer doesn't read
says "not reported by Claude" (the session's provider by name) in muted text, never a default. A pick shows at once; an
agent changing its own mode mid-turn shows when the turn ends. Another machine's session with the same id reads as not
reported, never as the selected one's.
g0-shape#17 The Context usage card says "Current context: 51,285" and "of 1,000,000" from the session's newest context
report, then a thin bar and its percent, rounded and at most 100, only when both are reported and the window is above
zero; then "Session total" in tokens, every turn's reported total added up (`rows/tokens`, from the session's own
machine, and "Reading…" until it answers); then "In" and "Out", the newest turn's that reported any, so a cancelled
turn's zeros leave the last figures showing. In is the provider's uncached count, as the chat's turn line shows it.
Each missing figure says it isn't reported by the provider; a total that can't be read reads that way too, and leaves
the lists alone. When no total was reported, used is above the window and in plus out fit in it, used was a running
total: the card shows in plus out as the current context and used as the session total, once the total has been read.
The total is read when the tab is shown, when another session is picked while shown and when a turn ends while shown,
and turns that were stopped, failed or ran before this card reported no total, so it can read low on those sessions.
g0-shape#18 A sent image's row shows a plain placeholder until its thumbnail arrives, 120 px on its longest edge,
read through `attachments/read` one image at a time, newest first, and scaled off the UI thread; the full image isn't
kept. One that fails, or is too large to decode, keeps the placeholder with no error and isn't tried again, and a
remote image with no valid source isn't read. A re-read keeps the thumbnails already made and reads only new images.
Hiding the tab, removing it, or picking another session stops the reads and drops every thumbnail, GPUI's copy
included; showing it again starts over. A source that's no longer valid stops the reads and tells Chat, as opening an
image does. Nothing reads while the tab is hidden, and there's no timer.
