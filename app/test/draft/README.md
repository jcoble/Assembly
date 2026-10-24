Draft tests: written from a behavior list Jesse hasn't corrected yet (`docs/behavior/<lane>.md`, AGENTS.md "Porting a
feature"). One folder per feature, under `app/test/draft/<feature>/`. They aren't locked and follow the
normal test rules. Each names the list line it tests (`// Pins: base#2`). Once Jesse has corrected the list,
`scripts/pin.sh <feature>` moves them into `app/test/pinned/`, after any changes the correction needs.
