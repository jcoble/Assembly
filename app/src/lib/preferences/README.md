# Preferences (the reference feature)

How a feature is laid out in the page. Copy this shape.

- One folder per feature, `app/src/lib/<feature>/`, one job per file.
- State is a `$state` object with one entry per key, changed one key at a time; nothing replaces the whole object.
- Tests: `app/test/draft/<lane>/` while the behavior list is a draft, each naming its line (`// Pins: base#7`).

`preferences.svelte.ts` holds every key and its default. A feature adds its keys there, reads `preferences.<key>`,
and changes one with `setPreference(key, value)`. `loadPreferences()` runs once at launch (`routes/+page.svelte`); a
key you changed before it answered keeps your new value.

## Where the plumbing lives

Features never call `fetch`, make an abort signal or start a timer to talk to the backend. They use the two helpers in
`lib/call.ts`, the one file that does the repeated work (and the one file `app/scripts/guards.sh` lets do it):

- `call(feature, method, body, { key, signal })` sends one call. It always resolves, never throws, with
  `{ ok: true, result }`, or `{ ok: false, why }`. `why` is `'failed'` when the call failed, which `call()` has
  already told the page once (`onFailure`, set in `routes/+page.svelte`), or `'aborted'` when it was cancelled on
  purpose, which isn't an error and shows nothing. Check `ok` before using `result`, so a delete or a commit always
  knows whether it worked. It keeps the fetch counter.
- `save(feature, key, value)` saves one setting. Changes within 300 ms of each other go as one `set` call with the
  newest value of each key. A feature has one save on the way at a time, so an older save never lands after a newer
  one; changes made meanwhile go when it's back. When the window closes or goes to the background, whatever is
  waiting goes at once. Features save their settings through `setPreference()` (preferences) or `save()`, and never
  debounce on their own.

Calls go over HTTP (`POST /<feature>/<method>`); pushes such as `/events` and `/live` go over WebSockets. Only
`call.ts` and the backend's `FeatureCalls.cs` know the transport. The Mac's own `/page` calls (memory, thumbnails,
the soak) still go through `host/pageCall.ts` until they move to features.

Not proven in the app yet: that WKWebView fires `pagehide` or `visibilitychange` when the app quits, so the close
flush only has node tests until the move thread checks it in the window on the Mac.

## Stopping work nobody wants any more (required)

When you switch tabs fast, each switch starts a load, and only the last one matters. The rest must stop, here and in
the backend (base#10):

1. Give a load a `key`. `call()` makes its abort signal, and a newer call with the same key aborts the one still
   running. Pass `signal` when something owns the load (a panel): its closing aborts it too.
2. `call()` hands the signal to the request, so aborting it closes the request.
3. The backend sees the request close and stops the work it started for it: database reads, files, child processes
   like git (`dotnet/Assembly.Mac/Preferences/README.md` says how).
4. Saves are the exception: debounced, the newest replaces the rest, and once sent they finish.

`loadPreferences()` shows 1 to 3; `setPreference()` shows 4. The tests are `app/test/draft/base/call.test.ts`. The plan
thread will expand this section.
