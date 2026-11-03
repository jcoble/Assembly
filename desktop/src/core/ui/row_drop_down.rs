//! One open row per list. ExpandRow remains the sole owner of motion and body layout.
use std::collections::HashMap;
use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon};
use gpui_kit::*;
use super::expand_row::{ExpandRow, ExpandRowEvent};

#[derive(Clone, Debug, PartialEq)]
pub enum RowDropDownEvent {
    Opened(SharedString),
    Shut(SharedString),
    Height(SharedString, Pixels),
}
struct HeldRow { row: Entity<ExpandRow>, _heard: Subscription }
#[derive(Default)]
pub struct RowDropDowns {
    open: Option<SharedString>,
    rows: HashMap<SharedString, HeldRow>,
}
impl EventEmitter<RowDropDownEvent> for RowDropDowns {}

impl RowDropDowns {
    pub fn open_key(&self) -> Option<&SharedString> { self.open.as_ref() }
    pub fn is_open(&self, key: &SharedString) -> bool { self.open.as_ref() == Some(key) }
    /// Also returns a closing row until its existing grow motion has finished.
    pub fn expand_row(&self, key: &SharedString) -> Option<Entity<ExpandRow>> {
        self.rows.get(key).map(|held| held.row.clone())
    }
    pub fn toggle(
        &mut self, key: SharedString,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
        cx: &mut Context<Self>,
    ) {
        if self.is_open(&key) { self.shut(cx); return; }
        self.shut(cx);
        let body = move |window: &mut Window, cx: &mut App| {
            let theme = cx.theme();
            div().mt(px(8.)).rounded(px(10.)).border_1().border_color(theme.border)
                .bg(theme.background).px(px(8.)).py(px(6.)).child(body(window, cx)).into_any_element()
        };
        if let Some(held) = self.rows.get(&key) {
            // Reopening during a close reverses the same ExpandRow motion.
            held.row.update(cx, |row, cx| {
                row.set_body(body);
                row.set_open(true, true, cx);
            });
        } else {
            let row = cx.new(|_| ExpandRow::new(false, |_, _, _| Empty.into_any_element(), body)
                .toggle_on_title(false).band(|row, _, _| row));
            let changed_key = key.clone();
            let heard = cx.subscribe(&row, move |drops, _, event, cx| {
                if let ExpandRowEvent::Height(height) = event {
                    cx.emit(RowDropDownEvent::Height(changed_key.clone(), *height));
                    if *height == px(0.) && !drops.is_open(&changed_key) {
                        drops.rows.remove(&changed_key);
                    }
                    cx.notify();
                }
            });
            row.update(cx, |row, cx| row.set_open(true, true, cx));
            self.rows.insert(key.clone(), HeldRow { row, _heard:heard });
        }
        self.open = Some(key.clone());
        cx.emit(RowDropDownEvent::Opened(key));
        cx.notify();
    }
    /// Virtual lists must release bodies removed by filtering or paging, since those rows
    /// no longer render the closing motion that would normally release their resources.
    pub fn retain_keys(&mut self, mut keep: impl FnMut(&SharedString) -> bool, cx: &mut Context<Self>) {
        let removed = self.open.as_ref().filter(|key| !keep(key)).cloned();
        let before=self.rows.len();
        self.rows.retain(|key, _| keep(key));
        let changed=removed.is_some()||before!=self.rows.len();
        if let Some(key) = removed {
            self.open = None;
            cx.emit(RowDropDownEvent::Shut(key));
        }
        if changed{cx.notify();}
    }
    pub fn shut(&mut self, cx: &mut Context<Self>) {
        let Some(key) = self.open.take() else { return; };
        if let Some(row) = self.expand_row(&key) {
            row.update(cx, |row, cx| row.set_open(false, true, cx));
            // No measured body means there is no visible close motion to retain.
            if row.read(cx).drawn_height() == px(0.) { self.rows.remove(&key); }
        }
        cx.emit(RowDropDownEvent::Shut(key));
        cx.notify();
    }
    /// The owner supplies the focused row and its actual body; unrelated keys stay theirs.
    pub fn handle_key(
        &mut self, key: SharedString, code: &str,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
        cx: &mut Context<Self>,
    ) -> bool {
        match code {
            "right" => { if !self.is_open(&key) { self.toggle(key, body, cx); } true }
            "left" => { if self.is_open(&key) { self.shut(cx); } true }
            "escape" => { self.shut(cx); true }
            _ => false,
        }
    }
}

/// The owner places this mark in its explicit toggle button and handles that button's click.
pub fn chevron(key: SharedString, drops: Entity<RowDropDowns>) -> Chevron { Chevron { key, drops } }
#[derive(IntoElement)]
pub struct Chevron { key: SharedString, drops: Entity<RowDropDowns> }
impl RenderOnce for Chevron {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        Icon::new(if self.drops.read(cx).is_open(&self.key) { IconName::ChevronDown } else { IconName::ChevronRight })
            .size(px(14.)).text_color(cx.theme().muted_foreground)
    }
}
