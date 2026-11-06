//! One pushed reconnect re-enters the sole navigator with the original actual key. It never replays a command.
use super::{
    SessionPane,
    opening::{ConnectionCut, refused},
};
use gpui_kit::*;
/// How many times running a session reopens by itself before its source has said anything.
const MOST_SOURCE_REOPENS: u8 = 3;

impl SessionPane {
    pub(super) fn reopen_after_connection(&mut self, active: u64, cx: &mut Context<Self>) {
        if self.active_open_serial != Some(active)
            || self.pending_open.is_some()
            || self.reopen_queued.is_some()
        {
            return;
        }
        let Some(previous) = self.active_source_cut.as_ref() else {
            return;
        };
        if previous.current(cx) {
            return;
        }
        let Ok(current) = ConnectionCut::capture(cx) else {
            return;
        };
        if previous.local_engine(cx) != current.local_engine(cx) {
            self.composer
                .update(cx, |composer, cx| composer.invalidate_source(cx));
            self.status="The backend Engine owner changed. The visible journal was kept; select a current session to continue.".into();
            self.active_source_cut = None;
            cx.notify();
            return;
        }
        self.queue_reopen(active, cx);
    }

    /// The session's machine is answering again after it took the session's source with it.
    pub(in crate::features::session::view) fn reopen_lost_source(
        &mut self,
        cx: &mut Context<Self>,
    ) {
        if let Some(active) = self.active_open_serial
            && self.pending_open.is_none()
            && self.reopen_queued.is_none()
        {
            // Each reopen answers at once, so a source that never settles must not spin here.
            if self.source_reopens >= MOST_SOURCE_REOPENS {
                self.source_lost = false;
                let machine = (self.row_source.as_ref())
                    .and_then(|source| source.route_machine.clone())
                    .unwrap_or_default();
                self.status =
                    format!("{machine} keeps reconnecting. Open the session again.").into();
                cx.notify();
                return;
            }
            self.source_reopens += 1;
            self.queue_reopen(active, cx);
        }
    }

    fn queue_reopen(&mut self, active: u64, cx: &mut Context<Self>) {
        let Some((mut request, window)) = self.active_reopen.clone() else {
            return;
        };
        let serial = self.open_serial;
        self.reopen_queued = Some(active);
        let pane = cx.weak_entity();
        cx.defer(move |cx| {
            let _ = window.update(cx, |_, window, cx| {
                let _ = pane.update(cx, |pane, cx| {
                    if pane.reopen_queued != Some(active) {
                        return;
                    }
                    pane.reopen_queued = None;
                    if pane.active_open_serial != Some(active)
                        || pane.open_serial != serial
                        || pane.pending_open.is_some()
                    {
                        return;
                    }
                    // Capture again inside the same UI turn; a replaced or not-ready connection cannot pass.
                    let Ok(current) = ConnectionCut::capture(cx) else {
                        return;
                    };
                    if pane
                        .active_source_cut
                        .as_ref()
                        .is_none_or(|old| old.local_engine(cx) != current.local_engine(cx))
                    {
                        pane.status = refused(
                            "source_changed",
                            "The reconnect source changed before revalidation.",
                        )
                        .message
                        .into();
                        cx.notify();
                        return;
                    }
                    request.request_id = format!(
                        "{}{}",
                        crate::core::intents::session_open::RECONNECT_REQUEST,
                        uuid::Uuid::new_v4()
                    );
                    pane.request_open(request, window, cx);
                });
            });
        });
    }
}
