# G0 shape: behavior list

Draft by Claude, not corrected by Jesse.

The GPUI window's shape: the sessions column, the center, the tools drawer and the bottom dock on the GPUI kit's
DockArea, the top row with the Home button, and the utility strip. Sources: notes/gpui-g0/g0-lanes.md (lane g0-shape)
and notes/gpui-g0/look-and-behavior-carryovers.md section 2, with the old app's commits. Settings and the palette are
g0-host's; what each pane shows belongs to the lane that owns that pane.

g0-shape#1 The window opens in the old app's layout. The sessions column is on the left, and Chat is in the center.
The drawer is on the right with Files, Source control, Agents, Tasks, Worktrees, Run, Context and History, and it's
closed at launch. The bottom dock holds Problems and Terminal, and it's closed too.
g0-shape#2 Any pane can be dragged anywhere, and any divider can be resized.
g0-shape#3 The layout is kept across relaunches, including the sessions column's width and whether it's folded
(491a4447, 5bfc3360). A layout saved by an older build, or one that can't be read, opens the default layout instead.
So does a launch with no backend, or one whose backend never gets ready: the window is never left empty.
g0-shape#4 Launching the app doesn't save the layout. Changes made together save once, and a save is never queued
behind older ones (378bc67d).
g0-shape#5 Opening the drawer pushes the panes over instead of covering them. Closing it gives the center its width
back (289c449b).
g0-shape#6 The Home button sits above the sessions column and keeps its width, so the dividers line up with the top
row. It follows the column while the column's edge is dragged (f4898b79, 668f7350).
g0-shape#7 The drawer's tabs, Sessions, Chat and Problems have no close button (951f2b43).
g0-shape#8 A pane shows nothing until the lane that owns it fills it, and nothing hidden is mounted (6353dc4d).
g0-shape#9 The utility strip under the panes has Resources and Usage, and each opens a popover anchored to it, on
screen and above the strip (fa39c659, 36abf71a, a56526ec). Nothing in the strip polls.
