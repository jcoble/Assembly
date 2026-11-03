//! The coordinator's one owned CAS writer serves both actual view changes and departure; no timer or second persistence path.
use gpui_kit::{Window,Context,AppContext as _};
use super::{WorkspaceCoordinator,WorkspacePanes,WorkspaceSaved,Read,Saved,SocketClient,Outcome};
use serde_json::json;
impl WorkspaceCoordinator {
    pub(super) fn save_current(&mut self,window:&mut Window,cx:&mut Context<Self>) {
        if self.saving.is_some()||self.guarding.is_some()||self.migrating.is_some() {return;}
        let Some(current) = &self.current else {
            if self.active_request {self.read_target(window,cx);}
            return;
        };
        // A failed/corrupt hydration is never saved over merely because another session was selected.
        if !current.writable {
            if self.active_request {self.read_target(window,cx);}
            return;
        }
        let snapshot = match WorkspacePanes::capture(window, cx) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.status = Some(format!("Not saved: {error}"));
                if self.active_request {self.guard_outgoing(window,cx);}
                cx.notify();
                return;
            }
        };
        if current.safe_to_leave.as_ref() == Some(&snapshot) {
            if self.active_request {self.read_target(window,cx);}
            return;
        }
        let (key, expected) = (current.key.clone(), current.revision);
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.save_failed("the bulk connection is unavailable.".into(),cx);
            return;
        };
        let call = socket.read(cx).call::<Saved>(
            "workspaces",
            "save",
            json!({"machine":key.machine,"sessionId":key.session_id,
            "checkoutRoot":key.checkout_root,"expectedRevision":expected,"snapshot":snapshot}),
            None,
        );
        let handle = window.window_handle();
        self.saving = Some(cx.spawn(async move |view, cx| {
            let outcome = call.await;
            let saved = match outcome {
                Outcome::Ok(saved) if saved.revision == expected + 1 => Ok(saved.revision),
                failed => {
                    // A lost save result is reconciled by exact revision and captured view JSON, never blindly replayed.
                    let message = match failed { Outcome::Failed(error) => error.message, _ => "The workspace save result is unknown.".into() };
                    let read = view.update(cx, |_, cx| socket.read(cx).call::<Read>("workspaces", "read", json!({
                        "machine":key.machine,"sessionId":key.session_id,"checkoutRoot":key.checkout_root}), None));
                    match read { Ok(read) => match read.await {
                        Outcome::Ok(read) if read.revision == expected + 1 && read.snapshot.as_ref() == Some(&snapshot) => Ok(read.revision),
                        _ => Err(message),
                    }, Err(_) => return }
                }
            };
            let _ = handle.update(cx, |_, window, cx| view.update(cx, |view, cx| {
                view.saving = None;
                if view.current_key() != Some(&key) { return; }
                match saved {
                    Ok(revision) => {
                        let current = view.current.as_mut().expect("same outgoing workspace");
                        current.revision = revision; current.safe_to_leave = Some(snapshot);
                        view.status=None;
                        let event=WorkspaceSaved {key:key.clone(),revision};let owner=cx.entity();
                        cx.defer(move|cx|owner.update(cx,|_,cx|cx.emit(event)));
                        // The same owner drains newer actual edits, including when no selection switch is pending.
                        view.save_current(window,cx);
                    }
                    Err(error) => {view.save_failed(error,cx);}
                }
                cx.notify();
            }));
        }));
    }

    fn save_failed(&mut self,error:String,cx:&mut Context<Self>) {
        let message=format!("Not saved: {error}");self.status=Some(message.clone());
        if self.active_request {self.refuse(message,cx);}cx.notify();
    }
}
