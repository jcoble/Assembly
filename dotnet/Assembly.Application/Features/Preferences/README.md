# Preferences (the reference feature)

How a feature is laid out in the backend. Copy this shape.

- One folder per feature, one class per file. A feature every machine serves (its own files, git, terminals) lives in
  `Assembly.Engine/<Feature>/` and has a line in `Engine/Features/MachineFeatureList.cs`. A feature only the Mac's
  window needs lives in `Assembly.Mac/<Feature>/` and has a line in `Assembly.Mac/Features/MacFeatureList.cs`.
  Preferences is Mac-only, so Workbox Test has no preferences table. Every feature has to say which it is.
- `<Feature>Routes.cs` has a static `Add(routes, context)` that makes the feature's `FeatureCalls` and adds each call
  with `calls.Map("<method>", handler)`, plus one method per call. That's the only way to add a route
  (`scripts/lint.d/40-routes.sh` fails on a `MapPost` or `MapGet` anywhere else).
- The feature's tables are in its own `<Feature>Schema.cs`, in the host's SQLite file (`FeatureContext.Database`).
- Tests: `Assembly.Tests/Draft/<Lane>/` while the behavior list is a draft, each naming its line (`// Pins: base#7`).

Calls: `get {}` answers every saved value as `{ key: value }`; `set { values: { key: value } }` saves those keys and
leaves the rest. The page (`app/src/lib/preferences/`) owns the keys and their defaults.

## Where the plumbing lives

Every feature call goes through one helper on each side, so the repeated work is done once and can't be forgotten:

- Page: `app/src/lib/call.ts`. `call(feature, method, body, { key, signal })` makes the request, and `save(feature,
  key, value)` saves a setting.
- Backend: `Engine/Features/FeatureCalls.cs`. `Map(method, handler)` adds a call. It reads the body, answers
  `{result}`, 400 `{error}` for a `CallFailed` or a bad body, 404 for a method the feature doesn't have, and 500
  `{error}` for anything else, which it logs with the exception. It logs each call's name at Debug and hands the
  handler the request's cancellation token. Once the page has gone, it writes nothing.

Calls go over HTTP (`POST /<feature>/<method>`); pushes such as `/events` and `/live` go over WebSockets. Only those
two files know the transport.

## Stopping work nobody wants any more (required)

When you switch tabs fast, the page starts a load for each tab. Only the last one matters, so the others must stop,
in the page and here (base#10). It works as a chain:

1. The page's `call()` makes the request's abort signal itself. A newer call with the same `key` aborts the older
   one, and so does the owner closing (`signal`, such as a panel) or 30 s passing. A feature never makes its own.
2. The aborted signal closes the HTTP request.
3. Here, a closed request cancels `HttpContext.RequestAborted`. `FeatureCalls` passes it to every handler as its
   `cancellationToken`, and the handler must pass it on to everything it waits for: the database
   (`ExecuteReaderAsync(cancellationToken)`, as `PreferenceStore.ReadAllAsync` does), files, and child processes
   such as git, which are killed when it fires. Then the work really stops instead of running on for nothing. The
   build fails on a call that could take the token and doesn't (CA2016, in `dotnet/.editorconfig`).
4. Saves are the exception. They aren't cancelled partway: the page waits until the changes stop, sends one save,
   and a newer save replaces it. `PreferenceStore.Save` takes no token for that reason.

`get` shows steps 1 to 3 and `set` shows step 4. The test for step 3 is `Assembly.Tests/Draft/Base/FeatureCallsTests.cs`.
A call forwarded to another machine (the Backend part) will carry the cancel there too. The plan thread will expand
this section.
