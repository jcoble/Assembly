# What is left of diagnostics after the cut (map, not built)

Read at `c111ac0` (reconnect lane). Plan: B8 status and notes, B9 rows 2 to 4, D2, "the cut".

## 1. What each piece is for, and who calls it today

- `diagnostics/memory.sample` (`DiagnosticsFeature.cs:24`, `MemorySample.cs:17-25`). Reads app, WebContent and backend RSS (resident memory). Callers: HTTP `/page/memory.sample` (`PageRoutes.cs:48`) from `routes/+page.svelte:88` and `soak.ts:183`. The socket call has no caller yet. Throws without a WebContent process (`MemorySample.cs:23`).
- `diagnostics/drop` (`DiagnosticsFeature.cs:25`) and `POST /debug/drop` (`DebugRoutes.cs:14`). The socket one runs `hub.CloseSubscriptions` plus `SocketEndpoint.DropAll` (`DesktopBackend.cs:51-55`, `SocketEndpoint.cs:37`). The HTTP one closes only `/live` (`DebugRoutes.cs:18`). Callers: `dropLink` (`api.ts:115`) from `+page.svelte:81` and `soak.ts:239`. `DesktopBackendTests.cs:227` tests the HTTP one.
- `LiveHub.CloseSubscriptions` (`LiveHub.cs:134`). Also used by `Dispose` and shutdown (`ApiServer.cs:36`).
- `diagnostics/soak.append` (`DiagnosticsFeature.cs:26`) and HTTP `soak.append` (`PageRoutes.cs:77,104`). Callers: `soak.ts:215,298,485,492` via `pageCall`. Tested at `DesktopBackendTests.cs:257`.
- `PageRoutes` also holds `attachment.thumbnail`, `thumbnail_path`, `upload_path` (`:51-76`). Callers: `Composer.svelte:158,161,201`. No socket version exists yet. `Thumbnail.cs` and `ImageFile.cs` are used only here.
- `pageCall.ts` and its callers: `+page.svelte:6`, `soak.ts:25`, `Composer.svelte:6`, and a stub in `app/test/conversation.test.ts:43`.
- Soak tooling: `soak.ts` (the page loop), `?soak=` and `?counters=1` in `+page.svelte:137-142` and `:217`, `lag.svelte.ts`, `debug.svelte.ts`, `dotnet/scripts/mem.sh` (reads `backend.pid`, finds the app and WebKit by macOS "responsible pid"), `shell/assembly.sh start --counters` (`:46`), `scripts/features/*` (socket/rows/preferences checks, no soak). `scripts/soak-report.ts` is named in `soak.ts:23` but does not exist.
- App Nap hold: already moved. `DesktopBackend.cs:81,88` calls `HoldActivity(summaries.AnyRunOpen)`. `TurnActivity` is untouched by the cut.

## 2. The cut: delete, move, keep, in order

1. **Move first (nothing deleted while called):** thumbnail and path-upload calls to the attachments feature, if GPUI still needs them (question 1). `Thumbnail`/`ImageFile` go with `PageRoutes` otherwise.
2. **Delete together:** `app/` (Svelte), `pageCall.ts`, `api.ts` `dropLink`, `soak.ts`, `lag.svelte.ts`, the page's debug counters, `?soak`/`?counters`, the `conversation.test.ts` stub. Then `PageRoutes.cs`, `DebugRoutes.cs`, their lines in `DesktopBackend.cs:44,59-60`, and both entries in `scripts/lint.d/40-routes.sh:16-17` (the script fails if a listed file maps no route). Then `LiveHub.CloseSubscriptions` as a drop path, with `/live`. Keep `Dispose`/shutdown behaviour: rename or inline.
3. **Test changes:** delete `DesktopBackendTests.cs:227` (HTTP drop) and `:256-264` (page calls). Add no replacement for drop: `SocketTests.Drop.cs` already covers it.
4. **Stays:** `DiagnosticsFeature` (all three calls), `DiagnosticsHost`, `TurnActivity`, `soak.append`, `mem.sh`, the backend-side hold.
5. **Moves out of the repo's page:** the soak becomes a script outside the app that drives the window through `/socket` (D2 A), including `diagnostics/drop` mid-turn. Counters (D2 B) come from the backend `clock` topic. Frame gaps and key-to-paint need the GPUI window's own meter (question 2).

## 3. Memory sampler for a GPUI window

Smallest design: sample the window's own process, drop WebContent.

- The window is the process that launched the backend (g0-host starts `serve`), so macOS's responsible pid for the backend (`ResponsibleFor`, `MemorySample.cs:73`) is already the window when it is launched as an app. No new flag for that case.
- Change `Read()` on macOS: `appPid = ResponsibleFor(backend)`, return app and backend RSS, `webContentPid`/`webContentRssKb` null. Delete `WebContentPid` and the `pgrep` (`:53-60`). Keep the record's shape for now; remove the two WebContent fields in the same change that removes the page reader (`+page.svelte:89`).
- If Jesse wants it robust when started from a terminal (responsible pid is then the terminal), add one launcher argument `--window-pid N` to `DesktopOptions.cs:42` and use it, falling back to the responsible pid. Not recommended until a run needs it.
- `mem.sh` needs only its `webkit` line dropped (`app` is found by bundle path, which changes with the GPUI app).

## 4. Failing tests first

- `Memory_read_on_a_mac_without_a_webcontent_process_answers_app_and_backend` (`MacFact`, new, beside `TurnActivityTests`): reading has app pid, no throw, WebContent fields null.
- `Memory_sample_answers_the_hosts_reading` updated to the reduced shape (`DiagnosticsSocketTests.cs:57`).
- Attachments: socket versions of thumbnail and path upload, if kept (question 1).
- Cut: `PageRoutes_and_DebugRoutes_are_gone`, a route-list test that `/page/*` and `/debug/drop` answer 404; `40-routes.sh` passes with both entries removed.
- A socket test that `diagnostics/soak.append` rejects a bad name already exists in spirit; keep.

## 5. Open questions for Jesse

1. Does the GPUI composer still need backend thumbnails (ImageIO, base64) and path upload, or does it make its own previews?
2. Frame gaps and key-to-paint: keep a GPUI-side lag meter, or accept counters-only (D2 B)?
3. Must the memory meter work when the backend is started from a terminal, not the app (decides `--window-pid`)?
4. Keep `soak.append` in the backend (the soak script could write its own file) or delete it with the page?

## 6. Risks

- Deleting `CloseSubscriptions` as a drop path while `Dispose` and shutdown use it: keep one method, drop only the drop use.
- `drop` also kills the caller's socket, so a soak driver must treat a missing answer as success and reconnect.
- `MemorySample` throws today on a GPUI Mac: any caller before step 3 gets an error.
- The 40-routes list "only shrinks": deleting one file without the other fails lint.
- Thumbnail removal strands `Thumbnail.cs`, `ImageFile.cs` and their tests if question 1 is "no".
