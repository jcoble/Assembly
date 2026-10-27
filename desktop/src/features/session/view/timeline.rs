//! `Timeline` owns the virtual list (the kit's MessageScroller, which lays out only the rows on screen and keeps the
//! reading place when a page goes on top), the entries it draws, each item's `Kept`, and the folds the reader opened.
//! Where it scrolls is `Follow`'s decision. Each session's reading place is kept in memory, written only when the
//! top item changes from scrolling and when the session closes, never on a draw; G0 doesn't keep it across
//! relaunches. Replaces the virtualizer half of
//! Conversation.svelte and session/scroll.ts.

use std::collections::{HashMap, HashSet};
use std::rc::Rc;

use gpui_kit::component::message_scroller::MessageScrollerState;
use gpui_kit::*;

use crate::features::session::model::entries::Entry;
use crate::features::session::model::follow::Follow;
use crate::features::session::model::kept::Kept;

pub type RenderRow = Rc<dyn Fn(&Entry, &mut Window, &mut App) -> AnyElement>;

/// Load the next older page when the top row on screen is this close to the first loaded entry.
pub const LOAD_OLDER_WITHIN: usize = 10;

pub struct Timeline {
    scroller: Entity<MessageScrollerState>,
    /// Draws one entry: the pane's, reading its view and sending its actions.
    render_row: RenderRow,
    session: Option<String>,
    entries: Vec<Entry>,
    /// Each item's kept state, by item key.
    kept: HashMap<String, Kept>,
    /// The folds the reader opened, by run id.
    open_folds: HashSet<String>,
    follow: Follow,
    /// Each session's reading place, by session id.
    places: HashMap<String, ListOffset>,
}

impl Timeline {
    pub fn new(_render_row: RenderRow, _cx: &mut Context<Self>) -> Self {
        todo!()
    }

    /// Switches to another session's entries: keeps the reading place of the one it leaves, and goes back to the
    /// one it left the new one at, or the end.
    pub fn set_session(&mut self, _session: Option<String>, _entries: Vec<Entry>, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Takes the entries worked out again from the view, and splices, appends or measures again only what changed.
    pub fn set_entries(&mut self, _entries: Vec<Entry>, _cx: &mut Context<Self>) {
        todo!()
    }

    /// Whether the reader is near enough the top for the next older page.
    pub fn near_top(&self, _cx: &App) -> bool {
        todo!()
    }

    pub fn toggle_fold(&mut self, _run_id: &str, _cx: &mut Context<Self>) {
        todo!()
    }
}

impl Render for Timeline {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
