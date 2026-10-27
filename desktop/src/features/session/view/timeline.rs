//! `Timeline` owns the virtual list (the kit's MessageScroller, which lays out only the rows on screen and keeps the
//! reading place when a page goes on top) and the entries it draws.
//! Where it scrolls is `Follow`'s decision. Each session's reading place is kept in memory, written when the session is
//! left, never on a draw; G0 doesn't keep it across
//! relaunches. Replaces the virtualizer half of
//! Conversation.svelte and session/scroll.ts.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::message_scroller::{MessageScroller, MessageScrollerState};
use gpui_kit::*;

use crate::features::session::model::entries::Entry;
use crate::features::session::model::follow::{Follow, FollowEvent, FollowStep};

/// Draws one entry; `last` is the list's last, which has no space below it. The space between rows is the row's own,
/// so a row growing in place grows its space with it.
pub type RenderRow = Rc<dyn Fn(&Entry, bool, &mut Window, &mut App) -> AnyElement>;

/// Load the next older page when the top row on screen is this close to the first loaded entry.
pub const LOAD_OLDER_WITHIN: usize = 10;

pub struct Timeline {
    scroller: Entity<MessageScrollerState>,
    /// Draws one entry: the pane's, reading its view and sending its actions.
    render_row: RenderRow,
    session: Option<String>,
    /// The entries on the list, each with its key.
    entries: Rc<Vec<(String, Entry)>>,
    follow: Follow,
    /// Each session's reading place, by session id: the key of the top row on screen when it was left.
    places: HashMap<String, String>,
    /// The reading place to go back to when the session's first page lands, if its row is in it.
    restore: Option<String>,
    /// The top row on screen when the scroller last changed.
    top: usize,
    _scrolled: Subscription,
    /// How many times the entries were worked out again, for tests.
    #[cfg(test)]
    pub builds: usize,
}

/// What the timeline tells the pane.
pub enum TimelineEvent {
    /// The reader scrolled to near the top: time for the next older page.
    NearTop,
}

impl Timeline {
    pub fn new(render_row: RenderRow, cx: &mut Context<Self>) -> Self {
        let scroller = cx.new(|cx| MessageScrollerState::new(0, cx));
        // Only the reader moving the top row counts: while the list follows its end, rows added there don't load
        // older ones.
        let scrolled = cx.observe(&scroller, |timeline: &mut Timeline, scroller, cx| {
            let (top, following) = (
                scroller.read(cx).logical_scroll_top().item_ix,
                scroller.read(cx).is_following_tail(),
            );
            if top != timeline.top {
                timeline.top = top;
                if top < LOAD_OLDER_WITHIN && !following {
                    cx.emit(TimelineEvent::NearTop);
                }
            }
            cx.notify();
        });
        Timeline {
            scroller,
            render_row,
            session: None,
            entries: Rc::default(),
            follow: Follow::default(),
            places: HashMap::new(),
            restore: None,
            top: 0,
            _scrolled: scrolled,
            #[cfg(test)]
            builds: 0,
        }
    }

    /// Switches to another session's entries: keeps the reading place of the one it leaves, and goes back to the
    /// one it left the new one at when the first page holds that row; otherwise, or when it was left at its end, it
    /// goes to the end.
    pub fn set_session(
        &mut self,
        session: Option<String>,
        entries: Vec<(String, Entry)>,
        cx: &mut Context<Self>,
    ) {
        if let Some(left) = self.session.take() {
            let top = self.scroller.read(cx).logical_scroll_top().item_ix;
            match self
                .entries
                .get(top)
                .filter(|_| !self.scroller.read(cx).is_following_tail())
            {
                Some((key, _)) => self.places.insert(left, key.clone()),
                None => self.places.remove(&left),
            };
        }
        self.restore = session
            .as_ref()
            .and_then(|session| self.places.get(session))
            .cloned();
        self.session = session;
        self.follow = Follow::default();
        let count = entries.len();
        self.entries = Rc::new(entries);
        self.scroller
            .update(cx, |scroller, cx| scroller.reset(count, cx));
        cx.notify();
    }

