//! Which of the session's checkouts the pane reads. Only a successful `git/checkouts` answer offers another one, and
//! it's only ever read: writes and opening a file stay with the session's own folder, and the way to work there is to
//! make it the session's checkout through the Engine. The read drops the choice with its session; the hint, saved with
//! the layout, is what brings it back, and only after a fresh list still offers it.
use super::{PaneResult, SourceControlPane};
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::state::sessions::Sessions;
use crate::features::source_control::actions_menu::STILL_RUNNING;
use crate::features::source_control::git_checkouts::{CheckoutHint, GitCheckouts, checkout_gone};
use crate::features::source_control::git_failure::GitFailure;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use gpui_kit::component::button::Button;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, v_flex};
use gpui_kit::*;
use serde_json::json;

const SCOPE_TIP: &str = "Folder this panel reads";
const CHECKOUT_GONE: &str =
    "That checkout can't be read any more, so this panel went back to the session folder.";

impl SourceControlPane {
    pub(super) fn selection(&self, cx: &App) -> Option<CheckoutSelector> {
        self.read.read(cx).checkout().cloned()
    }
    pub(super) fn read_only(&self, cx: &App) -> bool {
        self.read.read(cx).checkout().is_some()
    }
    /// After the status read changes: the list follows its session and source, and is read again with each answer.
    pub(super) fn sync_checkouts(&mut self, cx: &mut Context<Self>) {
        let read = self.read.read(cx);
        let now = read.session().map(String::from).zip(read.source().cloned());
        let answers = read.answers();
        let gone = read.checkout().is_some() && read.failure_code().is_some_and(checkout_gone);
        if self.checkouts_for != now {
            self.checkouts_for = now;
            self.checkouts = None;
            self.checkouts_read = None;
            self.scope_open = false;
        }
        if gone {
            self.leave_checkout(cx);
        } else if answers != self.checkouts_answers {
            self.checkouts_answers = answers;
            self.read_checkouts(cx);
        }
    }
    pub(super) fn read_checkouts(&mut self, cx: &mut Context<Self>) {
        let (Some(asked), Some(socket)) =
            (self.checkouts_for.clone(), SocketClient::try_global(cx))
        else {
            return;
        };
        let epoch = self.workspace_epoch;
        let key = format!("git/checkouts/{}", cx.entity_id());
        let call = socket.read(cx).call::<GitCheckouts>(
            "git",
            "checkouts",
            json!({"session":asked.0,"expectedSource":asked.1}),
            Some(&key),
        );
        self.checkouts_read = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                if pane.workspace_epoch == epoch && pane.checkouts_for.as_ref() == Some(&asked) {
                    pane.checkouts_read = None;
                    pane.checkouts_answered(answer, cx);
                }
            });
        }));
    }
    /// Anything but a successful answer offers nothing, which is how an older owner never gets a selector.
    fn checkouts_answered(&mut self, answer: Outcome<GitCheckouts>, cx: &mut Context<Self>) {
        self.checkouts = match answer {
            Outcome::Ok(checkouts) => Some(checkouts),
            Outcome::Failed(_) => None,
            // A dropped connection says nothing about the checkouts; the next answer decides.
            Outcome::Aborted => return,
        };
        self.settle_hint(cx);
        if let Some(selection) = self.selection(cx)
            && !self
                .checkouts
                .as_ref()
                .is_some_and(|checkouts| checkouts.offers(&selection))
        {
            self.leave_checkout(cx);
        }
        self.settle_place(cx);
        cx.notify();
    }
    /// A hint for this session becomes a read if this answer offers it, and otherwise is dropped and said so. A hint for
    /// another session waits for that session.
    fn settle_hint(&mut self, cx: &mut Context<Self>) {
        let (Some(hint), Some((session, source))) =
            (self.checkout_hint.clone(), self.checkouts_for.clone())
        else {
            return;
        };
        if hint.session != session
            || self.selection(cx).as_ref() == Some(&hint.selector())
            || self.busy(cx)
        {
            return;
        }
        match hint.qualified(&session, &source, self.checkouts.as_ref()) {
            Some(selector) => self
                .read
                .update(cx, |read, cx| read.set_checkout(Some(selector), cx)),
            None => self.leave_checkout(cx),
        }
    }
    fn leave_checkout(&mut self, cx: &mut Context<Self>) {
        self.result = Some(PaneResult::Failed(GitFailure::plain(CHECKOUT_GONE)));
        self.details_open = false;
        self.scope_open = false;
        self.back_to_session_folder(cx);
    }
    /// Forgets the choice as well as the read, so it doesn't come back with the next list.
    pub(super) fn back_to_session_folder(&mut self, cx: &mut Context<Self>) {
        self.checkout_hint = None;
        self.read.update(cx, |read, cx| read.set_checkout(None, cx));
    }
    /// `None` is the session's own folder.
    pub(super) fn pick_checkout(&mut self, path: Option<String>, cx: &mut Context<Self>) {
        self.scope_open = false;
        cx.notify();
        if self.busy(cx) {
            return;
        }
        let selector = match path {
            None => None,
            Some(path) => match self
                .checkouts
                .as_ref()
                .and_then(|checkouts| checkouts.selector(&path))
            {
                Some(selector) => Some(selector),
                None => return,
            },
        };
        let here = self.checkouts_for.clone();
        self.checkout_hint = match (&selector, &here) {
            (Some(selector), Some((session, source))) => {
                Some(CheckoutHint::new(session, source, selector))
            }
            _ => self.checkout_hint.take().filter(|hint| {
                here.as_ref()
                    .is_some_and(|(session, _)| hint.session != *session)
            }),
        };
        WorkspaceCoordinator::changed(cx);
        if selector == self.selection(cx) {
            return;
        }
        self.result = None;
        self.hovered_section = None;
        self.hovered_file = None;
        self.selected_file = None;
        self.read
            .update(cx, |read, cx| read.set_checkout(selector, cx));
    }
    fn checkout_hint(&self, cx: &App) -> (String, bool) {
        if self.checkout.is_some() {
            return ("Changing the session checkout…".into(), false);
        }
        if self.busy(cx) {
            return (STILL_RUNNING.into(), false);
        }
        let session = self.read.read(cx).session();
        let running = Sessions::try_global(cx).is_some_and(|sessions| {
            let sessions = sessions.read(cx);
            sessions
                .items()
                .iter()
                .any(|item| Some(item.session_id.as_str()) == session && item.state == "running")
        });
        if running {
            return (
                "Finish the active turn before changing checkout.".into(),
                false,
            );
        }
        (
            "Use this linked worktree as the session checkout.".into(),
            true,
        )
    }
    /// The picker, offered once there's somewhere else to look.
    pub(super) fn scope_picker(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let choices = self
            .checkouts
            .as_ref()
            .filter(|list| list.has_others())?
            .scope_choices();
        let current = self.selection(cx).map(|selection| selection.path);
        let label = choices
            .iter()
            .find(|choice| choice.path == current)
            .map_or_else(
                || "Session folder".to_string(),
                |choice| choice.label.clone(),
            );
        let pane = cx.entity().downgrade();
        Some(
            div()
                .debug_selector(|| "git-scope".into())
                .child(
                    Popover::new("git-scope")
                        .anchor(Anchor::TopLeft)
                        .p_0()
                        .open(self.scope_open)
                        .on_open_change(cx.listener(|pane, open: &bool, _, cx| {
                            pane.scope_open = *open;
                            cx.notify();
                        }))
                        .trigger_style(StyleRefinement::default().w_full())
                        .trigger(
                            Button::new("git-scope-trigger")
                                .outline()
                                .small()
                                .w_full()
                                .dropdown_caret(true)
                                .label(label)
                                .tooltip(SCOPE_TIP)
                                .disabled(self.busy(cx)),
                        )
                        .content(move |_, _, _| {
                            v_flex()
                                .min_w(px(240.))
                                .p_1()
                                .children(choices.iter().enumerate().map(|(at, choice)| {
                                    let (pane, path) = (pane.clone(), choice.path.clone());
                                    ListItem::new(SharedString::from(format!(
                                        "git-scope-choice-{at}"
                                    )))
                                    .selected(choice.path == current)
                                    .on_click(move |_, _, cx| {
                                        _ = pane.update(cx, |pane, cx| {
                                            pane.pick_checkout(path.clone(), cx)
                                        });
                                    })
                                    .child(choice.label.clone())
                                }))
                        }),
                )
                .into_any_element(),
        )
    }
    /// The picker on its own, above why there's nothing else to show yet.
    pub(super) fn scope_row(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        self.scope_picker(cx)
            .map(|picker| div().px_2().pb_2().child(picker).into_any_element())
    }
    /// What stands where the commit box was while another checkout is shown.
    pub(super) fn read_only_block(
        &self,
        selection: &CheckoutSelector,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let (hint, can) = self.checkout_hint(cx);
        let folder = selection.path.clone();
        v_flex()
            .gap_2()
            .child(
                div()
                    .text_sm()
                    .text_color(muted)
                    .child(format!("Viewing {} (read-only)", selection.path)),
            )
            .child(
                div()
                    .debug_selector(|| "git-use-session-checkout".into())
                    .child(
                        Button::new("git-use-session-checkout")
                            .outline()
                            .small()
                            .w_full()
                            .label(if self.checkout.is_some() {
                                "Changing checkout…"
                            } else {
                                "Use as session checkout"
                            })
                            .tooltip(hint.clone())
                            .disabled(!can)
                            .on_click(cx.listener(move |pane, _, _, cx| {
                                pane.move_to_folder(folder.clone(), cx)
                            })),
                    ),
            )
            .child(div().text_sm().text_color(muted).child(hint))
            .into_any_element()
    }
}
