Draft for Jesse to correct.

- terminal#1: The terminal opens the account's login shell in the session's folder, or home until its checkout exists. A missing folder produces an error showing its path.
- terminal#2: Typed and pasted bytes reach the program in order. Cancelling a write stops that writer before another write starts.
- terminal#3: Output keeps its original bytes, including text split across reads.
- terminal#4: Resizing changes the program's terminal size.
- terminal#5: Leaving the view keeps the shell running. Returning can replay the last 4 MB of output and continue with live output.
- terminal#6: Each session has its own shell and output.
- terminal#7: The program's exit status follows its final output. Output remains available until the terminal is closed.
- terminal#8: Closing the terminal or stopping its backend ends its programs and cancels pending input and output work.
- terminal#9: Reopening a running session's terminal reuses its shell. Reopening after exit starts a new shell.
- terminal#10: Running a separate command does not replace the session's shell.
- terminal#11: An unknown session cannot start a terminal, even with an explicit folder.
- terminal#12: A session or helper belonging to another machine cannot start a local shell. Until remote terminals are available, opening it reports that limitation.
