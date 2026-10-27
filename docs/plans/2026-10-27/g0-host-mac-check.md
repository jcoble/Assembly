# G0 host: what to check in the window on the Mac

Run through "Spike a GPUI session view", with the lane's own backend folder (`ASSEMBLY_BACKEND_DIR`) and
`ASSEMBLY_METER=<csv>` on. Never the base lane's ports, folders or data.

Before the first launch, open `$ASSEMBLY_BACKEND_DIR/lane.json` (shell/assembly.sh writes it) and check that `home`
is the lane's own folder, never `~/assembly-dotnet` or `~/.local/share/assembly`, and that `port` and `tokenFile`
are the lane's too. The launcher starts the backend with exactly those.

1. The window opens and the backend starts: `<home>/backend.log` gets a `listening on …` line. Then check both
   ways of leaving, each from a fresh launch: Cmd-Q, and closing the window with its red close button. Each time the
   app exits and no `Assembly.Desktop` is left running (`pgrep -fl Assembly.Desktop`). On Linux under Xvfb,
   `xdotool windowclose` left the app and its backend running, which may only be how xdotool closes a window; the
   close button on the Mac is the real check. The token appears nowhere in the output or backend.log.
2. Kill the backend from outside: a new one starts at once. Take its port first so it can't start: backend.log says
   so once, and the palette's "Restart backend" starts it after the port is free.
3. Cmd-K opens the palette (the tests in features/palette cover the keys; here, its look) with its slide-in; typing filters; Enter on "Restart backend" runs it and closes the
   palette; Escape clears the query, then closes; Cmd-K again closes it; a click outside closes it.
4. Once a terminal or browser pane exists (g0-shape, the terminal and browser lanes): the palette, a dialog (and
   its 250 ms fade-out), a menu, a popover and a pane drag each hide it, and it comes back where its pane is. A tab
   switch, a divider drag, zoom and a window resize keep it inside its pane. Note any tooltip, hover card or context
   menu it still covers.
5. The meter writes a line every 5 seconds in the spike's columns plus `footprint_mb`. Compare with the spike's
   numbers while hovering over the dock tabs and dragging a divider.
