//! The entry bar above the tree: New file, New folder and Rename name things here, as in the old panel. Enter or
//! leaving the field commits, Escape cancels.
use super::FilesPane;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::files as ids;
use crate::features::files::file_entry_bar::{
    EntryBar, EntryKind, EntryOutcome, entry_outcome, leaf,
};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, v_flex};
use gpui_kit::*;

pub(in crate::features::files) struct OpenEntry {
    pub(in crate::features::files) bar: EntryBar,
    pub(in crate::features::files) input: Entity<InputState>,
    pub(in crate::features::files) error: Option<String>,
    _heard: Subscription,
}

impl FilesPane {
    /// Opens the bar for something new in `target` (a folder, "" for the root) or to rename `target`. New in a closed
    /// folder opens it first, so what's made shows where it's made.
    pub(in crate::features::files) fn start_entry(
        &mut self,
        kind: EntryKind,
        target: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(reason) = self.read_only_reason(cx) {
            self.action_error = Some(reason);
            cx.notify();
            return;
        }
        let bar = match kind {
            EntryKind::Rename => EntryBar::rename(target),
            _ => EntryBar::new_in(kind, target),
        };
        if kind != EntryKind::Rename && self.tree.entry_state(target) == Some((true, false)) {
            self.clicked(target.to_string(), true, cx);
        }
        let input = cx.new(|cx| {
            let mut input = InputState::new(window, cx).placeholder(bar.placeholder());
            input.set_value(bar.initial().to_string(), window, cx);
            input
        });
        input.update(cx, |input, cx| {
            input.focus(window, cx);
            input.select_all(window, cx);
        });
        let heard = cx.subscribe_in(
            &input,
            window,
            |pane, input, event: &InputEvent, window, cx| {
                if matches!(event, InputEvent::PressEnter { .. } | InputEvent::Blur) {
                    pane.commit_entry(input, window, cx);
                }
            },
        );
        self.action_error = None;
        self.entry = Some(OpenEntry {
            bar,
            input,
            error: None,
            _heard: heard,
        });
        cx.notify();
    }

    /// Runs what the bar names. A name already shown in its folder keeps the bar open and says so.
    pub(in crate::features::files) fn commit_entry(
        &mut self,
        from: &Entity<InputState>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(entry) = &self.entry else {
            return;
        };
        if &entry.input != from || self.action_running() {
            return;
        }
        let typed = from.read(cx).value().to_string();
        match entry_outcome(&entry.bar, &typed) {
            EntryOutcome::Dropped | EntryOutcome::Unchanged => self.close_entry(window, cx),
            EntryOutcome::Invalid(reason) => self.entry_failed(reason.to_string()),
            EntryOutcome::Run {
                kind,
                path,
                destination,
            } => {
                let named = destination.clone().unwrap_or_else(|| path.clone());
                if self.tree.entry_state(&named).is_some() {
                    self.entry_failed(format!(
                        "Something is already called {} here.",
                        leaf(&named)
                    ));
                } else {
                    self.run_action(kind, path, destination, None, window, cx);
                }
            }
        }
        cx.notify();
    }

    pub(in crate::features::files) fn close_entry(
        &mut self,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.entry.take().is_some() {
            window.focus(&self.focus, cx);
            cx.notify();
        }
    }

    pub(super) fn entry_failed(&mut self, error: String) {
        match &mut self.entry {
            Some(entry) => entry.error = Some(error),
            None => self.action_error = Some(error),
        }
    }

    pub(super) fn entry_view(&self, cx: &mut Context<Self>) -> Option<Div> {
        let entry = self.entry.as_ref()?;
        let danger = cx.theme().danger;
        Some(
            v_flex()
                .flex_none()
                .px_2()
                .pb_1()
                .gap_0p5()
                .capture_key_down(cx.listener(|pane, event: &KeyDownEvent, window, cx| {
                    if event.keystroke.key == "escape" {
                        pane.close_entry(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(
                    Input::new(&entry.input)
                        .control(ids::ENTRY, entry.bar.placeholder())
                        .small(),
                )
                .children(entry.error.clone().map(|error| {
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(danger)
                        .child(error)
                })),
        )
    }
}
