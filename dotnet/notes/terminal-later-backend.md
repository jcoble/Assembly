# Terminal: later backend steps and risks


Draft research by sub-agents at 3cb50fa, before the review fixes: line numbers in the terminal files may have moved.
Research on lane/terminal at 523f338. Paths are under `dotnet/` unless they start with `docs/`, `scripts/`, `shell/` or `dotnet/`; those are from the repo root.

## 1. Remote (terminal#10)

**Today.** The remote host already serves the terminal calls (`Assembly.Remote/Serve/ServeRun.cs:54-56`, disposed when the host stops). The desktop can't reach them: `RemoteMachine` speaks only typed session routes (`Infrastructure/Remote/RemoteMachine.cs:57-140`, `IMachine.cs:10-58`).

**After C2.** The plan has C2 relaying sessions and one rows subscription per session (`docs/plans/2026-10-26/implementation-plan.md:1511-1519`), plus forwarding `cancel` (`:489`). It names no general relay for any feature, so `terminal-shape.md:95-97` ("nothing terminal-specific") holds only if C2 builds one. The terminal needs:
- **Routing.** `terminal/start` has no session to route by (`StartArgs.cs`), so it needs a machine field on the frame or in its args. Later calls need machine-qualified ids or a desktop-side id-to-machine map; ids are GUIDs (`TerminalHost.cs:324`).
- **Rows passed through untouched.** Seqs are byte offsets (`terminal-shape.md:69-74`). Re-sequencing breaks `terminal/read`.
- **`terminals` list.** `TerminalInfo` has no machine field (`TerminalInfo.cs`).
- **Cancel and floods.** Forwarding `cancel` is needed (plan `:489`). A remote flood aborts the whole machine socket (see 2).

**Proving it now.** Add `scripts/features/terminal.sh` plus `terminal.ts` like `socket.ts`, against `CHECK4_HOST_API`, the Workbox Test host through its tunnel (`scripts/features/README.md`, `scripts/check4.sh:57,123`).

Tests: start, then `echo $((6*7))` prints 42 · resize, then `stty size` prints the new size · `sleep 30` then Ctrl-C returns a prompt · `exit 3` sends all output, then exit 3 · resubscribe with `after` leaves no gap or repeat · close kills a background `sleep` (check with `pgrep` over SSH).

## 2. Flood and backpressure

`Send` aborts the whole page socket once more than 16 MB is waiting (`Infrastructure/Socket/SocketConnection.cs:21,208-215`). A terminal flood kills chat on that socket too. Each PTY read becomes one row: a 64 KB buffer (`RunningTerminal.cs:116,195-203`), but at most 16 KB per master read (`Infrastructure/Terminal/UnixPty.cs:17`), and Linux usually returns 4 KB or less (assumed). Base64 adds a third.

Options:
- **A. Pull.** Rows carry only `seq` and `prevSeq`, and the window keeps one `terminal/read` in flight until it reaches the end. Real backpressure, no socket code; costs one round trip per echo (sub-ms locally, tens of ms over SSH, assumed). Still needs coalescing.
- **B. Hybrid.** Send data inline only when the read is small and the terminal was quiet before it (about 10 ms, assumed). Otherwise send offsets only, at most one row per 16 ms. Typing latency unchanged; a flood degrades to A; about 30 lines in `RunningTerminal` (assumed).
- **C. Per-subscription budget.** The socket drops a rows frame instead of aborting, and the gap in `prevSeq` makes the window read. Generic, touches the chat rows contract.

Recommend B.

Socket-level test (in `SocketTests.Topics.cs` style, with a real loopback socket and `_topics.Publish`): a client subscribes to a rows topic and stops reading. The test publishes 20 MB of 16 KB rows and asserts that a second subscription on the same socket still gets a change. It fails today. Feature tests: `yes | head -c 200M` ends with the exit row, and the window's last offset equals the end · a keystroke typed mid-flood is echoed within 1 s.

