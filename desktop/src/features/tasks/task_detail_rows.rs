//! How a task's page draws: the header, then the list of its head, body rows and comments.

use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::text::TextView;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::activity::ActivitySection;
use crate::core::intents::open_task::OpenTaskLink;
use crate::core::intents::open_url::OpenUrl;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::inline_field::field_row;

use super::super::task_body::{BodyRow, edit_kind};
use super::{TaskDetailEvent, TaskDetailView, muted_note};

/// Selectable text; a link in it goes through the Tasks tab, which knows where each kind belongs.
fn text_view(id: impl Into<ElementId>, markdown: impl Into<SharedString>) -> TextView {
    TextView::markdown(id, markdown)
        .selectable(true)
        .on_link_click(|url, _, window, cx| {
            window.dispatch_action(
                Box::new(OpenTaskLink {
                    url: url.to_string(),
                }),
                cx,
            );
        })
}

impl TaskDetailView {
    fn head(&self, cx: &App) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let mut column = v_flex().gap_3().px_3().pt_3().pb_2();
        if !self.seed.key.is_empty() {
            column = column.child(
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .font_family(cx.theme().mono_font_family.clone())
                    .child(self.seed.key.clone()),
            );
        }
        column = column.child(self.title.clone()).child(
            h_flex()
                .gap_3()
                .items_start()
                .flex_wrap()
                .child(self.status.clone())
                .child(self.priority.clone()),
        );
        for (name, value) in &self.others {
            let markdown = if value.starts_with("https://") || value.starts_with("http://") {
                format!("[{value}](<{value}>)")
            } else {
                super::super::task_body::rich_markdown(&serde_json::json!([{"plain_text":value}]))
            };
            let id = SharedString::from(format!("task-property-{name}"));
            column = column.child(field_row(
                name.clone(),
                None,
                None,
                text_view(id, markdown),
                cx,
            ));
        }
        if let Some(error) = &self.error {
            column = column.child(div().text_color(cx.theme().danger).child(error.clone()));
        } else if !self.loaded {
            column = column.child(muted_note("Reading the task from Notion…", cx));
        }
        if let Some(note) = &self.note {
            column = column.child(div().text_color(cx.theme().danger).child(note.clone()));
        }
        column.into_any_element()
    }

    fn body_row(&self, row: &BodyRow, cx: &mut Context<Self>) -> AnyElement {
        let indent = px(row.depth as f32 * 16.);
        let shell = div()
            .w_full()
            .min_w_0()
            .pl(indent + px(12.))
            .pr_3()
            .py_0p5();
        if let Some(editor) = self.editor.as_ref().filter(|editor| editor.id == row.id) {
            return shell.child(editor.field.clone()).into_any_element();
        }
        let muted = cx.theme().muted_foreground;
        let text = |id: &str| {
            text_view(
                SharedString::from(format!("block-{id}")),
                row.markdown.clone(),
            )
        };
        let content: AnyElement = match row.kind.as_str() {
            "divider" => div()
                .h(px(1.))
                .my_2()
                .bg(cx.theme().border)
                .into_any_element(),
            "image" => self.image_row(row, cx),
            "to_do" => {
                let this = row.clone();
                h_flex()
                    .gap_2()
                    .items_start()
                    .child(
                        Checkbox::new(SharedString::from(format!("check-{}", row.id)))
                            .checked(row.checked.unwrap_or(false))
                            .disabled(self.checking.is_some())
                            .on_click(cx.listener(move |view, checked: &bool, window, cx| {
                                view.set_checked(&this, *checked, window, cx);
                            })),
                    )
                    .child(div().flex_1().min_w_0().child(text(&row.id)))
                    .into_any_element()
            }
            "bulleted_list_item" | "numbered_list_item" => {
                let marker = row
                    .number
                    .map_or("•".to_owned(), |number| format!("{number}."));
                h_flex()
                    .gap_2()
                    .items_start()
                    .child(div().flex_shrink_0().text_color(muted).child(marker))
                    .child(div().flex_1().min_w_0().child(text(&row.id)))
                    .into_any_element()
            }
            "quote" => div()
                .pl_3()
                .border_l_2()
                .border_color(cx.theme().border)
                .text_color(muted)
                .child(text(&row.id))
                .into_any_element(),
            "callout" => h_flex()
                .gap_3()
                .items_start()
                .p_3()
                .rounded(cx.theme().radius)
                .border_1()
                .border_color(cx.theme().border)
                .bg(cx.theme().muted.opacity(0.35))
                .when_some(row.icon.clone(), |card, icon| {
                    card.child(div().flex_shrink_0().child(icon))
                })
                .child(div().flex_1().min_w_0().child(text(&row.id)))
                .into_any_element(),
            "toggle" => {
                let id = row.id.clone();
                h_flex()
                    .gap_1()
                    .items_start()
                    .child(
                        Button::new(SharedString::from(format!("toggle-{}", row.id)))
                            .named(if row.open == Some(true) {
                                "Collapse"
                            } else {
                                "Expand"
                            })
                            .ghost()
                            .xsmall()
                            .disabled(row.open.is_none())
                            .icon(if row.open == Some(true) {
                                IconName::ChevronDown
                            } else {
                                IconName::ChevronRight
                            })
                            .on_click(cx.listener(move |view, _, _, cx| view.toggle(&id, cx))),
                    )
                    .child(div().flex_1().min_w_0().child(text(&row.id)))
                    .into_any_element()
            }
            _ => text(&row.id).into_any_element(),
        };
        let group = SharedString::from(format!("task-block-{}", row.id));
        let pencil = edit_kind(&row.kind).map(|_| {
            let this = row.clone();
            div()
                .flex_shrink_0()
                .opacity(0.)
                .group_hover(group.clone(), |style| style.opacity(1.))
                .child(
                    Button::new(SharedString::from(format!("edit-{}", row.id)))
                        .named("Edit")
                        .ghost()
                        .xsmall()
                        .icon(IconName::Pencil)
                        .tooltip("Edit")
                        .on_click(cx.listener(move |view, _, window, cx| {
                            view.edit_block(&this, window, cx)
                        })),
                )
        });
        shell
            .child(
                h_flex()
                    .group(group.clone())
                    .gap_1()
                    .items_start()
                    .child(div().flex_1().min_w_0().child(content))
                    .children(pencil),
            )
            .into_any_element()
    }

    /// An image's bytes have no read path yet, so it opens in the browser.
    fn image_row(&self, row: &BodyRow, cx: &App) -> AnyElement {
        let image = row
            .image
            .clone()
            .unwrap_or(super::super::task_body::ImageRow {
                url: None,
                caption: String::new(),
            });
        let label = if image.caption.is_empty() {
            "Image".to_owned()
        } else {
            format!("Image: {}", image.caption)
        };
        let Some(url) = image.url else {
            return muted_note(label, cx).into_any_element();
        };
        Button::new(SharedString::from(format!("image-{}", row.id)))
            .small()
            .ghost()
            .icon(IconName::ExternalLink)
            .label(label)
            .on_click(move |_, window, cx| {
                window.dispatch_action(Box::new(OpenUrl { url: url.clone() }), cx)
            })
            .into_any_element()
    }

    fn foot(&self, cx: &App) -> AnyElement {
        let mut comments =
            ActivitySection::new("task-comments", "Comments").count(self.comments.len());
        if self.comments_unavailable {
            comments = comments.child(muted_note("This connection can't read comments.", cx));
        } else if self.comments.is_empty() {
            comments = comments.child(muted_note("No comments.", cx));
        }
        for (at, comment) in self.comments.iter().enumerate() {
            comments = comments.child(
                v_flex()
                    .gap_0p5()
                    .child(text_view(("task-comment", at), comment.markdown.clone()))
                    .child(
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(cx.theme().muted_foreground)
                            .child(comment.when.clone()),
                    ),
            );
        }
        v_flex()
            .gap_4()
            .px_3()
            .pt_3()
            .pb_6()
            .child(self.add_text.clone())
            .child(comments)
            .into_any_element()
    }

    fn item(&mut self, index: usize, cx: &mut Context<Self>) -> AnyElement {
        if index == 0 {
            return self.head(cx);
        }
        match self.rows.get(index - 1).cloned() {
            Some(row) => self.body_row(&row, cx),
            None => self.foot(cx),
        }
    }
}

