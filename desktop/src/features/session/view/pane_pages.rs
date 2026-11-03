//! The session pane's existing page loading lifecycle.
use gpui_kit::*;
use serde_json::Value;

use super::{PAGE_ROWS, SessionPane};
use crate::features::session::client::session_client::CallError;
use crate::features::session::model::change::Change;
use crate::features::session::rows::row_page::RowPage;

impl SessionPane {
    /// Loads the page above what's loaded, when the timeline nears its top and one isn't already loading.
    pub fn load_older(&mut self, cx: &mut Context<Self>) {
        let Some(before) = self.view.oldest.filter(|_| {
            self.view.has_before && !self.loading && self.failed_before != self.view.oldest
        }) else {
            return;
        };
        self.loading = true;
        self.refresh(Change::Nothing, cx);
        self.load_page(before, false, cx);
    }

    /// Loads the page below `before`; `first` on open, when the page also says the run going now.
    pub(super) fn load_page(&mut self, before: i64, first: bool, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let args = crate::core::socket::row_source::page_args(
            &session,
            before,
            PAGE_ROWS,
            self.row_source.as_ref(),
        );
        let key = crate::core::socket::row_source::page_key(&session, self.row_source.as_ref());
        let call = self.client.call("rows", "page", args, Some(&key), cx);
        self._page = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            pane.update(cx, |pane, cx| pane.on_page(answer, before, first, cx))
                .ok();
        }));
    }

    fn on_page(
        &mut self,
        answer: Result<Value, CallError>,
        before: i64,
        first: bool,
        cx: &mut Context<Self>,
    ) {
        self.loading = false;
        let page = answer.and_then(|page| {
            serde_json::from_value::<RowPage>(page)
                .map_err(|error| CallError::Failed(error.to_string()))
        });
        self.status = match &page {
            Ok(_) | Err(CallError::Aborted) => SharedString::default(),
            Err(CallError::Unreachable { machine }) => format!("{machine} is offline").into(),
            Err(
                CallError::NotFound(message)
                | CallError::BadArgs(message)
                | CallError::Failed(message)
                | CallError::Refused { message, .. }
                | CallError::Uncertain { message, .. }
                | CallError::Timeout(message),
            ) => format!("Couldn't load the session: {message}").into(),
        };
        let Ok(page) = page else {
            // A failed first page keeps the live rows waiting for Try again or the next `Jump`'s page; an older one
            // isn't asked for again by itself.
            if first {
                self.retry_first = Some(before);
            } else {
                self.failed_before = Some(before);
            }
            self.refresh(Change::Nothing, cx);
            return;
        };
        self.failed_before = None;
        if first {
            self.retry_first = None;
            // What the backend says runs now, so a run that ended while the socket was away stops counting.
            self.known_run = page.active_run_id;
        }
        self.view.prepend_page(page.rows, page.has_before);
        if first {
            for row in self.held.take().unwrap_or_default() {
                self.view.apply(&row);
            }
        }
        self.refresh(Change::Nothing, cx);
    }
}
