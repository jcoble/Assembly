//! The header's branch menu: switch branch, new branch, and stash. It draws what the pane read and says what was picked;
//! the pane owns the read and the write, so nothing here reaches the backend.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::git_branches::GitBranch;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq)]
pub enum BranchPicked {
    /// A branch with nothing holding it: this checkout switches to it.
    Switch(String),
    /// A branch another worktree holds: the session moves to that folder.
    MoveToFolder(String),
    Create(String),
    Stash,
    OpenStashes,
}
pub fn choice_for(branch: &GitBranch) -> Option<BranchPicked> {
    if branch.current {
        return None;
    }
    Some(match &branch.worktree_folder {
        Some(folder) => BranchPicked::MoveToFolder(folder.clone()),
        None => BranchPicked::Switch(branch.name.clone()),
    })
}
/// Why git would refuse this name, in plain words; `None` while it's empty or fine.
pub fn branch_name_problem(name: &str, branches: &[GitBranch]) -> Option<String> {
    let name = name.trim();
    if name.is_empty() {
        return None;
    }
    if name.starts_with('-') {
        return Some("A branch name cannot start with a dash.".into());
    }
    if name.len() > 200 {
        return Some("That branch name is too long.".into());
    }
    if name.chars().any(char::is_whitespace) {
        return Some("Branch names cannot contain spaces.".into());
    }
    if let Some(bad) = name.chars().find(|c| "~^:?*[\\".contains(*c)) {
        return Some(format!("Branch names cannot contain {bad}"));
    }
    if name.contains("..") {
        return Some("Branch names cannot contain two dots in a row.".into());
    }
    if name.starts_with('/') || name.ends_with('/') {
        return Some("Branch names cannot start or end with a slash.".into());
    }
    if name.ends_with(".lock") {
        return Some("Branch names cannot end with .lock".into());
    }
    branches
        .iter()
        .any(|branch| branch.name == name)
        .then(|| format!("{name} already exists."))
}
pub fn filter_branches<'a>(branches: &'a [GitBranch], filter: &str) -> Vec<&'a GitBranch> {
    let filter = filter.trim().to_lowercase();
    branches
        .iter()
        .filter(|branch| filter.is_empty() || branch.name.to_lowercase().contains(&filter))
        .collect()
}
/// The muted line under a branch's name.
pub fn branch_detail(branch: &GitBranch) -> String {
    let mut parts = vec![match &branch.upstream {
        Some(upstream) => format!("tracks {upstream}"),
        None => "no remote branch".to_string(),
    }];
    if let Some(folder) = &branch.worktree_folder {
        parts.push(format!("held in {folder}"));
    }
    if !branch.subject.is_empty() {
        parts.push(branch.subject.clone());
    }
    parts.join(" · ")
}
pub struct BranchMenu {
    filter: Entity<InputState>,
    name: Entity<InputState>,
    branches: Vec<GitBranch>,
    reading: bool,
    failure: Option<String>,
    /// The refusal the last create came back with.
    refused: Option<String>,
    busy: bool,
    has_changes: bool,
    _heard: Vec<Subscription>,
}
impl EventEmitter<BranchPicked> for BranchMenu {}
impl BranchMenu {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let filter = cx.new(|cx| InputState::new(window, cx).placeholder("Filter branches"));
        let name = cx.new(|cx| InputState::new(window, cx).placeholder("feature/my-change"));
        let heard = vec![
            cx.subscribe(&filter, |_: &mut Self, _, _: &InputEvent, cx| cx.notify()),
            cx.subscribe(&name, |menu: &mut Self, _, event: &InputEvent, cx| {
                match event {
                    InputEvent::Change => menu.refused = None,
                    InputEvent::PressEnter { .. } => menu.create(cx),
                    _ => {}
                }
                cx.notify();
            }),
        ];
        Self {
            filter,
            name,
            branches: Vec::new(),
            reading: false,
            failure: None,
            refused: None,
            busy: false,
            has_changes: false,
            _heard: heard,
        }
    }
    pub fn set_state(
        &mut self,
        branches: Vec<GitBranch>,
        reading: bool,
        failure: Option<String>,
        busy: bool,
        has_changes: bool,
        cx: &mut Context<Self>,
    ) {
        self.branches = branches;
        self.reading = reading;
        self.failure = failure;
        self.busy = busy;
        self.has_changes = has_changes;
        cx.notify();
    }
    pub fn set_refused(&mut self, refused: Option<String>, cx: &mut Context<Self>) {
        self.refused = refused;
        cx.notify();
    }
    /// A finished create leaves nothing to type over next time.
    pub fn clear(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.refused = None;
        self.name.update(cx, |input, cx| input.set_value("", window, cx));
        self.filter.update(cx, |input, cx| input.set_value("", window, cx));
        cx.notify();
    }
    fn create(&mut self, cx: &mut Context<Self>) {
        let name = self.name.read(cx).value().trim().to_string();
        if self.busy || name.is_empty() || branch_name_problem(&name, &self.branches).is_some() {
            return;
        }
        cx.emit(BranchPicked::Create(name));
    }
    fn rule(cx: &App) -> Div {
        div().h(px(1.)).bg(cx.theme().border)
    }
    fn heading(title: &'static str, cx: &App) -> Div {
        div()
            .px_2()
            .text_role(TextRole::Body, cx)
            .font_weight(FontWeight::MEDIUM)
            .text_color(cx.theme().muted_foreground)
            .child(title)
    }
    fn branch_rows(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String, color: Hsla| div().px_2().py_1().text_role(TextRole::Body, cx).text_color(color).child(text);
        if let Some(failure) = &self.failure {
            return line(failure.clone(), danger).into_any_element();
        }
        if self.branches.is_empty() {
            let text = if self.reading {
                "Reading branches…"
            } else {
                "No local branches were found."
            };
            return line(text.into(), muted).into_any_element();
        }
        let filter = self.filter.read(cx).value().to_string();
        let shown: Vec<GitBranch> = filter_branches(&self.branches, &filter)
            .into_iter()
            .cloned()
            .collect();
        if shown.is_empty() {
            return line("No branch matches that.".into(), muted).into_any_element();
        }
        let busy = self.busy;
        v_flex()
            .children(shown.into_iter().map(|branch| {
                let choice = choice_for(&branch);
                ListItem::new(SharedString::from(format!("branch:{}", branch.name)))
                    .disabled(busy || choice.is_none())
                    .on_click(cx.listener(move |_, _, _, cx| {
                        if let Some(choice) = choice.clone() {
                            cx.emit(choice);
                        }
                    }))
                    .child(
                        h_flex()
                            .w_full()
                            .min_w_0()
                            .gap_2()
                            .child(div().flex_none().size(px(14.)).when(branch.current, |mark| {
                                mark.child(Icon::new(IconName::Check).size(px(14.)))
                            }))
                            .child(
                                v_flex()
                                    .flex_1()
                                    .min_w_0()
                                    .child(div().truncate().child(branch.name.clone()))
                                    .child(
                                        div()
                                            .text_role(TextRole::Body, cx)
                                            .truncate()
                                            .text_color(muted)
                                            .child(branch_detail(&branch)),
                                    ),
                            ),
                    )
            }))
            .into_any_element()
    }
}
impl Render for BranchMenu {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let name = self.name.read(cx).value().to_string();
        let problem = self
            .refused
            .clone()
            .or_else(|| branch_name_problem(&name, &self.branches));
        let can_create = !self.busy && !name.trim().is_empty() && problem.is_none();
        let danger = cx.theme().danger;
        let rows = self.branch_rows(cx);
        let (busy, has_changes) = (self.busy, self.has_changes);
        v_flex()
            .w(px(280.))
            .gap_2()
            .child(Self::heading("SWITCH BRANCH", cx))
            .child(Input::new(&self.filter).small())
            .child(
                div()
                    .id("branch-list")
                    .max_h(px(220.))
                    .overflow_y_scroll()
                    .child(rows),
            )
            .child(Self::rule(cx))
            .child(Self::heading("NEW BRANCH", cx))
            .child(Input::new(&self.name).small().disabled(busy))
            .when_some(problem, |menu, problem| {
                menu.child(div().px_2().text_role(TextRole::Body, cx).text_color(danger).child(problem))
            })
            .child(
                Button::new("branch-create")
                    .secondary()
                    .small()
                    .w_full()
                    .label("Create and switch")
                    .tooltip("Create this branch and switch to it")
                    .disabled(!can_create)
                    .on_click(cx.listener(|menu, _, _, cx| menu.create(cx))),
            )
            .child(Self::rule(cx))
            .child(Self::heading("STASH", cx))
            .child(
                ListItem::new("branch-stash")
                    .disabled(busy || !has_changes)
                    .on_click(cx.listener(|_, _, _, cx| cx.emit(BranchPicked::Stash)))
                    .child(div().child(if has_changes {
                        "Stash all changes, including new files"
                    } else {
                        "Nothing to stash"
                    })),
            )
            .child(
                ListItem::new("branch-stashes")
                    .disabled(busy)
                    .on_click(cx.listener(|_, _, _, cx| cx.emit(BranchPicked::OpenStashes)))
                    .child(div().child("Stashes…")),
            )
    }
}
#[cfg(test)]
#[path = "branch_menu_tests.rs"]
mod tests;
