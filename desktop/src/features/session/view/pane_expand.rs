//! Tool calls, thinking and questions open in place through the shared `ExpandRow` (core/ui/expand_row.rs): one per
//! item key, kept by the pane so a row scrolled away and back is as it was left. Each draw hands it its title and body
//! as they are now, so a late detail or more thinking grows it on the same curve. A click opens or shuts it, and the
//! pane keeps `Kept::open` in step, reading an opened tool call's detail; on each frame its height changes the
//! timeline measures its row again, so the rows below slide with it.

use gpui_kit::*;

use super::SessionPane;
use crate::core::ui::expand_row::{ExpandRow, ExpandRowEvent};
use crate::features::session::model::entries::entry_key;
use crate::features::session::model::fold::denied;
use crate::features::session::model::item::{Item, ItemKind};
use crate::features::session::view::ask_row::AskRow;
use crate::features::session::view::grow::band;
use crate::features::session::view::item_row::{thinking_body, thinking_title};
use crate::features::session::view::tool_row::ToolRow;

type Title = Box<dyn Fn(f32, &mut Window, &mut App) -> AnyElement>;
type Body = Box<dyn Fn(&mut Window, &mut App) -> AnyElement>;

impl SessionPane {
    /// What a tool call, thinking or a question draws: its `ExpandRow`, with its title and body as they are now. A tool
    /// call with nothing to open draws its line alone, and `took_ms` is its time in the activity card. `None` for
    /// other items.
    pub(super) fn expand_of(
        &mut self,
        item: &Item,
        took_ms: Option<i64>,
        cx: &mut Context<Self>,
    ) -> Option<AnyElement> {
        let (open, title, body): (bool, Title, Body) = match &item.kind {
            ItemKind::Tool { item_id, tool } => {
                let row = ToolRow {
                    key: item.key.clone(),
                    tool: tool.clone(),
                    kept: self.kept.get(&item.key).cloned().unwrap_or_default(),
                    took_ms,
                    denied: denied(&self.view.items).contains(item_id.as_str()),
                    on_action: self.on_action.clone(),
                };
                if !tool.has_detail {
                    return Some(row.line(None, cx).into_any_element());
                }
                let body = row.clone();
                (
                    false,
                    Box::new(move |grown, _, cx| row.line(Some(grown), cx).into_any_element()),
                    Box::new(move |_, cx| body.body(cx)),
                )
            }
            ItemKind::Thinking { text, done } => {
                let (text, done) = (SharedString::from(text.clone()), *done);
                (
                    false,
                    Box::new(move |grown, _, cx| thinking_title(done, grown, cx)),
                    Box::new(move |_, cx| thinking_body(text.clone(), cx)),
                )
            }
            ItemKind::Ask { ask_id, ask } => {
                let row = AskRow {
                    ask_id: ask_id.clone(),
                    ask: ask.clone(),
                    form: self.forms.get(ask_id).cloned(),
                    on_action: self.on_action.clone(),
                };
                let body = row.clone();
                (
                    ask.state == "open",
                    Box::new(move |grown, _, cx| row.head(grown, cx).into_any_element()),
                    Box::new(move |_, cx| body.controls(cx).into_any_element()),
                )
            }
            _ => return None,
        };
        let row = match self.expands.get(&item.key) {
            Some((row, _)) => {
                row.update(cx, |row, _| {
                    row.set_title(title);
                    row.set_body(body);
                });
                row.clone()
            }
            None => {
                // Only a question opens as a card; a tool call's or thinking's body hangs from its own rule.
                let ask = matches!(item.kind, ItemKind::Ask { .. });
                let lit = move |row, grown, cx: &App| band(row, if ask { grown } else { 0. }, cx);
                let row = cx.new(|_| ExpandRow::new(open, title, body).band(lit));
                let key = item.key.clone();
                let heard = cx.subscribe(&row, move |pane, _, event, cx| {
                    pane.on_expand(&key, event, cx)
                });
                self.expands.insert(item.key.clone(), (row.clone(), heard));
                row
            }
        };
        // A question answered since it was last drawn open shrinks shut, whether the answer came live or on a page
        // loaded after a reconnect; one opened again by hand after that stays open.
        if let ItemKind::Ask { ask, .. } = &item.kind {
            if ask.state == "open" {
                self.open_asks.insert(item.key.clone());
            } else if self.open_asks.remove(&item.key) {
                row.update(cx, |row, cx| row.set_open(false, true, cx));
            }
        }
        Some(row.into_any_element())
    }

    /// A click opens or shuts the row, or its height changed on this frame.
    fn on_expand(&mut self, key: &str, event: &ExpandRowEvent, cx: &mut Context<Self>) {
        let at = self.view.items.iter().position(|item| item.key == key);
        match event {
            ExpandRowEvent::Height(_) => {
                // A step in the activity card grows inside the card's row.
                let row = at.map_or(key.to_string(), |at| {
                    entry_key(&self.view.items[at], self.live_run())
                });
                self.timeline
                    .update(cx, |timeline, cx| timeline.redraw_row(&row, cx));
            }
            ExpandRowEvent::Opened | ExpandRowEvent::Closed => {
                let open = *event == ExpandRowEvent::Opened;
                self.kept.entry(key.to_string()).or_default().open = open;
                if let (true, Some(at)) = (open, at) {
                    self.read_detail_if_stale(at, cx);
                }
            }
        }
    }
}
