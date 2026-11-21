# dotnet/: the C# backend

These rules add to the root `AGENTS.md`.

- Pass the `CancellationToken` all the way down. Hold every task you start: `scripts/lint.d/80-no-bare-task-starts.sh` refuses a discarded or bare `Task.Run`, a new `Thread` and `async void`, in tests too.
- Related database writes share one transaction. Publish state after the commit.
- A change to operational state (sessions, workflows, the rail, the Build Board) goes through an Assembly.Engine command, in `Assembly.Engine/EngineCommands.*.cs`. Engine types reference no outside system; `Assembly.Tests/Architecture/EngineArchitectureTests.cs` checks that.
- Some files are a contract the remote must match: the socket, row and message shapes, the stored data and the adapter contract. The list is `hardContract` in `scripts/dev-remote-identity.ts`; it includes `Assembly.Application/Common/Acp/` and `Common/Agents/`. A commit that changes one must raise `version` under `[package]` in `desktop/Cargo.toml` (and `desktop/Cargo.lock`), or the pre-commit hook refuses it.
- A commit that changes a .NET file runs the architecture tests (`Assembly.Tests/Architecture`) and `dotnet format` in the pre-commit hook, so expect a build.
- Run one test class from `dotnet/`:

  ```sh
  MSBUILDDISABLENODEREUSE=1 dotnet test Assembly.Tests --filter "FullyQualifiedName~<ClassName>"
  ```
