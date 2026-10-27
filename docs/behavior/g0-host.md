# G0 host: behavior list

Draft by Claude, not corrected by Jesse.

The GPUI window's host: the window, the backend it starts, the native views (terminal and browser), the Cmd-K
palette and its commands, the open-something asks, and the lag meter. Sources: notes/gpui-g0/g0-lanes.md (lane
g0-host) and notes/gpui-g0/look-and-behavior-carryovers.md section 1, with the old app's commits.

## The window and the backend

g0-host#1 Launching the app opens one window and starts its own backend. Quitting the window stops that backend.
g0-host#2 When the backend stops on its own, the app starts a new one at once and reconnects when it's up, with no
waiting loop. A backend that stops before it was ever ready, or stops within 30 seconds of being ready three times in
a row, isn't restarted again and again: the app says it failed, with where its log is, and the palette's "Restart
backend" starts it again. Until the backend is up the window says
it's starting.
g0-host#16 Everything the backend prints goes to backend.log in its data folder.
g0-host#3 The backend's token never appears in the app's output or logs.
g0-host#4 Settings opens as a dialog over the window.

## Native views (the terminal and the browser)

g0-host#5 A terminal or browser shows only while its tab is the front tab of a pane that's showing. Session restore
never brings one up over another tab (821ec040).
g0-host#6 While the palette, a menu, a popover, a dialog or a tooltip is open, terminals and browsers step out of the
way, and come back when it closes (ded79246).
g0-host#7 A terminal or browser stays inside its pane, below the pane's tab bar, and moves and resizes with the pane;
nothing of it paints outside its column (b0845e27, c65922f6, 893cc7dd, ba5a652f).
g0-host#8 While a pane is dragged or a divider resized, terminals and browsers are hidden; on drag end they're placed
again where their panes now are.
g0-host#9 A hidden terminal or browser frees what it draws with (f127df9a), and closing its tab releases it (4f61e404).
Switching sessions releases the window's terminal view; the backend keeps the terminal's output until the session
is switched back to, and the view shows it again (Jesse, 2026-10-27).
g0-host#10 After a theme change the terminal shows the new theme's colors (34223b94).

## Palette, commands and opening things

g0-host#11 Cmd-K opens the command palette over the window, and Cmd-K or Escape closes it. Typing filters the commands;
Enter runs the chosen one and closes the palette.
g0-host#12 Every command a feature adds is in the palette, with its shortcut shown next to it, and the shortcut runs
the same command.
g0-host#13 Opening a file, a web page, a diff or a session from anywhere (a chat link, the file tree, the palette) goes
to the one place that shows that kind of thing.

## The lag meter

g0-host#14 Started with the meter on, the app writes one line every five seconds: frames, frame lag, longest draw,
key-to-screen, main-thread stalls and memory, in the spike meter's columns, so the numbers compare with the spike.
g0-host#15 With the meter off, nothing of it runs. A soak is a long run with the meter on, driven from outside the
window.

Not covered yet: kit tooltips, hover cards and context menus have nothing the app can read, so a terminal or browser
may still sit over them. The Mac check lists any.
