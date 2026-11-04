//! How the Changes page looks: the stacked diff with its file header buttons, comment cards and the file tree.
use super::*;
use crate::core::ui::changed_tree::{TREE_WIDTH, TreeHandler};
use crate::core::ui::diff_header::{file_heading, viewed_button};
use crate::core::{intents::open_file::OpenFile, state::selected_session::SelectedSession};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::diff::Diff;
use gpui_kit::component::input::{Input, Textarea};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use std::rc::Rc;

impl PrFiles {
    fn open_in_editor(
        session: Option<String>,
        path: String,
        line: Option<u32>,
        window: &mut Window,
        cx: &mut App,
    ) {
        let Some(session) = session else {
            return;
        };
        let selected = SelectedSession::global(cx).read(cx);
        let expected_source = (selected.session_id() == Some(session.as_str()))
            .then(|| selected.source().cloned())
            .flatten();
        window.dispatch_action(
            Box::new(OpenFile {
                session_id: session,
                path,
                expected_source,
                line,
                column: None,
                pinned: false,
            }),
            cx,
        );
    }
    fn diff(&self, cx: &mut Context<Self>) -> Diff {
        let (annotations, contents) = self.annotations(cx);
        let target = cx.entity().downgrade();
        let adder = target.clone();
        let header = target.clone();
        let viewed = self.viewed.clone();
        let session = self.owner.session_id().map(String::from);
        let line_session = session.clone();
        let mut diff = Diff::new(&self.state)
            .annotations(annotations)
            .render_header(move |file, _, cx| {
                let path = file.path().to_string();
                let toggle = header.clone();
                let toggled = path.clone();
                let actions = h_flex()
                    .gap_1()
                    .child(viewed_button(
                        &path,
                        viewed.contains(&path),
                        move |_, cx| {
                            _ = toggle
                                .update(cx, |viewer, cx| viewer.toggle_viewed(toggled.clone(), cx));
                        },
                    ))
                    .child(file_menu(&path, session.clone()));
                file_heading(file, None, actions, cx)
            })
            .on_line_click(move |position, click, window, cx| {
                if click.click_count() == 2 {
                    Self::open_in_editor(
                        line_session.clone(),
                        position.path().to_string(),
                        u32::try_from(position.line()).ok(),
                        window,
                        cx,
                    );
                }
            })
            .render_annotation(move |annotation, _, cx| {
                let content = contents
                    .iter()
                    .find(|(id, _)| id == annotation.id())
                    .map(|(_, content)| content.clone());
                annotation_card(content, target.clone(), cx)
            });
        if self.open {
            diff = diff.on_add_annotation(move |range, _, cx| {
                let position =
                    DiffLinePosition::new(range.path().clone(), range.end_side(), range.end());
                _ = adder.update(cx, |viewer, cx| viewer.comment(&position, cx));
            });
        }
        diff
    }
    fn aside(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let border = theme.border;
        let (state, picked, folded) = (
            self.state.clone(),
            cx.entity().downgrade(),
            cx.entity().downgrade(),
        );
        let on_pick: TreeHandler = Rc::new(move |path, _, cx| {
            let path = path.to_string();
            _ = picked.update(cx, |viewer, cx| {
                viewer.active_file = Some(path.clone());
                cx.notify();
            });
            state.update(cx, |state, cx| state.scroll_to_file(&path, cx));
        });
        let on_fold: TreeHandler = Rc::new(move |folder, _, cx| {
            _ = folded.update(cx, |viewer, cx| {
                viewer.tree.toggle_folder(folder);
                cx.notify();
            });
        });
        v_flex()
            .w(TREE_WIDTH)
            .flex_none()
            .h_full()
            .min_h_0()
            .border_l_1()
            .border_color(border)
            .children(
                self.filter_input
                    .as_ref()
                    .map(|(input, _)| div().p_2().child(Input::new(input).small())),
            )
            .child(div().flex_1().min_h_0().child(self.tree.list(
                "pr-files-tree",
                Rc::new(self.viewed.clone()),
                self.active_file.clone(),
                on_pick,
                on_fold,
            )))
    }
}
/// "Open in editor" and "Copy path", behind the file's ellipsis.
fn file_menu(path: &str, session: Option<String>) -> impl IntoElement + use<> {
    let (opened, copied) = (path.to_string(), path.to_string());
    Button::new(SharedString::from(format!("file-menu:{path}")))
        .ghost()
        .small()
        .icon(IconName::Ellipsis)
        .tooltip("More file actions")
        .dropdown_menu(move |menu, _, _| {
            let session = session.clone();
            let openable = session.is_some();
            let opened = opened.clone();
            let copied = copied.clone();
            menu.item(
                PopupMenuItem::new(if openable {
                    "Open in editor"
                } else {
                    "Open in editor (needs a session)"
                })
                .disabled(!openable)
                .on_click(move |_, window, cx| {
                    PrFiles::open_in_editor(session.clone(), opened.clone(), None, window, cx);
                }),
            )
            .item(PopupMenuItem::new("Copy path").on_click(move |_, _, cx| {
                cx.write_to_clipboard(ClipboardItem::new_string(copied.clone()));
            }))
        })
}
fn annotation_card(
    content: Option<AnnotationContent>,
    target: WeakEntity<PrFiles>,
    cx: &App,
) -> AnyElement {
    let theme = cx.theme();
    let (border, muted) = (theme.border, theme.muted_foreground);
    match content {
        Some(AnnotationContent::Adding(key, comment, input)) => {
            let (add, cancel) = (target.clone(), target);
            let (added, canceled) = (comment.id.clone(), comment.id.clone());
            v_flex()
                .m_2()
                .p_3()
                .gap_2()
                .rounded(theme.radius_lg)
                .border_1()
                .border_color(border)
                .child(div().font_weight(FontWeight::SEMIBOLD).child(format!(
                    "Comment on line {} ({} side)",
                    comment.target.line,
                    if comment.target.side == "LEFT" {
                        "old"
                    } else {
                        "new"
                    }
                )))
                .child(Textarea::new(&input))
                .child(
                    h_flex()
                        .gap_2()
                        .child(
                            Button::new(SharedString::from(format!("{key}:add")))
                                .small()
                                .label("Add to review")
                                .disabled(comment.target.body.trim().is_empty())
                                .on_click(move |_, _, cx| {
                                    _ = add.update(cx, |viewer, cx| viewer.add_comment(&added, cx));
                                }),
                        )
                        .child(
                            Button::new(SharedString::from(format!("{key}:cancel")))
                                .ghost()
                                .small()
                                .label("Cancel")
                                .on_click(move |_, _, cx| {
                                    _ = cancel.update(cx, |viewer, cx| {
                                        viewer.remove_comment(&canceled, cx)
                                    });
                                }),
                        ),
                )
                .into_any_element()
        }
        Some(AnnotationContent::Added(key, comment)) => {
            let id = comment.id.clone();
            h_flex()
                .m_2()
                .px_3()
                .py_1()
                .gap_2()
                .rounded(theme.radius_lg)
                .border_1()
                .border_color(border)
                .child(
                    div()
                        .flex_none()
                        .font_weight(FontWeight::SEMIBOLD)
                        .child(format!("Line {}", comment.target.line)),
                )
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .truncate()
                        .text_color(muted)
                        .child(comment.target.body),
                )
                .child(
                    Button::new(SharedString::from(format!("{key}:remove")))
                        .ghost()
                        .small()
                        .label("Remove")
                        .on_click(move |_, _, cx| {
                            _ = target.update(cx, |viewer, cx| viewer.remove_comment(&id, cx));
                        }),
                )
                .into_any_element()
        }
        None => Empty.into_any_element(),
    }
}
impl Render for PrFiles {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.sync_inputs(window, cx);
        self.sync_filter(window, cx);
        let theme = cx.theme();
        let (danger, muted) = (theme.danger, theme.muted_foreground);
        let current = self.draft.read(cx).identity == self.identity;
        let nothing = self.files.is_empty();
        let shown = self
            .parsed
            .iter()
            .any(|file| matches_filter(&file.path().to_string(), &self.filter));
        let reading = self.reading.clone();
        let notes: Vec<(String, &'static str, bool)> = self
            .notes
            .iter()
            .filter(|note| matches_filter(&note.path, &self.filter))
            .map(|note| (note.path.clone(), note.note, note.can_read))
            .collect();
        let filtered_out = !nothing && !shown && notes.is_empty();
        let body = v_flex()
            .flex_1()
            .min_w_0()
            .h_full()
            .min_h_0()
            .children((!current).then(|| {
                div().px_3().py_1().text_sm().child(
                    "This review draft is for an earlier head. Its comments are kept; refresh before posting.",
                )
            }))
            .children(self.failure.clone().map(|error| {
                div().px_3().py_1().text_sm().text_color(danger).child(error)
            }))
            .children(notes.into_iter().map(|(path, note, can_read)| {
                let is_reading = reading.as_deref() == Some(path.as_str());
                let read = path.clone();
                h_flex()
                    .px_3()
                    .py_1()
                    .gap_2()
                    .text_sm()
                    .text_color(muted)
                    .child(div().truncate().child(format!("{path} · {note}")))
                    .children(can_read.then(|| {
                        Button::new(SharedString::from(format!("read:{path}")))
                            .ghost()
                            .small()
                            .label(if is_reading { "Reading…" } else { "Show changes" })
                            .disabled(is_reading)
                            .on_click(cx.listener(move |viewer, _, _, cx| {
                                viewer.read_full(read.clone(), cx)
                            }))
                    }))
            }))
            .children(nothing.then(|| {
                div().p_3().text_color(muted).child("No changes.")
            }))
            .children(filtered_out.then(|| {
                div()
                    .p_3()
                    .text_color(muted)
                    .child("No changed file matches the filter.")
            }))
            .children(shown.then(|| div().flex_1().min_h_0().child(self.diff(cx))));
        h_flex()
            .size_full()
            .min_h_0()
            .child(body)
            .child(self.aside(cx))
    }
}
