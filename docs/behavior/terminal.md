# Terminal: behavior list

Draft by Claude, not corrected by Jesse.

The terminal host is the backend half of W4 (plan section W, Sol P22). The backend owns each terminal's process and
passes its bytes through untouched; the window emulates them with alacritty's VT parser and GPUI draws them in the pane. Source: mac-command-bar 2089290
`tauri-svelte-preview/src-tauri/src/terminal/mod.rs`, a reference for details, not the source of truth.

terminal#1 A terminal opens in a folder you name and runs your login shell there, or one command in it that exits when it's done. A folder that no longer exists fails with its path.
terminal#2 What you type reaches the program, Ctrl-C included: the shell is the terminal's own, with job control, as in any terminal app.
terminal#3 The program's output reaches the window byte for byte, unchanged: the backend never decodes, re-encodes or cuts it, so a character split across two reads still draws whole.
terminal#4 Resizing the view resizes the terminal, and the program sees the new size. A size is kept between 20 and 300 columns and 4 and 100 rows.
terminal#5 Switching sessions disposes the terminal's view in the window, every time (its screen and its subscription), while the backend keeps the terminal running with its output. Bringing another tab to the front or closing the dock does the same. A dropped connection picks up where it left off, except during that replay, which then asks for the terminal to be shown again. Switching back builds a new view that shows the last 4 MB of output, then carries on live, with nothing missed or repeated. Jesse, 2026-10-27: "We have to make sure that it gets disposed of on each session switch but the pty keeps everything on the backend until session switches back to it."
terminal#6 A terminal keeps at most 16 MB of output; past that it drops the oldest down to 12 MB. A slow window can't make it keep more.
terminal#7 When the program exits, the window sees all of its output first, then its exit code (a signal shows as 128 plus its number). An exited terminal keeps its output readable until you close it.
terminal#8 Closing a terminal ends its program and everything the program started, and lets go of its output. Quitting the app or stopping a remote host closes every terminal it started.
terminal#9 A session asks for its terminal again and gets the one it already has while that one is still running. Once it has ended, the next ask closes it and starts a new one.
terminal#10 A terminal on a remote machine behaves the same as one on this computer.
terminal#11 The terminal draws in the theme's monospace font and its colors, light themes included: the text, background and cursor are the theme's, and the 16 ANSI colors are its base colors.
terminal#12 The bottom dock's Terminal tab shows the selected session's terminal, opened the first time the tab is shown with that session, never while the dock is closed. It starts in the session's folder, or the home folder until the session's first turn has checked one out. A session on another machine, or a helper of one, says its terminal can't be opened here yet.
terminal#13 Scrolling goes back through the output, and typing goes back to the live screen. Pasting (Cmd-V, or Ctrl-Shift-V) sends the clipboard, bracketed when the program asked for that.
