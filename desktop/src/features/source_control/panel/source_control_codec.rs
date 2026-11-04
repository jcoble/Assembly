//! Source control's view in a workspace snapshot: which sections are open, the place in the scroller and the checkout
//! last picked. All of it is metadata. A saved checkout reads nothing until a fresh `git/checkouts` still offers it, and
//! a saved place waits for a new status answer proven to be this workspace's own.
use super::SourceControlPane;
use crate::core::state::session_read::Shown;
use crate::core::state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes};
use crate::features::source_control::git_checkouts::CheckoutHint;
use crate::features::source_control::git_status::Section;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::HashSet;

const INVALID: &str = "The saved Source control view is invalid.";

pub struct SourceControlCodec;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Stored {
    open_sections: Vec<String>,
    scroll_top: f64,
    checkout_hint: Option<CheckoutHint>,
}
/// One workspace's view. A missing or null value is this type's default, never what another workspace had open.
#[derive(Clone, Debug, Default, PartialEq)]
pub(super) struct SavedView {
    pub(super) open_sections: HashSet<Section>,
    pub(super) scroll_top: f32,
    pub(super) checkout_hint: Option<CheckoutHint>,
}
impl SavedView {
    pub(super) fn parse(value: &Value, key: &WorkspaceKey) -> Result<Self, String> {
        if value.is_null() {
            return Ok(Self::default());
        }
        let stored: Stored = serde_json::from_value(value.clone()).map_err(|_| INVALID)?;
        let mut open_sections = HashSet::new();
        for name in &stored.open_sections {
            let section = Section::ALL
                .into_iter()
                .find(|section| section.label() == name);
            if !section.is_some_and(|section| open_sections.insert(section)) {
                return Err(INVALID.into());
            }
        }
        let scroll_top = stored.scroll_top as f32;
        if !scroll_top.is_finite() || scroll_top < 0. {
            return Err(INVALID.into());
        }
        if let Some(hint) = &stored.checkout_hint
            && !hint.belongs_to(key, &hint.source)
        {
            return Err("A saved checkout hint contradicts its pane or workspace.".into());
        }
        Ok(Self {
            open_sections,
            scroll_top,
            checkout_hint: stored.checkout_hint,
        })
    }
    fn to_value(&self) -> Value {
        let open: Vec<_> = Section::ALL
            .into_iter()
            .filter(|section| self.open_sections.contains(section))
            .map(Section::label)
            .collect();
        json!({"openSections":open,"scrollTop":self.scroll_top,"checkoutHint":self.checkout_hint})
    }
}
/// A saved place, and how many status answers had come when it was restored.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct PendingPlace {
    pub(super) top: f32,
    after_answers: u64,
}
impl PaneCodec for SourceControlCodec {
    fn name(&self) -> &'static str {
        "sourceControl"
    }
    fn capture(&self, _: &Window, cx: &App) -> Result<Value, String> {
        let (Some(key), Some((_, _, pane))) =
            (WorkspacePanes::key(cx), SourceControlPane::located(cx))
        else {
            return Ok(Value::Null);
        };
        Ok(pane.read(cx).saved_view(key))
    }
    fn validate(
        &self,
        value: &Value,
        key: &WorkspaceKey,
        _: &Value,
        _: &App,
    ) -> Result<(), String> {
        SavedView::parse(value, key).map(|_| ())
    }
    fn restore(&self, value: &Value, _: &mut Window, cx: &mut App) {
        let (Some(key), Some((_, _, pane))) = (
            WorkspacePanes::key(cx).cloned(),
            SourceControlPane::located(cx),
        ) else {
            return;
        };
        match SavedView::parse(value, &key) {
            Ok(view) => pane.update(cx, |pane, cx| pane.bind_workspace(Some(key), view, cx)),
            Err(error) => WorkspacePanes::refuse_restore(error, cx),
        }
    }
    fn deactivate(&self, _: &mut Window, cx: &mut App) {
        if let Some((_, _, pane)) = SourceControlPane::located(cx) {
            pane.update(cx, |pane, cx| {
                pane.bind_workspace(None, SavedView::default(), cx)
            });
        }
    }
}
impl SourceControlPane {
    pub fn register_workspace(cx: &mut App) -> Result<(), String> {
        WorkspacePanes::register(Box::new(SourceControlCodec), cx)
    }
    /// What this workspace would save. Null unless the pane is bound to it, so a pane still on the last workspace never
    /// speaks for the next, and a hint that isn't this workspace's stays out rather than refusing the whole save.
    pub(super) fn saved_view(&self, key: &WorkspaceKey) -> Value {
        if self.workspace.as_ref() != Some(key) {
            return Value::Null;
        }
        SavedView {
            open_sections: self.open_sections.clone(),
            scroll_top: self.pending_place.map_or_else(
                || f32::from(-self.scroll.offset().y).max(0.),
                |place| place.top,
            ),
            checkout_hint: self
                .checkout_hint
                .clone()
                .filter(|hint| hint.belongs_to(key, &hint.source)),
        }
        .to_value()
    }
    /// Leaves one workspace for the next. The last status read stops before anything is rebound and what's shown stays
    /// unproven: a shown pane reads again now and a hidden one when it's next shown.
    pub(super) fn bind_workspace(
        &mut self,
        workspace: Option<WorkspaceKey>,
        view: SavedView,
        cx: &mut Context<Self>,
    ) {
        self.read.update(cx, |read, cx| {
            read.set_active(false, cx);
            read.set_checkout(None, cx);
        });
        self.checkouts = None;
        self.checkouts_for = None;
        self.checkouts_read = None;
        self.reveal_read = None;
        self.scope_open = false;
        self.result = None;
        self.details_open = false;
        self.hovered_section = None;
        self.hovered_file = None;
        self.selected_file = None;
        self.workspace = workspace;
        self.workspace_epoch += 1;
        self.checkout_hint = view.checkout_hint;
        self.open_sections = view.open_sections;
        self.scroll.set_offset(Point::default());
        let after_answers = self.read.read(cx).answers();
        self.pending_place = (view.scroll_top > 0.).then_some(PendingPlace {
            top: view.scroll_top,
            after_answers,
        });
        if self.active {
            self.read.update(cx, |read, cx| read.set_active(true, cx));
        }
        cx.notify();
    }
    /// Brings the saved place back once a status answered since the restore is proven to be this workspace's: from its
    /// own root, or from a linked checkout it offers. A saved checkout still being settled keeps waiting for its answer.
    pub(super) fn settle_place(&mut self, cx: &mut Context<Self>) {
        let (Some(place), Some(workspace)) = (self.pending_place, self.workspace.as_ref()) else {
            return;
        };
        let read = self.read.read(cx);
        let Shown::Read(status) = read.shown() else {
            return;
        };
        let root = status.checkout_root.as_deref();
        let proven = match read.checkout() {
            None => root == Some(workspace.checkout_root.as_str()),
            Some(selector) => {
                root == Some(selector.path.as_str())
                    && selector.expected_root == workspace.checkout_root
            }
        };
        let settled = self
            .checkout_hint
            .as_ref()
            .is_none_or(|hint| read.checkout() == Some(&hint.selector()));
        if read.answers() > place.after_answers
            && read.session() == Some(workspace.session_id.as_str())
            && read
                .source()
                .is_some_and(|source| source.machine == workspace.machine)
            && proven
            && settled
        {
            self.pending_place = None;
            self.scroll.set_offset(point(px(0.), px(-place.top)));
            cx.notify();
        }
    }
}

#[cfg(test)]
#[path = "codec_tests/source_control_codec_tests.rs"]
mod tests;
