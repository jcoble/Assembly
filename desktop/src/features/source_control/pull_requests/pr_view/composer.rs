//! The Summary page's comment box and reply box. One box writes the review draft's text: alone it posts a comment,
//! with line comments it posts a review. Posting always goes through the confirm dialog.
use super::super::{post_confirm, review_draft::LineComment};
use super::helper_writer::Spot;
use super::*;
use crate::core::theme::text::TextRole;
use gpui_kit::component::diff::{DiffLinePosition, DiffSide};
use gpui_kit::component::input::{InputEvent, Textarea};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use serde::Deserialize;

#[derive(Deserialize)]
struct DraftedReview {
    body: String,
    comments: Vec<LineComment>,
}
impl PrDetail {
    /// Makes the comment box once the draft exists, and applies text that was waiting for a window.
    pub(super) fn sync_composer(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.review_input.is_none()
            && let Some(review) = self.review.clone()
        {
            let body = review.read(cx).body.clone();
            let input = cx.new(|cx| {
                TextareaState::new(window, cx)
                    .default_value(body)
                    .placeholder("Leave a comment, or notes for the helper to write it from")
                    .auto_grow(2, 8)
            });
            let heard = cx.subscribe(
                &input,
                move |view: &mut Self, input, event: &InputEvent, cx| {
                    if matches!(event, InputEvent::Change) {
                        view.edits += 1;
                        let body = input.read(cx).value().to_string();
                        review.update(cx, |review, cx| {
                            review.body = body;
                            cx.notify();
                        });
                    }
                },
            );
            self.review_input = Some((input, heard));
        }
        if let (Some(text), Some((input, _))) = (self.pending_body.take(), &self.review_input) {
            input.update(cx, |input, cx| input.set_value(text, window, cx));
        }
        if self.reply.is_some() && self.reply_input.is_none() {
            self.reply_input = Some(cx.new(|cx| {
                TextareaState::new(window, cx)
                    .placeholder("Write a reply, or notes for the helper to write it from")
                    .auto_grow(3, 8)
            }));
        }
        if let (Some(text), Some(input)) = (self.pending_reply.take(), &self.reply_input) {
            input.update(cx, |input, cx| input.set_value(text, window, cx));
        }
    }
    fn open_post(
        &mut self,
        request: Result<PostRequest, String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match request {
            Ok(request) => post_confirm::open(request, cx.entity().downgrade(), window, cx),
            Err(error) => {
                self.failure = Some(error);
                cx.notify();
            }
        }
    }
    fn preview_post(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let request = self
            .read_identity()
            .ok_or_else(|| "Refresh first. Your comment is kept.".to_string())
            .and_then(|identity| {
                let review = self
                    .review
                    .as_ref()
                    .ok_or_else(|| "Refresh first. Your comment is kept.".to_string())?;
                let review = review.read(cx);
                if review.comments.is_empty() && self.verdict == "COMMENT" {
                    PostRequest::comment(identity, review.body.clone())
                } else {
                    PostRequest::review(review, &identity, self.verdict)
                }
            });
        self.open_post(request, window, cx);
    }
    fn preview_reply(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let request = match (self.read_identity(), &self.reply, &self.reply_input) {
            (Some(identity), Some(reply), Some(input)) => {
                PostRequest::reply(identity, reply.id, input.read(cx).value().to_string())
            }
            _ => Err("Refresh first. Your reply is kept.".to_string()),
        };
        self.open_post(request, window, cx);
    }
    /// The pull request a post is checked against: an open one for a review, as read for a comment or reply.
    pub fn post_identity(&self, request: &PostRequest) -> Option<ReviewIdentity> {
        if request.is_review() {
            self.current_identity()
        } else {
            self.read_identity()
        }
    }
    pub fn writes_free(&self, cx: &App) -> bool {
        self.writes.read(cx).free(cx)
    }
    /// Starts a post the person just confirmed. Refused, it says why and nothing was sent.
    pub fn start_post(
        &mut self,
        request: &PostRequest,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        let identity = self
            .post_identity(request)
            .ok_or_else(|| "Refresh before posting. Your draft is kept.".to_string())?;
        self.posted = None;
        self.writes
            .update(cx, |writes, cx| writes.post(request, &identity, cx))
    }
    /// Starts a merge the person just confirmed. Refused, it says why and nothing was sent.
    pub fn start_merge(
        &mut self,
        request: &MergeRequest,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        if self.current_identity().is_none() {
            return Err("Refresh before merging.".into());
        }
        self.writes
            .update(cx, |writes, cx| writes.merge(request, cx))
    }
    pub(super) fn write_done(&mut self, done: &WriteDone, cx: &mut Context<Self>) {
        match done {
            WriteDone::Posted { reply, url } => self.post_finished(*reply, url.clone(), cx),
            WriteDone::Merged { url } => {
                self.merge_finished(url.clone(), cx);
                if let Some(repository) = self.repository.clone() {
                    cx.emit(PrMerged {
                        repository,
                        number: self.number,
                    });
                }
            }
            WriteDone::Acted { act } => {
                self.acted = Some(act.done());
                self.refresh(cx);
            }
        }
    }
    /// The read a page just took in. A draft tied to a head that moved is cleared, as is a reply to it; words the
    /// person only typed stay and go with the new head. Line comments can't outlive an open pull request.
    pub(super) fn page_read(&mut self, writes_settled: bool, cx: &mut Context<Self>) {
        if writes_settled {
            self.writes.update(cx, |writes, cx| writes.page_read(cx));
        }
        let Some(detail) = self.detail.as_ref() else {
            return;
        };
        let identity = ReviewIdentity {
            repository: detail.repository.clone(),
            number: detail.number,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        };
        let open = detail.state == "OPEN";
        let Some(review) = self.review.clone() else {
            return;
        };
        let held = review.read(cx);
        let moved = held.identity.head != identity.head;
        let tied_to_old_head = !held.comments.is_empty()
            || self
                .drafted_head
                .as_ref()
                .is_some_and(|head| *head != identity.head);
        if moved {
            self.reply = None;
            self.reply_input = None;
        }
        if moved && tied_to_old_head {
            self.clear_draft(cx);
            self.failure = Some(
                "The PR changed. Your unposted comment was cleared; look at the new changes before commenting."
                    .into(),
            );
        }
        review.update(cx, |review, cx| {
            review.identity = identity;
            if !open {
                review.comments.clear();
            }
            cx.notify();
        });
    }
    /// The draft is spent or thrown away; the next one belongs to the head on screen.
    fn clear_draft(&mut self, cx: &mut Context<Self>) {
        self.drafted_head = None;
        let identity = self.current_identity();
        if let Some(review) = &self.review {
            review.update(cx, |review, cx| {
                review.body.clear();
                review.comments.clear();
                if let Some(identity) = identity {
                    review.identity = identity;
                }
                cx.notify();
            });
        }
        self.pending_body = Some(String::new());
        cx.notify();
    }
    /// A post landed on GitHub.
    pub fn post_finished(&mut self, was_reply: bool, url: String, cx: &mut Context<Self>) {
        if was_reply {
            self.reply = None;
            self.reply_input = None;
        } else {
            self.clear_draft(cx);
        }
        self.posted = Some(url);
        self.refresh(cx);
    }
    /// The merge landed on GitHub; anything unposted no longer has a pull request to go to.
    pub fn merge_finished(&mut self, url: String, cx: &mut Context<Self>) {
        self.clear_draft(cx);
        self.reply = None;
        self.reply_input = None;
        self.posted = None;
        self.merged = Some(url);
        self.refresh(cx);
    }
    fn draft_with_helper(&mut self, cx: &mut Context<Self>) {
        let Some(identity) = self.current_identity() else {
            self.failure = Some("Refresh first. Your comment is kept.".into());
            cx.notify();
            return;
        };
        let shown = (self.detail.as_ref())
            .map(|detail| (detail.repository.as_str(), detail.local_root.as_str()));
        let Some(scope) = self.owner.scope(shown) else {
            self.failure = Some("Refresh first. Your comment is kept.".into());
            cx.notify();
            return;
        };
        let args = json!({"number":identity.number,"expectedHead":identity.head});
        self.drafting_at = Some((identity, self.edits));
        self.drafter.update(cx, |drafter, cx| {
            drafter.start_scope("draftReview", scope, args, cx)
        });
        cx.notify();
    }
    /// A generated draft lands only on the same head and untouched words; anything else leaves the text alone.
    pub(super) fn draft_changed(&mut self, cx: &mut Context<Self>) {
        let drafter = self.drafter.read(cx);
        let Some(status) = drafter.status.as_ref().filter(|status| status.terminal()) else {
            cx.notify();
            return;
        };
        let answer = (status.state == "succeeded")
            .then(|| status.result.clone())
            .flatten()
            .and_then(|result| serde_json::from_value::<DraftedReview>(result).ok());
        let asked = self.drafting_at.take();
        let current = self.current_identity();
        let unchanged = asked.is_some_and(|(identity, edits)| {
            Some(&identity) == current.as_ref() && edits == self.edits
        });
        if let (true, Some(answer), Some(review)) = (unchanged, answer, self.review.clone()) {
            self.pending_body = Some(answer.body.clone());
            self.drafted_head = self.current_identity().map(|identity| identity.head);
            review.update(cx, |review, cx| {
                review.body = answer.body;
                for comment in &answer.comments {
                    let side = if comment.side == "LEFT" {
                        DiffSide::Original
                    } else {
                        DiffSide::Modified
                    };
                    let position = DiffLinePosition::new(comment.path.clone(), side, comment.line);
                    let Ok(id) = review.start_comment(&position) else {
                        break;
                    };
                    review.update_comment(&id, comment.body.clone());
                }
                cx.notify();
            });
        }
        cx.notify();
    }
    pub(super) fn composer(&self, window: &Window, cx: &mut Context<Self>) -> AnyElement {
        let Some((input, _)) = &self.review_input else {
            return Empty.into_any_element();
        };
        let Some(review) = self.review.as_ref().map(|review| review.read(cx)) else {
            return Empty.into_any_element();
        };
        let theme = cx.theme();
        let (surface, muted, danger, radius, mono, ring) = (
            theme.secondary,
            theme.muted_foreground,
            theme.danger,
            theme.radius_lg,
            theme.mono_font_family.clone(),
            theme.ring,
        );
        let focused = input.focus_handle(cx).contains_focused(window, cx);
        let writes_free = self.writes_free(cx);
        let has_text = !review.body.trim().is_empty();
        let lines: Vec<_> = review.comments.clone();
        let open = self
            .detail
            .as_ref()
            .is_some_and(|detail| detail.state == "OPEN");
        let verdict = if open { self.verdict } else { "COMMENT" };
        let label = match verdict {
            "APPROVE" if lines.is_empty() => "Approve".to_string(),
            "APPROVE" => format!(
                "Approve · {}",
                super::super::pr_activity::post_label(lines.len())
                    .trim_start_matches("Post review · ")
            ),
            "REQUEST_CHANGES" => "Request changes".to_string(),
            _ => super::super::pr_activity::post_label(lines.len()),
        };
        let writing = self.helper_writing(Spot::Comment, cx);
        let can_undo = self.can_undo(Spot::Comment, cx);
        let helper_error = self.helper_error(Spot::Comment, cx);
        let target = cx.entity().downgrade();
        let drafter = self.drafter.read(cx);
        let drafting = drafter.busy;
        let draft_error = drafter.error.clone().or(helper_error);
        let review_entity = self.review.clone();
        let identity = if verdict == "COMMENT" && lines.is_empty() {
            self.read_identity()
        } else {
            self.current_identity()
        };
        let can_post = (has_text || verdict == "APPROVE") && writes_free && identity.is_some();
        v_flex()
            .gap_2()
            .p_3()
            .rounded(radius)
            .bg(surface)
            .border_1()
            .border_color(if focused { ring } else { transparent_black() })
            .child(Textarea::new(input).appearance(false))
            .children(lines.into_iter().map(|line| {
                let id = line.id.clone();
                let review = review_entity.clone();
                h_flex()
                    .gap_2()
                    .text_role(TextRole::Body, cx)
                    .child(
                        div()
                            .flex_none()
                            .text_role(TextRole::Meta, cx)
                            .font_family(mono.clone())
                            .text_color(muted)
                            .child(format!("{}:{}", line.target.path, line.target.line)),
                    )
                    .child(div().flex_1().min_w_0().truncate().child(line.target.body))
                    .child(
                        Button::new(SharedString::from(format!("draft-line:{}", line.id)))
                            .ghost()
                            .small()
                            .label("Remove")
                            .on_click(move |_, _, cx| {
                                if let Some(review) = &review {
                                    review.update(cx, |review, cx| {
                                        review.remove_comment(&id);
                                        cx.notify();
                                    });
                                }
                            }),
                    )
            }))
            .children(draft_error.map(|error| {
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        Button::new("pr-post")
                            .primary()
                            .small()
                            .label(label)
                            .disabled(!can_post)
                            .on_click(
                                cx.listener(|view, _, window, cx| view.preview_post(window, cx)),
                            ),
                    )
                    .when(open, |row| {
                        row.child(
                            Button::new("pr-verdict")
                                .ghost()
                                .small()
                                .icon(IconName::ChevronDown)
                                .tooltip("Comment, approve or request changes")
                                .dropdown_menu(move |menu, _, _| {
                                    ["COMMENT", "APPROVE", "REQUEST_CHANGES"].into_iter().fold(menu, |menu, choice| {
                                        let target = target.clone();
                                        menu.item(PopupMenuItem::new(verdict_label(choice)).checked(choice == verdict).on_click(
                                            move |_, _, cx| {
                                                _ = target.update(cx, |view, cx| {
                                                    view.verdict = choice;
                                                    cx.notify();
                                                });
                                            },
                                        ))
                                    })
                                }),
                        )
                    })
                    .child(div().flex_1())
                    .child(
                        Button::new("pr-helper-write")
                            .ghost()
                            .small()
                            .icon(IconName::Sparkles)
                            .label(if writing {
                                "Writing…"
                            } else if has_text {
                                "Write from my notes"
                            } else {
                                "Suggest a comment"
                            })
                            .loading(writing)
                            .disabled(writing || drafting)
                            .on_click(cx.listener(|view, _, _, cx| view.write_with_helper(Spot::Comment, cx))),
                    )
                    .when(can_undo, |row| {
                        row.child(
                            Button::new("pr-helper-undo")
                                .ghost()
                                .small()
                                .icon(IconName::Undo2)
                                .label("Undo")
                                .on_click(cx.listener(|view, _, _, cx| view.undo_helper(cx))),
                        )
                    })
                    .when(open, |row| {
                        row.child(
                            Button::new("pr-draft")
                                .ghost()
                                .small()
                                .label(if drafting { "Drafting…" } else { "Draft a review" })
                                .tooltip("The utility model reads the changes and drafts a review with line comments.")
                                .loading(drafting)
                                .disabled(drafting || writing)
                                .on_click(cx.listener(|view, _, _, cx| view.draft_with_helper(cx))),
                        )
                    })
                    .when(has_text || !review.comments.is_empty(), |row| {
                        row.child(
                            Button::new("pr-discard")
                                .ghost()
                                .small()
                                .label("Discard")
                                .on_click(cx.listener(|view, _, _, cx| view.clear_draft(cx))),
                        )
                    }),
            )
            .into_any_element()
    }
    pub(super) fn reply_box(&self, window: &Window, cx: &mut Context<Self>) -> Option<AnyElement> {
        let reply = self.reply.as_ref()?;
        let input = self.reply_input.as_ref()?;
        let theme = cx.theme();
        let (surface, radius, ring) = (theme.secondary, theme.radius_lg, theme.ring);
        let focused = input.focus_handle(cx).contains_focused(window, cx);
        let place = match reply.line {
            Some(line) => format!("{}:{line}", reply.path),
            None => reply.path.clone(),
        };
        let spot = Spot::Reply(reply.id);
        let writing = self.helper_writing(spot, cx);
        let can_undo = self.can_undo(spot, cx);
        let helper_error = self.helper_error(spot, cx);
        let danger = cx.theme().danger;
        let has_text = !input.read(cx).value().trim().is_empty();
        let can_send = !input.read(cx).value().trim().is_empty()
            && self.writes_free(cx)
            && self.read_identity().is_some();
        Some(
            v_flex()
                .gap_2()
                .p_3()
                .rounded(radius)
                .bg(surface)
                .border_1()
                .border_color(if focused { ring } else { transparent_black() })
                .child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .font_weight(FontWeight::SEMIBOLD)
                        .child(format!("Reply to {} on {place}", reply.author)),
                )
                .child(Textarea::new(input).appearance(false))
                .children(helper_error.map(|error| {
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(danger)
                        .child(error)
                }))
                .child(
                    h_flex()
                        .gap_2()
                        .child(
                            Button::new("pr-reply-helper")
                                .ghost()
                                .small()
                                .icon(IconName::Sparkles)
                                .label(if writing {
                                    "Writing…"
                                } else if has_text {
                                    "Write from my notes"
                                } else {
                                    "Suggest a reply"
                                })
                                .loading(writing)
                                .disabled(writing)
                                .on_click(cx.listener(move |view, _, _, cx| {
                                    view.write_with_helper(spot, cx)
                                })),
                        )
                        .when(can_undo, |row| {
                            row.child(
                                Button::new("pr-reply-undo")
                                    .ghost()
                                    .small()
                                    .icon(IconName::Undo2)
                                    .label("Undo")
                                    .on_click(cx.listener(|view, _, _, cx| view.undo_helper(cx))),
                            )
                        })
                        .child(div().flex_1())
                        .child(
                            Button::new("pr-reply-cancel")
                                .ghost()
                                .small()
                                .label("Cancel")
                                .on_click(cx.listener(|view, _, _, cx| {
                                    view.reply = None;
                                    view.reply_input = None;
                                    cx.notify();
                                })),
                        )
                        .child(
                            Button::new("pr-reply-send")
                                .small()
                                .label("Reply")
                                .disabled(!can_send)
                                .on_click(cx.listener(|view, _, window, cx| {
                                    view.preview_reply(window, cx)
                                })),
                        ),
                )
                .into_any_element(),
        )
    }
}
fn verdict_label(verdict: &str) -> &'static str {
    match verdict {
        "APPROVE" => "Approve",
        "REQUEST_CHANGES" => "Request changes",
        _ => "Comment",
    }
}
