//! The Context tab's Attachments section: the images the session's prompts carried, newest prompt first, each by the
//! name it was attached with. A click opens one full size, as Chat's chips do. A remote session's images stay on its
//! machine (`attachments/read` serves only this one's store), so its rows say so and don't open.

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use serde_json::Value;

use super::ContextPane;
use super::listing::Listing;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::composer::attachment::Attachment;
use crate::features::session::view::lightbox::{open_lightbox, read_image};

impl ContextPane {
    pub(super) fn attachments_section(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let remote = self.machine.clone();
        let theme = cx.theme();
        let (muted, hover, danger) = (theme.muted_foreground, theme.muted, theme.danger);
        let note = |id: &'static str, text: SharedString| {
            div()
                .debug_selector(move || id.into())
                .px_2()
                .text_color(muted)
                .child(text)
        };
        let count = match &self.attachments {
            Listing::Read(images) if !images.is_empty() => Some(images.len()),
            _ => None,
        };
        let body = match &self.attachments {
            Listing::Reading => note("attachments-note-reading", "Reading this session…".into()),
            Listing::Failed(why) => note(
                "attachments-note-failed",
                format!("Couldn't read attachments: {why}").into(),
            ),
            Listing::Read(images) if images.is_empty() => note(
                "attachments-note-empty",
                "Nothing attached. Images added to this session's message box are listed here."
                    .into(),
            ),
            Listing::Read(images) => {
                v_flex().children(images.iter().enumerate().map(|(index, (path, name))| {
                    let selector = format!("attachment-{path}");
                    let row = h_flex()
                        .id(("attachment", index))
                        .debug_selector(move || selector)
                        .h(px(28.))
                        .px_2()
                        .child(div().min_w_0().truncate().child(name.clone()));
                    match remote {
                        Some(_) => row.text_color(muted),
                        None => {
                            let (path, name) = (path.clone(), name.clone());
                            row.cursor_pointer()
                                .hover(|row| row.bg(hover))
                                .on_click(cx.listener(move |pane, _, window, cx| {
                                    pane.open(path.clone(), name.clone(), window, cx)
                                }))
                        }
                    }
                }))
            }
        };
        v_flex()
            .gap_1()
            .child(
                h_flex()
                    .px_2()
                    .h(px(28.))
                    .gap_2()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Attachments")
                    .when_some(count, |heading, count| {
                        heading.child(div().text_color(muted).child(count.to_string()))
                    }),
            )
            .child(body)
            .when_some(remote.filter(|_| count.is_some()), |section, machine| {
                section.child(note(
                    "attachments-note-remote",
                    format!("These images are on {machine} and can't be opened here yet.").into(),
                ))
            })
            .when(self.more && count.is_some(), |section| {
                section.child(note(
                    "attachments-note-more",
                    "Only images from this session's newest 50 prompts are listed.".into(),
                ))
            })
            .when_some(self.open_failed.clone(), |section, why| {
                section.child(
                    div()
                        .debug_selector(|| "attachments-open-failed".into())
                        .px_2()
                        .text_color(danger)
                        .child(format!("Couldn't open the image: {why}")),
                )
            })
    }

    /// Reads an attached image by its file name in the store (the end of its path) and opens it full size.
    fn open(&mut self, path: String, name: String, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
        else {
            return;
        };
        let file = path
            .rsplit(['/', '\\'])
            .next()
            .unwrap_or_default()
            .to_string();
        let client = self.client.clone();
        self.open_failed = None;
        cx.notify();
        self._open = Some(cx.spawn_in(window, async move |pane, cx| {
            let image = read_image(client, session, file, cx).await;
            pane.update_in(cx, |pane, window, cx| match image {
                Ok(image) => open_lightbox(image, name.into(), window, cx),
                Err(error) => {
                    pane.open_failed = Some(error.reason());
                    cx.notify();
                }
            })
            .ok();
        }));
    }
}

/// `rows/attachments`' answer as each image's (path, name), newest prompt first, the machine that has them, and whether
/// older prompts were left out (RowsFeature.AttachmentPrompts). Only a
/// prompt's trailing "Attached image:" lines name images, as Chat reads them.
pub(super) fn attached(answer: &Value) -> (Listing<(String, String)>, Option<String>, bool) {
    let images = answer["prompts"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .flat_map(|text| Attachment::split_lines(text).1)
        .map(|(path, name, _)| (path, name))
        .collect();
    let machine = answer["machine"]
        .as_str()
        .filter(|machine| !machine.is_empty());
    let more = answer["more"].as_bool().unwrap_or_default();
    (Listing::Read(images), machine.map(String::from), more)
}
