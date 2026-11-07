//! The utility model writing a comment or reply. Typed notes are rewritten into the comment; an empty box gets a
//! suggestion. The answer lands only on the same head and the same words it was asked about, and Undo puts the words
//! back until they're edited again.
use super::*;

#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) enum Spot {
    Comment,
    /// The thread's first comment, by GitHub's number.
    Reply(u64),
}
pub(super) struct Writing {
    spot: Spot,
    head: String,
    sent: String,
}
pub(super) struct Undo {
    pub spot: Spot,
    before: String,
    landed: String,
}
impl PrDetail {
    fn spot_text(&self, spot: Spot, cx: &App) -> Option<String> {
        match spot {
            Spot::Comment => self
                .review
                .as_ref()
                .map(|review| review.read(cx).body.clone()),
            Spot::Reply(id) => (self.reply.as_ref().filter(|reply| reply.id == id))
                .and(self.reply_input.as_ref())
                .map(|input| input.read(cx).value().to_string()),
        }
    }
    fn set_spot_text(&mut self, spot: Spot, text: String, cx: &mut Context<Self>) {
        match spot {
            Spot::Comment => {
                if let Some(review) = &self.review {
                    review.update(cx, |review, cx| {
                        review.body = text.clone();
                        cx.notify();
                    });
                }
                self.pending_body = Some(text);
            }
            Spot::Reply(_) => self.pending_reply = Some(text),
        }
        cx.notify();
    }
    pub(super) fn write_with_helper(&mut self, spot: Spot, cx: &mut Context<Self>) {
        let (Some(identity), Some(sent)) = (self.read_identity(), self.spot_text(spot, cx)) else {
            self.failure = Some("Refresh first. Your words are kept.".into());
            cx.notify();
            return;
        };
        let shown = (self.detail.as_ref())
            .map(|detail| (detail.repository.as_str(), detail.local_root.as_str()));
        let Some(scope) = self.owner.scope(shown) else {
            self.failure = Some("Refresh first. Your words are kept.".into());
            cx.notify();
            return;
        };
        let target = match spot {
            Spot::Comment => json!({"kind":"overall"}),
            Spot::Reply(id) => json!({"kind":"reply","commentId":id.to_string()}),
        };
        let args = json!({"number":identity.number,"expectedHead":identity.head,"target":target,"intent":sent});
        self.undo = None;
        self.writing = Some(Writing {
            spot,
            head: identity.head,
            sent,
        });
        self.writer.update(cx, |writer, cx| {
            writer.start_scope("draftComment", scope, args, cx)
        });
        cx.notify();
    }
    /// Where the helper is writing now, so only that box says so.
    pub(super) fn helper_writing(&self, spot: Spot, cx: &App) -> bool {
        self.writer.read(cx).busy
            && self
                .writing
                .as_ref()
                .is_some_and(|writing| writing.spot == spot)
    }
    pub(super) fn helper_error(&self, spot: Spot, cx: &App) -> Option<String> {
        let writer = self.writer.read(cx);
        (!writer.busy
            && self.writing.is_none()
            && self.undo.is_none()
            && self.last_spot == Some(spot))
        .then(|| writer.error.clone())
        .flatten()
    }
    pub(super) fn writer_changed(&mut self, cx: &mut Context<Self>) {
        let writer = self.writer.read(cx);
        let Some(status) = writer.status.as_ref().filter(|status| status.terminal()) else {
            cx.notify();
            return;
        };
        let answer = (status.state == "succeeded")
            .then(|| {
                status
                    .result
                    .as_ref()
                    .and_then(Value::as_str)
                    .map(str::to_owned)
            })
            .flatten()
            .filter(|answer| !answer.trim().is_empty());
        let Some(asked) = self.writing.take() else {
            cx.notify();
            return;
        };
        self.last_spot = Some(asked.spot);
        let same_head = self
            .read_identity()
            .is_some_and(|identity| identity.head == asked.head);
        let untouched = self.spot_text(asked.spot, cx).as_deref() == Some(asked.sent.as_str());
        if let (true, true, Some(answer)) = (same_head, untouched, answer) {
            self.undo = Some(Undo {
                spot: asked.spot,
                before: asked.sent,
                landed: answer.clone(),
            });
            self.set_spot_text(asked.spot, answer, cx);
        }
        cx.notify();
    }
    /// Undo is offered while the helper's words are still what the box holds.
    pub(super) fn can_undo(&self, spot: Spot, cx: &App) -> bool {
        self.undo.as_ref().is_some_and(|undo| {
            undo.spot == spot && self.spot_text(spot, cx).as_deref() == Some(undo.landed.as_str())
        })
    }
    pub(super) fn undo_helper(&mut self, cx: &mut Context<Self>) {
        if let Some(undo) = self.undo.take() {
            self.set_spot_text(undo.spot, undo.before, cx);
        }
    }
}
