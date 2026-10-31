Draft for Jesse to correct.

- terminal#1: The terminal opens the shell named by the backend's SHELL setting (else /bin/sh) in the session's folder, or home until its checkout exists. A missing folder produces an error showing its path.
- terminal#2: Typed and pasted bytes reach the program in order. Cancelling a write stops that writer before another write starts.
- terminal#3: Output keeps its original bytes, including text split across reads.
- terminal#4: Resizing changes the program's terminal size.
- terminal#5: Leaving the view keeps the shell running. Returning catches up with terminal/read, replaying the last 4 MB of output, and continues with live output on the terminal topic on the terminal socket.
- terminal#6: Each session has its own shell and output.
- terminal#7: The program's exit status follows its final output. Output remains available until the terminal is closed.
- terminal#8: Closing the terminal or stopping its backend ends its programs and cancels pending input and output work.
- terminal#9: Reopening a running session's terminal reuses its shell. Reopening after exit starts a new shell.
- terminal#10: Running a separate command does not replace the session's shell.
- terminal#11: An unknown session cannot start a terminal, even with an explicit folder.
- terminal#12: A remote session's shell starts in its folder on its owning machine, through the local backend. Its machine-qualified id routes write, resize, read and close there; list includes its shell for reuse, and output is relayed on the local terminal socket. Unknown sessions cannot start a shell.
- terminal#13: All terminal calls and output use /socket/terminal, separately from chat, on both the window-to-backend and backend-to-remote hops. Other feature calls are refused there; terminal calls are refused on the chat socket.
