# desktop/: the Rust GPUI window

These rules add to the root `AGENTS.md`.

## Comments

Jesse reads Rust less easily than C#, and the comments are how he follows the desktop code. Write them for a reader who knows the product and C#, but not Rust or GPUI.

- A file opens with a `//!` header: what it is for, who calls it, what it calls.
- A type or non-trivial function gets a `///` line in product terms.
- A longer function gets a `//` comment at each step: what it does and why.
- Explain a Rust or GPUI idiom in a few words where a C# reader would not guess it. Example: `// Take the attachments out of the composer, leaving it an empty list, so they travel with this send.`
- Enough to follow what is going on when looking something up, and no more. Full sentences in the product's words. History goes in the commit message.
- Add or improve the comments in a file or type when you work in it, and fix a comment in the same change that makes it false. Reviewers check them.

## UI

- Build from the shared components in `src/components/` ([component catalog](https://app.notion.com/p/40e394b0689d81f2962dfc798918a034)). When they lack what you need, take the component from GPUI Kit's list and bring it into `src/components/`. A component you build that could be used again goes there too.
- Colours, spacing and sizes come from the shared theme tokens (`src/core/theme/`, `themes/`).
- Rows and cards expand with `src/core/ui/row_drop_down.rs`.

## Tasks, tests and builds

- A `Task` lives in a field of its owner, so dropping the owner cancels it. `scripts/guard.sh` runs before every commit and refuses `detach()`, OS threads outside `src/core/socket/` and `src/core/backend/`, and sockets outside `src/core/socket/`.
- Tests live in a `tests.rs` or `*_tests.rs` file beside the code, or in `tests/`. Run one module's tests from `desktop/` with `cargo test --locked <module path>`, for example `cargo test --locked core::theme`.
- Builds on the Mac are allowed: ask Jesse first, and check the free disk space (`df -h`) before you start.
