//! `SourceControlPane` is the drawer's Source control tab: the selected session's project's branch and changed files
//! from `git/status`, read only while the tab is shown (`SessionRead`, scm#12). The files are in Staged, Changes and
//! Untracked, each with its letter (scm#10). Replaces SourceControlPanel.svelte's status lists. Staging, commits,
//! diffs and branches aren't ported yet.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::git_status::{GitStatus, Section};
use crate::core::intents::open_diff::OpenDiff;
use crate::core::state::session_read::{SessionRead, Shown};

/// The pane's name in the saved layout (g0-shape's `SOURCE_CONTROL`).
const SOURCE_CONTROL: &str = "Source control";

pub struct SourceControlPane {
    focus: FocusHandle,
    read: Entity<SessionRead<GitStatus>>,
    _heard: Subscription,
}

impl SourceControlPane {
    /// Registers the pane under the drawer's Source control name, after the placeholders, since the last registration
    /// wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, SOURCE_CONTROL, |_, _, cx| {
            panel_handle(cx.new(SourceControlPane::new))
        });
    }

    pub fn new(cx: &mut Context<Self>) -> Self {
        let read = cx.new(|cx| SessionRead::new("git", "status", cx));
        let heard = cx.observe(&read, |_, _, cx| cx.notify());
        Self {
            focus: cx.focus_handle(),
            read,
            _heard: heard,
        }
    }
}

/// The lists, each under its name and count; an empty one is left out. Picking a file opens its diff (scm#13).
fn lists(status: &GitStatus, session: &str, cx: &App) -> Div {
    let theme = cx.theme();
    let muted = theme.muted_foreground;
    v_flex()
        .gap_2()
        .children(Section::ALL.into_iter().filter_map(|section| {
            let files = status.section(section);
            if files.is_empty() {
                return None;
            }
            let heading = h_flex()
                .px_2()
                .gap_2()
                .text_xs()
                .font_weight(FontWeight::MEDIUM)
                .text_color(muted)
                .child(section.label())
                .child(files.len().to_string());
            let rows = files.into_iter().map(|(letter, file)| {
                let color = match letter {
                    'A' | 'C' | 'U' => theme.success,
                    'D' | '!' => theme.danger,
                    _ => theme.warning,
                };
                let open = OpenDiff {
                    session_id: session.to_string(),
                    path: file.path.clone(),
                    original_path: file.original_path.clone(),
                };
                h_flex()
                    .id(SharedString::from(format!(
                        "{}:{}",
                        section.label(),
                        file.path
                    )))
                    .h(px(24.))
                    .px_2()
                    .gap_2()
                    .text_sm()
                    .rounded_sm()
                    .cursor_pointer()
                    .hover(|row| row.bg(theme.list_hover))
                    .on_click(move |_, window, cx| {
                        window.dispatch_action(Box::new(open.clone()), cx)
                    })
                    .child(
                        div()
                            .flex_none()
                            .w(px(12.))
                            .text_color(color)
                            .child(letter.to_string()),
                    )
                    .child(div().min_w_0().truncate().child(file.label()))
            });
            Some(v_flex().child(heading).children(rows))
        }))
}

impl EventEmitter<PanelEvent> for SourceControlPane {}

impl Focusable for SourceControlPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for SourceControlPane {
    fn panel_name(&self) -> &'static str {
        SOURCE_CONTROL
    }

    /// The drawer's panes are permanent tabs (g0-shape#7).
    fn closable(&self, _cx: &App) -> bool {
        false
    }

    fn set_active(&mut self, active: bool, _window: &mut Window, cx: &mut Context<Self>) {
        self.read.update(cx, |read, cx| read.set_active(active, cx));
    }
}

impl Panel for SourceControlPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        SOURCE_CONTROL
    }
}

impl Render for SourceControlPane {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let line = |text: String| div().px_3().text_sm().text_color(muted).child(text);
        let (shown, reading, session) = {
            let read = self.read.read(cx);
            let session = read.session().unwrap_or_default().to_string();
            (read.shown().clone(), read.reading(), session)
        };
        let header = h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .justify_between()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::MEDIUM)
                    .child(SOURCE_CONTROL),
            )
            .child(
                Button::new("source-control-refresh")
                    .ghost()
                    .compact()
                    .label(if reading { "Reading…" } else { "Refresh" })
                    .disabled(reading || shown == Shown::NoSession)
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.read.update(cx, |read, cx| read.refresh(cx))
                    })),
            );
        let body = match shown {
            Shown::NoSession => {
                line("Pick a session to see its project's changes.".into()).into_any_element()
            }
            Shown::Reading => line("Reading the changes…".into()).into_any_element(),
            Shown::Failed(message) => v_flex()
                .px_3()
                .gap_2()
                .items_start()
                .child(div().text_sm().text_color(danger).child(message))
                .child(
                    Button::new("source-control-retry")
                        .ghost()
                        .compact()
                        .label("Try again")
                        .on_click(cx.listener(|pane, _, _, cx| {
                            pane.read.update(cx, |read, cx| read.refresh(cx))
                        })),
                )
                .into_any_element(),
            Shown::Read(status) => div()
                .id("source-control")
                .flex_1()
                .min_h_0()
                .overflow_y_scroll()
                .px_1()
                .child(
                    div()
                        .px_2()
                        .pb_2()
                        .text_sm()
                        .truncate()
                        .child(status.branch_line()),
                )
                .map(|pane| {
                    if status.files.is_empty() {
                        return pane.child(line("No changes.".into()));
                    }
                    pane.child(lists(&status, &session, cx))
                })
                .when_some(status.more_line(), |pane, more| pane.child(line(more)))
                .into_any_element(),
        };
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(header)
            .child(body)
    }
}
