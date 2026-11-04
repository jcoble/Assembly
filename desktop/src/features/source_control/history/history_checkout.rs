//! Which of the session's checkouts this tab reads. Only a successful `git/checkouts` answer offers another one, and the
//! tab's choice moves nothing in the session or the panel. Its hint is saved with the layout, or arrives from the panel
//! that opened this tab, and becomes a read only once a fresh answer still offers it.
use super::HistoryPane;
use crate::core::socket::{checkout_selector::CheckoutSelector, client::SocketClient, outcome::Outcome};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::source_control::git_checkouts::{CheckoutChoice, CheckoutHint, GitCheckouts};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{Disableable as _, Sizable as _};
use gpui_kit::*;
use serde_json::json;

pub(super) const CHECKOUT_GONE: &str = "That checkout can't be read any more, so this went back to the session folder.";

impl HistoryPane {
    /// A selector needs the source in its own arguments, so a session with none is asked for nothing.
    pub(super) fn read_checkouts(&mut self, cx: &mut Context<Self>) {
        let (Some(source), Some(socket)) = (self.expected_source.clone(), SocketClient::try_global(cx)) else {
            return;
        };
        let call = socket.read(cx).call::<GitCheckouts>(
            "git",
            "checkouts",
            json!({"session":self.session,"expectedSource":source}),
            Some(&format!("git/checkouts/{}", cx.entity_id())),
        );
        self.checkouts_read = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.checkouts_read = None;
                pane.checkouts = match answer {
                    Outcome::Ok(checkouts) => Some(checkouts),
                    Outcome::Failed(_) => None,
                    // A dropped connection says nothing about the checkouts; the next answer decides.
                    Outcome::Aborted => return,
                };
                pane.settle_hint(cx);
                if let Some(checkout) = pane.checkout.clone()
                    && !pane.checkouts.as_ref().is_some_and(|checkouts| checkouts.offers(&checkout))
                {
                    pane.leave_checkout(cx);
                }
                cx.notify();
            });
        }));
    }
    /// The hint in the layout this tab was built from. With no source there's no list to check it against.
    pub(super) fn restore_hint(&mut self, hint: Option<CheckoutHint>, cx: &mut Context<Self>) {
        self.checkout_hint = hint;
        if self.checkout_hint.is_some() && self.expected_source.is_none() {
            self.leave_checkout(cx);
        }
    }
    /// What the panel that opened this tab was reading.
    pub(super) fn ask_for(&mut self, selector: Option<CheckoutSelector>, cx: &mut Context<Self>) {
        let Some(selector) = selector else {
            return;
        };
        self.notice = None;
        match self.expected_source.clone() {
            Some(source) => {
                self.checkout_hint = Some(CheckoutHint::new(&self.session, &source, &selector));
                CenterPanes::save_layout(cx);
            }
            None => self.leave_checkout(cx),
        }
    }
    fn settle_hint(&mut self, cx: &mut Context<Self>) {
        let (Some(hint), Some(source)) = (self.checkout_hint.clone(), self.expected_source.clone()) else {
            return;
        };
        if self.checkout.as_ref() == Some(&hint.selector()) {
            return;
        }
        match hint.qualified(&self.session, &source, self.checkouts.as_ref()) {
            Some(selector) => self.set_checkout(Some(selector), cx),
            None => self.leave_checkout(cx),
        }
    }
    /// `None` is the session's own folder.
    pub(super) fn pick_checkout(&mut self, path: Option<String>, cx: &mut Context<Self>) {
        let checkout = match path {
            None => None,
            Some(path) => match self.checkouts.as_ref().and_then(|checkouts| checkouts.selector(&path)) {
                Some(checkout) => Some(checkout),
                None => return,
            },
        };
        self.notice = None;
        self.checkout_hint = checkout
            .as_ref()
            .zip(self.expected_source.as_ref())
            .map(|(checkout, source)| CheckoutHint::new(&self.session, source, checkout));
        CenterPanes::save_layout(cx);
        if checkout != self.checkout {
            self.set_checkout(checkout, cx);
        }
        cx.notify();
    }
    pub(super) fn leave_checkout(&mut self, cx: &mut Context<Self>) {
        self.notice = Some(CHECKOUT_GONE.into());
        self.checkout_hint = None;
        CenterPanes::save_layout(cx);
        if self.checkout.is_some() {
            self.set_checkout(None, cx);
        }
    }
    /// What was shown belongs to the last checkout, and another checkout's head isn't the head moving.
    fn set_checkout(&mut self, checkout: Option<CheckoutSelector>, cx: &mut Context<Self>) {
        self.checkout = checkout.clone();
        self.status = None;
        self.timeline.update(cx, |timeline, cx| timeline.set_checkout(checkout, cx));
        self.read_status(cx);
        cx.notify();
    }
}
/// The folder this tab reads. With nowhere else to look it only names the folder.
pub(super) fn repository_picker(
    label: String,
    choices: Option<Vec<CheckoutChoice>>,
    current: Option<String>,
    pane: WeakEntity<HistoryPane>,
) -> AnyElement {
    let trigger = Button::new("history-folder").outline().small();
    let Some(choices) = choices else {
        return trigger.label(label).disabled(true).into_any_element();
    };
    div()
        .debug_selector(|| "history-repository".into())
        .min_w(px(150.))
        .max_w(px(260.))
        .child(
            trigger
                .dropdown_caret(true)
                .label(label)
                .tooltip("Which repository's history to show")
                .dropdown_menu(move |menu, _, _| {
                    choices.iter().cloned().fold(menu, |menu, CheckoutChoice { path, label }| {
                        let pane = pane.clone();
                        let checked = current == path;
                        menu.item(PopupMenuItem::new(label).checked(checked).on_click(move |_, _, cx| {
                            _ = pane.update(cx, |pane, cx| pane.pick_checkout(path.clone(), cx));
                        }))
                    })
                }),
        )
        .into_any_element()
}