    /// Takes the entries worked out again from the view, and splices in only what changed; `changed` is an item
    /// updated in place, which is measured again.
    pub fn set_entries(
        &mut self,
        entries: Vec<(String, Entry)>,
        changed: Option<&str>,
        cx: &mut Context<Self>,
    ) {
        let old = &self.entries;
        let same = |a: &(String, Entry), b: &(String, Entry)| a.0 == b.0;
        let start = old
            .iter()
            .zip(&entries)
            .take_while(|(a, b)| same(a, b))
            .count();
        let end = old[start..]
            .iter()
            .rev()
            .zip(entries[start..].iter().rev())
            .take_while(|(a, b)| same(a, b))
            .count();
        #[cfg(test)]
        {
            self.builds += 1;
        }
        let removed = start..old.len() - end;
        let added = entries.len() - start - end;
        let remeasure = changed.and_then(|key| entries.iter().position(|(entry, _)| entry == key));
        self.entries = Rc::new(entries);
        self.scroller.update(cx, |scroller, cx| {
            if !removed.is_empty() || added > 0 {
                scroller.splice(removed, added, cx);
            }
            if let Some(at) = remeasure {
                scroller.remeasure_items(at..at + 1, cx);
            }
        });
        // The first page's rows are where the place can come back; one older than them opens at the end, and an older
        // page that holds it later doesn't jump there. A Working line alone isn't the page.
        let paged = self
            .entries
            .iter()
            .any(|(_, entry)| matches!(entry, Entry::Item(_)));
        let restore = self.restore.take_if(|_| paged);
        if let Some(at) =
            restore.and_then(|key| self.entries.iter().position(|(entry, _)| *entry == key))
        {
            self.scroller
                .update(cx, |scroller, cx| scroller.scroll_to_item(at, cx));
        }
        cx.notify();
    }

    /// Measures and draws one row again, by its key, leaving the entries as they are.
    pub fn redraw_row(&mut self, key: &str, cx: &mut Context<Self>) {
        if let Some(at) = self.entries.iter().position(|(entry, _)| entry == key) {
            self.scroller
                .update(cx, |scroller, cx| scroller.remeasure_items(at..at + 1, cx));
            cx.notify();
        }
    }

    /// Tells the timeline what happened, for the one scroll the list doesn't make itself.
    pub fn follow(&mut self, event: FollowEvent, cx: &mut Context<Self>) {
        if let FollowStep::ShowAtTop(key) = self.follow.on(event)
            && let Some(at) = self.entries.iter().position(|(entry, _)| *entry == key)
        {
            self.scroller
                .update(cx, |scroller, cx| scroller.scroll_to_item(at, cx));
        }
    }

    /// The top row on screen, for tests.
    #[cfg(test)]
    pub fn top_row(&self, cx: &App) -> usize {
        self.scroller.read(cx).logical_scroll_top().item_ix
    }

    /// Scrolls as the reader would, for tests.
    #[cfg(test)]
    pub fn scroll_to_row(&mut self, row: usize, cx: &mut Context<Self>) {
        self.scroller
            .update(cx, |scroller, cx| scroller.scroll_to_item(row, cx));
    }

    /// The entries on the list, for tests.
    #[cfg(test)]
    pub fn entries(&self) -> Vec<Entry> {
        self.entries
            .iter()
            .map(|(_, entry)| entry.clone())
            .collect()
    }
}

impl EventEmitter<TimelineEvent> for Timeline {}

impl Render for Timeline {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        let (entries, render_row) = (self.entries.clone(), self.render_row.clone());
        let count = entries.len();
        MessageScroller::new("timeline", self.scroller.clone(), move |at, window, cx| {
            entries.get(at).map_or_else(
                || div().into_any_element(),
                |(_, entry)| render_row(entry, at + 1 == count, window, cx),
            )
        })
        // The kit's space between rows sits outside them; the rows draw their own (RenderRow).
        .with_row_style(StyleRefinement::default().pb_0())
        .size_full()
    }
}
