//! How the Changes page looks: the stacked diff with its file header buttons, comment cards and the file tree.
use super::super::pr_stack::changed_tree;
use super::*;
use crate::core::{
    intents::open_file::OpenFile, state::selected_session::SelectedSession,
    ui::markdown::view::MarkdownView,
};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::diff::Diff;
use gpui_kit::component::input::{Input, Textarea};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::scroll::ScrollableElement as _;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};

impl PrFiles {
    fn diff(&self, cx: &mut Context<Self>) -> Diff {
        let (annotations, contents) = self.annotations(cx);
        let target = cx.entity().downgrade();
        let adder = target.clone();
        let header = target.clone();
        let viewed = self.viewed.clone();
        let session = self.owner.session_id().map(String::from);
        let mut diff = Diff::new(&self.state)
            .annotations(annotations)
            .render_header_suffix(move |file, _, _| {
                let path = file.path().to_string();
                let is_viewed = viewed.contains(&path);
                let toggle = header.clone();
                let toggled = path.clone();
                let opened = path.clone();
                let copied = path.clone();
                let session = session.clone();
                h_flex()
                    .gap_1()
                    .child(
                        Button::new(SharedString::from(format!("viewed:{path}")))
                            .ghost()
                            .small()
                            .icon(if is_viewed {
                                IconName::EyeOff
                            } else {
                                IconName::Eye
                            })
                            .tooltip(if is_viewed {
                                "Mark as not viewed"
                            } else {
                                "Mark as viewed"
                            })
                            .on_click(move |_, _, cx| {
                                _ = toggle.update(cx, |viewer, cx| {
                                    viewer.toggle_viewed(toggled.clone(), cx)
                                });
                            }),
                    )
                    .child(
                        Button::new(SharedString::from(format!("file-menu:{path}")))
                            .ghost()
                            .small()
                            .icon(IconName::Ellipsis)
                            .tooltip("More for this file")
                            .dropdown_menu(move |menu, _, _| {
                                let session = session.clone();
                                let openable = session.is_some();
                                let opened = opened.clone();
                                let copied = copied.clone();
                                menu.item(PopupMenuItem::new("Open in editor").disabled(!openable).on_click(
                                    move |_, window, cx| {
                                        let Some(session) = session.clone() else {
                                            return;
                                        };
                                        let selected = SelectedSession::global(cx).read(cx);
                                        let expected_source = (selected.session_id()
                                            == Some(session.as_str()))
                                        .then(|| selected.source().cloned())
                                        .flatten();
                                        window.dispatch_action(
                                            Box::new(OpenFile {
                                                session_id: session,
                                                path: opened.clone(),
                                                expected_source,
                                                line: None,
                                                column: None,
                                                pinned: false,
                                            }),
                                            cx,
                                        );
                                    },
                                ))
                                .item(
                                    PopupMenuItem::new("Copy path").on_click(move |_, _, cx| {
                                        cx.write_to_clipboard(ClipboardItem::new_string(
                                            copied.clone(),
                                        ));
                                    }),
                                )
                            }),
                    )
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
        let (border, muted) = (theme.border, theme.muted_foreground);
        let paths: Vec<String> = self
            .parsed
            .iter()
            .map(|file| file.path().to_string())
            .chain(self.notes.iter().map(|note| note.path.clone()))
            .filter(|path| matches_filter(path, &self.filter))
            .collect();
        let rows = changed_tree(paths.iter().map(String::as_str));
        let state = self.state.clone();
        v_flex()
            .w(px(260.))
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
            .child(
                v_flex()
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scrollbar()
                    .text_sm()
                    .children(rows.into_iter().enumerate().map(|(index, row)| {
                        let indent = px(8. + row.depth as f32 * 12.);
                        let icon = if row.path.is_some() {
                            IconName::File
                        } else {
                            IconName::FolderOpen
                        };
                        let mut item = ListItem::new(SharedString::from(format!("tree:{index}")))
                            .pl(indent)
                            .child(
                                h_flex()
                                    .gap_1()
                                    .min_w_0()
                                    .child(Icon::new(icon).size(px(14.)).text_color(muted))
                                    .child(div().truncate().child(row.label)),
                            );
                        if let Some(path) = row.path {
                            let state = state.clone();
                            item = item.on_click(move |_, _, cx| {
                                state.update(cx, |state, cx| state.scroll_to_file(&path, cx));
                            });
                        }
                        item
                    })),
            )
    }
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
                                    _ = add.update(cx, |viewer, cx| {
                                        viewer.adding.remove(&added);
                                        cx.notify();
                                    });
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
        Some(AnnotationContent::Posted(comment)) => v_flex()
            .m_2()
            .p_3()
            .gap_1()
            .rounded(theme.radius_lg)
            .border_1()
            .border_color(border)
            .child(div().font_weight(FontWeight::SEMIBOLD).child(comment.author.clone()))
            .child(MarkdownView {
                id: SharedString::from(format!("pr-comment:{}", comment.id)),
                blocks: comment.body_blocks.clone(),
                session: None,
                expected_source: None,
            })
            .into_any_element(),
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
