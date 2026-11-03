//! `Timeline` owns the virtual list (the kit's MessageScroller, which lays out only the rows on screen and keeps the
//! reading place when a page goes on top) and the entries it draws.
//! Follow handles live arrivals. The sole workspace codec persists the source-qualified session reading place.

use std::rc::Rc;

use crate::core::state::preferences::Preferences;
use crate::core::theme::text::TextRole;
use gpui_kit::component::message_scroller::{MessageScroller, MessageScrollerState};
use gpui_kit::*;
use crate::core::state::preferences::Preferences;
use crate::core::theme::text::TextRole;

use crate::features::session::model::entries::Entry;
use crate::features::session::model::follow::{Follow, FollowEvent, FollowStep};

/// Draws one entry; `last` is the list's last, which has no space below it. The space between rows is the row's own,
/// so a row growing in place grows its space with it.
pub type RenderRow = Rc<dyn Fn(&Entry, bool, &mut Window, &mut App) -> AnyElement>;

/// Load the next older page when the top row on screen is this close to the first loaded entry.
pub const LOAD_OLDER_WITHIN: usize = 10;

/// The strip under the rows that the scroll-to-bottom arrow sits in: its 32 px and its space from the edge.
const ARROW_STRIP: Pixels = px(48.);

pub struct Timeline {
    scroller: Entity<MessageScrollerState>,
    /// Draws one entry: the pane's, reading its view and sending its actions.
    render_row: RenderRow,
    session: Option<String>,
    /// The entries on the list, each with its key.
    entries: Rc<Vec<(String, Entry)>>,
    follow: Follow,
    /// The top row on screen when the scroller last changed.
    top: usize,
    _scrolled: Subscription,
    _appearance: Option<Subscription>,
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
        let appearance = Preferences::try_global(cx).map(|preferences| {
            let mut metrics = Preferences::read(cx, |appearance| {
                (appearance.ui_font(), appearance.ui_text(TextRole::Chat))
            });
            cx.observe(&preferences, move |timeline, _, cx| {
                let next = Preferences::read(cx, |appearance| {
                    (appearance.ui_font(), appearance.ui_text(TextRole::Chat))
                });
                if next != metrics {
                    metrics = next;
                    timeline.scroller.update(cx, |scroller, cx| {
                        scroller.remeasure_items(0..timeline.entries.len(), cx);
                    });
                    cx.notify();
                }
            })
        });
        Timeline {
            scroller,
            render_row,
            session: None,
            entries: Rc::default(),
            follow: Follow::default(),
            top: 0,
            _scrolled: scrolled,
            _appearance: appearance,
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
        cx.notify();
    }

    pub(super) fn workspace_place(&self,cx:&App)->super::session_pane::chat_codec::ChatPlace {
        let scroller=self.scroller.read(cx);let offset=scroller.logical_scroll_top();
        super::session_pane::chat_codec::ChatPlace {
            item_key:self.entries.get(offset.item_ix).map(|(key,_)|key.clone()),
            offset:f32::from(offset.offset_in_item),following:scroller.is_following_tail(),
        }
    }
    pub(super) fn restore_workspace_place(&mut self,place:&super::session_pane::chat_codec::ChatPlace,cx:&mut Context<Self>)->Result<(),String> {
        if place.following {
            self.scroller.update(cx,|scroller,cx|scroller.scroll_to_end(cx));
        } else if let Some(at)=place.item_key.as_ref().and_then(|key|self.entries.iter().position(|(candidate,_)|candidate==key)) {
            self.scroller.update(cx,|scroller,cx|scroller.scroll_to_item(at,cx));
        } else {
            // An anchor outside the coherent first page stays at the tail; later paging never jumps the reader.
            self.scroller.update(cx,|scroller,cx|scroller.scroll_to_end(cx));
        }
        cx.notify();Ok(())
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
        // The mock's feed starts 22 px under the header.
        .with_list_style(StyleRefinement::default().pt(px(22.)))
        // The scroll-to-bottom arrow is drawn over the bottom of the scroller. The rows stop above a strip tall
        // enough to hold it, so it never covers one.
        .with_content_style(StyleRefinement::default().flex_1().h_auto().mb(ARROW_STRIP))
        .flex()
        .flex_col()
        .size_full()
    }
}