impl Render for TaskDetailView {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.any_editing(cx) {
            self.list.remeasure();
        }
        let muted = cx.theme().muted_foreground;
        let back = Button::new("task-back")
            .control(ids::task::BACK, "Back to tasks")
            .ghost()
            .small()
            .icon(IconName::ArrowLeft)
            .tooltip("Back to tasks")
            .on_click(cx.listener(|_, _, _, cx| cx.emit(TaskDetailEvent::Back)));
        let open = Button::new("task-open")
            .control(ids::task::OPEN, "Open in Notion")
            .ghost()
            .small()
            .icon(IconName::ExternalLink)
            .tooltip("Open in Notion")
            .on_click(cx.listener(|view, _, window, cx| view.open_in_notion(window, cx)));
        v_flex()
            .size_full()
            .min_h_0()
            .track_focus(&self.focus)
            .on_key_down(cx.listener(|view, event: &KeyDownEvent, _, cx| {
                if event.keystroke.key == "escape" && !view.any_editing(cx) {
                    cx.emit(TaskDetailEvent::Back);
                    cx.stop_propagation();
                }
            }))
            .child(
                h_flex()
                    .flex_none()
                    .gap_1()
                    .px_2()
                    .py_1()
                    .items_center()
                    .border_b_1()
                    .border_color(cx.theme().border)
                    .child(back)
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .truncate()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(self.seed.project.clone()),
                    )
                    .child(open),
            )
            .child(
                list(
                    self.list.clone(),
                    cx.processor(|view, index, _, cx| view.item(index, cx)),
                )
                .flex_1()
                .min_h_0(),
            )
    }
}
