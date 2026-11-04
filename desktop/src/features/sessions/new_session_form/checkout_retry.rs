use super::{EngineFeed, NewSessionForm, WindowCommand};
use gpui_kit::*;
use serde_json::json;
impl NewSessionForm {
    pub(super) fn retry_initial_checkout(&mut self, cx: &mut Context<Self>) {
        let Some(key) = self.start_key() else {
            return;
        };
        let Some(attempt) = self.attempt.as_ref() else {
            return;
        };
        if !attempt.request.is_current()
            || attempt
                .accepted
                .as_ref()
                .and_then(|accepted| accepted.initial_operation_id.as_ref())
                .is_none()
            || attempt
                .source
                .as_ref()
                .is_none_or(|source| !source.current(cx))
        {
            self.start_error(
                "This start does not have a verified initial current-checkout retry.",
                cx,
            );
            return;
        }
        let feed = EngineFeed::global(cx);
        let Some(session) = feed.read(cx).session(&key) else {
            self.start_error("The current accepted session is unavailable.", cx);
            return;
        };
        let operation = uuid::Uuid::new_v4().to_string();
        let command = WindowCommand {
            command_id: operation.clone(),
            entity_id: key.id,
            expected_version: session.version,
            epoch: session.epoch,
            machine: Some(key.machine),
            surface: None,
            command: json!({"kind":"change_checkout","checkoutKind":"current","branch":null,"base":null,"folder":null}),
        };
        let attempt = self.attempt.as_mut().expect("owned retry");
        attempt.retry_operation = Some(operation.clone());
        attempt.retry_version = session.version;
        self.starting = true;
        self.start_composer.update(cx, |composer, cx| {
            composer.set_start_state(false, true, None, cx)
        });
        let task = feed.update(cx, |feed, cx| feed.submit(command, cx));
        self._create = Some(cx.spawn(async move |form, cx| {
            let answer = task.await;
            let _ = form.update(cx, |form, cx| {
                if form
                    .attempt
                    .as_ref()
                    .and_then(|attempt| attempt.source.as_ref())
                    .is_none_or(|source| !source.current(cx))
                {
                    form.start_error(
                        "The retry answer belongs to a replaced source connection.",
                        cx,
                    );
                    return;
                }
                match answer {
                    Ok(receipt) if receipt.applied => {
                        let attempt = form.attempt.as_mut().expect("same retry");
                        attempt.current_operation = Some(operation);
                        attempt.retry_operation = None;
                        attempt.checkout_refused = false;
                        form.observe_start(cx);
                    }
                    Ok(receipt) => {
                        form.attempt.as_mut().expect("same retry").retry_operation = None;
                        let reason = match receipt.error.as_deref() {
                            Some("version_conflict") => "The session just changed. Try again.",
                            Some(reason) => reason,
                            None => "The checkout retry was refused.",
                        };
                        form.start_error(reason, cx);
                    }
                    Err(error) => form.start_error(&error.message, cx),
                }
            });
        }));
    }
}
