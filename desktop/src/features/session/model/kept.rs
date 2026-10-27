//! `Kept` is what one item keeps while the virtual list has it off screen, so it draws again as it was left: an
//! opened tool call, thinking, fold or long prompt, a long diff shown in full, and an open tool's detail. A tool's
//! detail is read again only when its state changes or the call finishes. Ported from rows/kept.svelte.ts.

use std::time::Instant;

use crate::features::session::rows::tool::{ToolDetail, ToolState};

#[derive(Clone, Debug, Default)]
pub struct Kept {
    pub open: bool,
    pub all_diff: bool,
    /// An open tool's detail, and the state it was read for.
    pub detail: Option<(ToolState, ToolDetail)>,
    /// When it was last opened or shut: its chevron and body ease from then.
    pub toggled_at: Option<Instant>,
}
