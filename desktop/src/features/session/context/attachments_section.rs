//! The Context tab's Attachments card: the draft's images first, from the composer's own pictures, then the images
//! the session's prompts carried, newest prompt first. Each row has a thumbnail slot, the name it was attached with and
//! its type; a sent image's slot shows its thumbnail once made (thumbnails.rs). A click opens one full size, as Chat's
//! chips do. A remote session's images stay on its machine; only a
//! captured, validated source permits opening those bytes. A prompt sent while the tab shows reads the sent images
//! again, so a sent draft moves down to them.

use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use serde_json::Value;

use super::ContextPane;
use super::listing::Listing;
use crate::activity::{StatusChip, Tone};
use crate::features::session::client::session_client::CallError;
use crate::features::session::composer::attachment::Attachment;
use crate::features::session::context::live_facts::DraftImage;
use crate::features::session::view::lightbox::{last_part, open_lightbox, read_image};
use crate::section_card::SectionCard;
use gpui_kit::assets::IconName;

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
        let (drafts, sent) = self.listed(cx);
        let any_sent = !sent.is_empty();
        let draft_rows = drafts.into_iter().map(|draft| {
            let selector = format!("attachment-draft-{}", draft.id);
            let row = row(
                ("attachment-draft", draft.id),
                selector,
                draft.preview.clone().map(ImageSource::from),
                draft.name.clone(),
                draft.mime,
                cx,
            );
            match draft.preview {
                Some(image) => {
                    let name = draft.name;
                    row.cursor_pointer().hover(|row| row.bg(hover)).on_click(
                        move |_, window, cx| open_lightbox(image.clone(), name.clone(), window, cx),
                    )
                }
                None => row,
            }
        });
        let draft_rows: Vec<_> = draft_rows.collect();
        let rows = draft_rows.len() + sent.len();
        let body = match &self.attachments {
            Listing::Reading => note("attachments-note-reading", "Reading this session…".into()),
            Listing::Failed(why) => note(
                "attachments-note-failed",
                format!("Couldn't read attachments: {why}").into(),
            ),
            Listing::Read(images) if images.is_empty() && draft_rows.is_empty() => note(
                "attachments-note-empty",
                "Nothing attached. Images added to this session's message box are listed here."
                    .into(),
            ),
            Listing::Read(images) => {
                v_flex().children(images.iter().enumerate().map(|(index, (path, name))| {
                    let selector = format!("attachment-{path}");
                    let thumbnail = (self.thumbnails.as_ref())
                        .and_then(|thumbnails| thumbnails.get(path))
                        .map(ImageSource::from);
                    let row = row(
                        ("attachment", index),
                        selector,
                        thumbnail,
                        name.clone().into(),
                        sent_type(path, name),
                        cx,
                    );
                    match remote {
                        Some(_)
                            if self
                                .context_scope
                                .as_ref()
                                .is_none_or(|scope| !scope.valid || scope.source.is_none()) =>
                        {
                            row.text_color(muted)
                        }
                        _ => {
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
        SectionCard::new(IconName::Image, "Attachments")
            .count(rows)
            .children(draft_rows)
            .child(body)
            .when_some(
                remote.filter(|_| {
                    any_sent
                        && self
                            .context_scope
                            .as_ref()
                            .is_none_or(|scope| !scope.valid || scope.source.is_none())
                }),
                |section, machine| {
                    section.child(note(
                        "attachments-note-remote",
                        format!("These images are on {machine} and can't be opened here yet.")
                            .into(),
                    ))
                },
            )
            .when(self.more && any_sent, |section| {
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

    /// The draft images to list, when the live facts are the selected session's, less any already sent, and the sent
    /// images read. The count chip counts both.
    pub(super) fn listed(&self, cx: &App) -> (Vec<DraftImage>, &[(String, String)]) {
        let sent: &[(String, String)] = match &self.attachments {
            Listing::Read(images) => images,
            _ => &[],
        };
        let drafts = self.live(cx).map(|facts| facts.drafts).unwrap_or_default();
        (unsent(drafts, sent), sent)
    }

    /// Reads the sent images again in their own task, after a prompt was sent while the tab shows. The files list and
    /// the images shown stay until the answer arrives, and stay if it fails; a changed source is refused as `read`
    /// does.
    pub(super) fn reread_attachments(&mut self, cx: &mut Context<Self>) {
        let Some(scope) = self.context_scope.clone().filter(|scope| scope.valid) else {
            return;
        };
        self.attachments_serial += 1;
        let (serial, attachments_serial) = (self.read_serial, self.attachments_serial);
        let answer = self.client.call(
            "rows",
            "attachments",
            scope.args(),
            Some("rows/attachments"),
            cx,
        );
        self._attachments = Some(cx.spawn(async move |pane, cx| {
            let answer = answer.await;
            pane.update(cx, |pane, cx| {
                if !pane.current(serial, &scope) {
                    return;
                }
                if matches!(&answer, Err(CallError::Refused { code, .. }) if code == "source_changed") {
                    pane.refuse_source(&scope, super::SOURCE_CHANGED.into(), cx);
                }
                pane.take_attachments(attachments_serial, answer, true, cx);
                cx.notify();
            })
            .ok();
        }));
    }

    /// Keeps the answer of attachments read `serial`, or why it couldn't be read, unless the list already holds a newer
    /// read's answer. A failed re-read (`keep_on_failure`) leaves the list as it is, and an earlier read still going
    /// can still fill it. A kept answer asks for its thumbnails.
    pub(super) fn take_attachments(
        &mut self,
        serial: u64,
        answer: Result<Value, CallError>,
        keep_on_failure: bool,
        cx: &mut Context<Self>,
    ) {
        if serial < self.attachments_shown || (keep_on_failure && answer.is_err()) {
            return;
        }
        self.attachments_shown = serial;
        (self.attachments, self.machine, self.more) = match answer {
            Ok(answer) => attached(&answer),
            Err(error) => (Listing::Failed(error.reason()), None, false),
        };
        self.want_thumbnails(cx);
    }

    /// Reads an attached image by its file name in the store (the end of its path) and opens it full size.
    pub(super) fn open(
        &mut self,
        path: String,
        name: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(scope) = self.context_scope.clone().filter(|scope| scope.valid) else {
            self.open_failed = Some("The displayed attachment source is no longer valid.".into());
            cx.notify();
            return;
        };
        let serial = self.read_serial;
        let file = last_part(&path).to_string();
        let client = self.client.clone();
        self.open_failed = None;
        cx.notify();
        self._open = Some(cx.spawn_in(window, async move |pane, cx| {
            let image = read_image(
                client,
                scope.session.clone(),
                file,
                scope.source.clone(),
                cx,
            )
            .await;
            pane.update_in(cx, |pane, window, cx| {
                if pane.read_serial != serial
                    || pane.context_scope.as_ref() != Some(&scope)
                    || !pane.active
                {
                    return;
                }
                match image {
                    Ok(image) => open_lightbox(image, name.into(), window, cx),
                    Err(error) => {
                        if matches!(&error,CallError::Refused{code,..} if code=="source_changed") {
                            pane.refuse_source(&scope, error.reason(), cx);
                        }
                        pane.open_failed = Some(error.reason());
                        cx.notify();
                    }
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

/// The draft's images less any the sent list already holds by its saved path, which shows once, as sent.
pub(super) fn unsent(drafts: Vec<DraftImage>, sent: &[(String, String)]) -> Vec<DraftImage> {
    drafts
        .into_iter()
        .filter(|draft| {
            draft
                .saved_path
                .as_ref()
                .is_none_or(|saved| !sent.iter().any(|(path, _)| path == saved))
        })
        .collect()
}

/// A sent image's type, from the extension of the name it was attached with, else of the file it was saved as.
pub(super) fn sent_type(path: &str, name: &str) -> Option<&'static str> {
    Attachment::mime_of(name).or_else(|| Attachment::mime_of(path))
}

/// The type chip's words.
pub(super) fn type_words(mime: Option<&str>) -> SharedString {
    mime.unwrap_or("type not reported").to_string().into()
}

/// One image's row: its thumbnail slot, which shows `preview` or else a plain box with an image icon, its name and its
/// type chip.
fn row(
    id: impl Into<ElementId>,
    selector: String,
    preview: Option<ImageSource>,
    name: SharedString,
    mime: Option<&'static str>,
    cx: &App,
) -> Stateful<Div> {
    let theme = cx.theme();
    let slot = format!(
        "{selector}-{}",
        if preview.is_some() {
            "picture"
        } else {
            "placeholder"
        }
    );
    let thumbnail = div()
        .debug_selector(move || slot)
        .flex_none()
        .size(px(40.))
        .flex()
        .items_center()
        .justify_center()
        .rounded(theme.radius)
        .overflow_hidden()
        .bg(theme.muted)
        .map(|thumbnail| match preview {
            Some(image) => thumbnail.child(img(image).size_full().object_fit(ObjectFit::Cover)),
            None => thumbnail.child(
                Icon::new(IconName::Image)
                    .small()
                    .text_color(theme.muted_foreground),
            ),
        });
    h_flex()
        .id(id)
        .debug_selector(move || selector)
        .px_2()
        .py_1()
        .gap_2()
        .child(thumbnail)
        .child(div().flex_1().min_w_0().truncate().child(name))
        .child(
            div()
                .flex_none()
                .child(StatusChip::new(type_words(mime), Tone::Neutral)),
        )
}

#[cfg(test)]
#[path = "attachment_rows_tests.rs"]
mod tests;
