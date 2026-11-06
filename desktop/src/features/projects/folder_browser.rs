use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderListing {
    pub path: String,
    pub directories: Vec<String>,
    pub truncated: bool,
}
pub struct FolderChosen {
    pub machine: String,
    pub path: String,
}
pub struct FolderBrowser {
    machine: String,
    pub listing: Option<FolderListing>,
    error: Option<String>,
    loading: bool,
    revision: u64,
    read: Option<Task<()>>,
}
impl EventEmitter<FolderChosen> for FolderBrowser {}
impl FolderBrowser {
    pub fn new(machine: String, path: String, cx: &mut Context<Self>) -> Self {
        let mut browser = Self {
            machine,
            listing: None,
            error: None,
            loading: false,
            revision: 0,
            read: None,
        };
        browser.browse(path, cx);
        browser
    }
    pub fn browse(&mut self, path: String, cx: &mut Context<Self>) {
        self.read = None;
        self.revision += 1;
        let revision = self.revision;
        self.listing = None;
        self.error = None;
        self.loading = true;
        let Some(socket) = SocketClient::try_global(cx) else {
            self.loading = false;
            self.error = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        let call = socket.read(cx).call::<FolderListing>(
            "projects",
            "folders",
            json!({"machine":self.machine,"path":path}),
            None,
        );
        self.read = Some(cx.spawn(async move |browser, cx| {
            let outcome = call.await;
            _ = browser.update(cx, |browser, cx| {
                if browser.revision != revision {
                    return;
                }
                browser.loading = false;
                browser.read = None;
                match outcome {
                    Outcome::Ok(listing) => browser.listing = Some(listing),
                    Outcome::Failed(error) => browser.error = Some(error.message),
                    Outcome::Aborted => {
                        browser.error = Some("The folder read was cancelled.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
    pub fn choose(&mut self, cx: &mut Context<Self>) {
        if self.loading {
            return;
        }
        if let Some(listing) = &self.listing {
            cx.emit(FolderChosen {
                machine: self.machine.clone(),
                path: listing.path.clone(),
            });
        }
    }
}
impl Render for FolderBrowser {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let parent = self
            .listing
            .as_ref()
            .and_then(|listing| std::path::Path::new(&listing.path).parent())
            .map(|path| path.to_string_lossy().to_string());
        let count = self
            .listing
            .as_ref()
            .map_or(0, |listing| listing.directories.len());
        let path = self
            .listing
            .as_ref()
            .map_or("Reading folders…".to_string(), |listing| {
                listing.path.clone()
            });
        let danger = cx.theme().danger;
        v_flex()
            .gap_1()
            .h(px(240.))
            .min_h_0()
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("folder-up")
                            .ghost()
                            .compact()
                            .label("Up")
                            .disabled(parent.is_none() || self.loading)
                            .on_click(cx.listener(move |browser, _, _, cx| {
                                if let Some(parent) = &parent {
                                    browser.browse(parent.clone(), cx);
                                }
                            })),
                    )
                    .child(
                        div()
                            .flex_1()
                            .overflow_hidden()
                            .text_role(TextRole::Body, cx)
                            .child(path),
                    )
                    .child(
                        Button::new("folder-choose")
                            .primary()
                            .compact()
                            .label("Use this folder")
                            .disabled(self.listing.is_none() || self.loading)
                            .on_click(cx.listener(|browser, _, _, cx| browser.choose(cx))),
                    ),
            )
            .child(
                uniform_list(
                    "parent-folders",
                    count,
                    cx.processor(|browser, range: std::ops::Range<usize>, _, cx| {
                        range
                            .filter_map(|index| {
                                browser
                                    .listing
                                    .as_ref()
                                    .and_then(|listing| listing.directories.get(index))
                                    .cloned()
                                    .map(|path| {
                                        let label = std::path::Path::new(&path)
                                            .file_name()
                                            .map_or(path.clone(), |name| {
                                                name.to_string_lossy().to_string()
                                            });
                                        Button::new(SharedString::from(format!(
                                            "parent-folder:{index}"
                                        )))
                                        .ghost()
                                        .label(label)
                                        .on_click(
                                            cx.listener(move |browser, _, _, cx| {
                                                browser.browse(path.clone(), cx)
                                            }),
                                        )
                                    })
                            })
                            .collect()
                    }),
                )
                .flex_1()
                .min_h_0(),
            )
            .children(
                self.listing
                    .as_ref()
                    .filter(|listing| listing.truncated)
                    .map(|_| {
                        div().text_role(TextRole::Body, cx).child(
                            "More folders are not listed. Type a path to open another folder.",
                        )
                    }),
            )
            .children(self.error.clone().map(|error| {
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
    }
}
#[cfg(test)]
#[path = "folder_browser_tests.rs"]
mod tests;