## 3. Native interop risks

- **Variadic `ioctl`.** Already handled: on Apple arm64, `SetSize` pads six register arguments so the `winsize` pointer lands on the stack (`Infrastructure/Terminal/PtyNative.cs:43-47,88`). The .NET team's supported answer is a C shim ([runtime#48752](https://github.com/dotnet/runtime/issues/48752)). Padding works "in some cases but not all"; the failures are HFA (float struct) arguments, not pointers. The runtime itself calls `ioctl` from C (`SystemNative_GetWindowSize`, [pal_console.c](https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_console.c)). vs-pty.net uses a plain `ioctl(int, ulong, ref WinSize)` ([NativeMethods.cs](https://github.com/microsoft/vs-pty.net/blob/main/src/Pty.Net/Mac/NativeMethods.cs)); assumed broken on Apple arm64. Keep the padding. Proof: `The_program_sees_its_size_and_a_resize` (`Tests/Draft/Terminal/UnixPtyTests.cs:51`) passing on the Mac.
- **Native AOT.** `Assembly.Remote` is not published AOT: the box runs a JIT `dotnet publish` (`dotnet/scripts/workbox.sh:108,118`). The AOT build is Desktop's (`shell/assembly.sh:4`, `dotnet/scripts/desktop.sh:58-62`), so `terminal-shape.md:49` names the wrong target.
- **DllImport vs LibraryImport.** Code is DllImport (`PtyNative.cs:7,57-88`); `terminal-shape.md:30` wrongly says LibraryImport. LibraryImport needs `AllowUnsafeBlocks`, which no project sets (`Directory.Build.props`, all csproj files). DllImport already builds under `IsAotCompatible` and `TreatWarningsAsErrors` (`ProcessGroup.cs:77`, `RemoteTunnel.cs:191`).
- **Flag values.** `O_NOCTTY` and `O_CLOEXEC` are 0x20000 and 0x1000000 on macOS, 0x100 and 0x80000 on Linux. `TIOCSCTTY` and `TIOCSWINSZ` are 0x20007461 and 0x80087467 on macOS, 0x540E and 0x5414 on Linux (`PtyNative.cs:16-20`). On macOS, `FIOCLEX` (0x20006601) sets close-on-exec instead (`:26-29`).
- **EIO.** Linux returns EIO after the last slave closes; it's treated as end of file (`UnixPty.cs:124-125`). That kernels deliver buffered output before the EIO is assumed. Test: `head -c 5000000 /dev/zero; exit` delivers exactly 5,000,000 bytes.
- **Other risks.** A disowned job that holds the slave open delays the exit row indefinitely (`RunningTerminal.cs:195-205`, `UnixPty.cs:99`). A `write()` to a program that doesn't read blocks, and its token can't interrupt it (`UnixPty.cs:60-70`). Tests: `nohup sleep 300 & exit` shows its exit · a write to `sleep 300` fails or ends once the terminal closes.

## 4. Mac check list

1. `scripts/lane.sh adopt terminal` (`scripts/lane.sh:6-8`).
2. `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/Assembly.Tests --filter "FullyQualifiedName~Terminal"`.
3. `shell/assembly.sh build` and `start`: a Native AOT osx-arm64 build. Start and resize a terminal through the socket (PTY under AOT).
4. `ASSEMBLY_REMOTE=workbox-test shell/assembly.sh start` and `scripts/check4.sh --feature terminal`.
5. Confirm `/usr/bin/perl` exists on the current macOS (assumed; `Agents/ProcessGroup.cs` needs it too).

## 5. Open questions for Jesse

- Should C2 build a general relay (machine field on call and sub frames), or should terminals get their own `IMachine` methods?
- Flood option B, or plain pull (A) with an extra round trip per echo remotely?
- Should a flood ever be allowed to close the page socket, or should the 16 MB abort skip rows topics?
- Exit while a disowned job holds the terminal: wait for it (as now), or publish the exit when the shell ends?
