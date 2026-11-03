use super::*;
impl Composer {
    /// Sends the draft once every attachment has uploaded; one whose upload failed is removed first.
    pub(super) fn send(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.start_draft.is_some(){self.send_start(cx);return;}
        if !self.operational(){return;}
        let Some(session) = self.session.clone() else {
            return;
        };
        let typed = self.draft.read(cx).value().trim().to_string();
        if self.offline.is_some()
            || !self.all_saved()
            || (typed.is_empty() && self.attachments.is_empty())
        {
            return;
        }
        let text = Attachment::with_lines(&typed, &self.attachments);
        let attached = std::mem::take(&mut self.attachments);
        self.uploads.clear();
        (self.slash_selected, self.error) = (None, None);
        self.draft
            .update(cx, |draft, cx| draft.set_value("", window, cx));
        let attachment_ids: Vec<String> = attached
            .iter()
            .filter_map(|attachment| match &attachment.state {
                AttachmentState::Saved { path } => Some(path.clone()),
                _ => None,
            })
            .collect();
        let owner_key=self.session_key.clone().expect("operational owner");
        let request = match self.retry_ids.remove(&owner_key) {
            Some(request) if request.text == text && request.attachment_ids == attachment_ids => {
                request
            }
            _ => {
                self.named += 1;
                let ms = chrono::Utc::now().timestamp_millis();
                PromptRequest {
                    text: text.clone(),
                    client_id: format!("{ms}-{}-{}", std::process::id(), self.named),
                    attachment_ids,
                    epoch: None,
                }
            }
        };
        self.submit(session, request, typed, attached, window, cx);
    }

    /// Retries a failed prompt's captured payload while preserving any new draft.
    pub fn retry_pending(&mut self, client_id: &str, window: &mut Window, cx: &mut Context<Self>) {
        if !self.operational(){return;}
        let owner_key=self.session_key.clone().expect("operational owner");
        let Some(session) = self.session.clone().filter(|_| self.offline.is_none()) else {
            return;
        };
        let Some(pending) = self
            .pending(&session)
            .iter()
            .find(|pending| {
                pending.client_id() == client_id && pending.state.starts_with("Failed:")
            })
            .cloned()
        else {
            return;
        };
        let (typed, references) = Attachment::split_lines(pending.text());
        let mut attached = references
            .into_iter()
            .map(|(path, name, _)| {
                self.attached += 1;
                Attachment {
                    id: self.attached,
                    name: name.into(),
                    size: 0,
                    state: AttachmentState::Saved { path },
                }
            })
            .collect();
        if self.draft.read(cx).value().to_string() == typed
            && self
                .attachments
                .iter()
                .filter_map(|a| match &a.state {
                    AttachmentState::Saved { path } => Some(path),
                    _ => None,
                })
                .eq(pending.request.attachment_ids.iter())
        {
            self.draft
                .update(cx, |draft, cx| draft.set_value("", window, cx));
            attached = std::mem::take(&mut self.attachments);
        } else if self
            .failed_drafts
            .get(&owner_key)
            .is_some_and(|(text, _)| text == &typed)
        {
            attached = self.failed_drafts.remove(&owner_key).unwrap().1;
        }
        self.error = None;
        self.retry_ids.remove(&owner_key);
        self.submit(session, pending.request, typed, attached, window, cx);
    }

    pub fn recover_pending(&mut self, cx: &mut Context<Self>) {
        self.read_pending(cx);
    }

    pub(super) fn submit(
        &mut self,
        session: String,
        request: PromptRequest,
        typed: String,
        attached: Vec<Attachment>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let args = self.source_args(json!({ "session": session, "text": request.text,
            "clientId": request.client_id, "attachmentIds": request.attachment_ids,
            "epoch": request.epoch }));
        let Some(prompt_key)=self.session_key.clone().filter(|key|key.id==session && self.operational())else{return;};
        let pending = self.pending.entry(prompt_key.clone()).or_default();
        if !pending
            .iter()
            .any(|p| p.request.client_id == request.client_id)
        {
            pending.push(PendingPrompt::sending(request.clone()));
        } else if let Some(pending) = pending
            .iter_mut()
            .find(|p| p.request.client_id == request.client_id)
        {
            pending.state = "sending".into();
        }
        cx.emit(ComposerEvent::Sending);
        let call = self.client.call("send", "send", args, None, cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks
            .push(cx.spawn_in(window, async move |composer, cx| {
                let answer = call.await.and_then(|answer| {
                    let accepted: OwnerPromptAccepted = serde_json::from_value(answer)
                        .map_err(|_| CallError::Failed("Invalid prompt acceptance".into()))?;
                    if accepted.session_id != session
                        || accepted.client_id != request.client_id
                        || accepted.message_id != format!("prompt/{session}/{}", request.client_id)
                        || accepted.state != "queued"
                        || accepted.run_id.is_some()
                        || request.epoch.is_some_and(|epoch| epoch != accepted.epoch)
                        || accepted.message_version < 0
                        || accepted.seq < 0
                        || accepted.epoch < 0
                    {
                        return Err(CallError::Failed("Invalid prompt acceptance".into()));
                    }
                    Ok(accepted)
                });
                composer
                    .update_in(cx, |composer, window, cx| match answer {
                        Ok(accepted) => {
                            if let Some(pending) =
                                composer.pending.get_mut(&prompt_key).and_then(|items| {
                                    items
                                        .iter_mut()
                                        .find(|p| p.request.client_id == accepted.client_id)
                                })
                            {
                                if pending.version <= accepted.message_version {
                                    pending.state = accepted.state.clone();
                                    pending.version = accepted.message_version;
                                }
                                pending.receipt = Some(accepted.clone());
                            }
                            if composer.session.as_ref() == Some(&session)
                                && composer.session_key.as_ref() == Some(&prompt_key)
                            {
                                composer.watch_pending(cx);
                            }
                            cx.emit(ComposerEvent::Accepted{key:prompt_key.clone(),receipt:accepted});
                        }
                        // It didn't go out, or the socket closed before it answered (`Aborted`: a send has no key,
                        // so nothing else takes its place): it comes back to its session's draft with its images,
                        // unless something new was typed there, and a resend keeps its clientId in case the first
                        // did land. Images don't follow a draft to another session.
                        Err(error) => {
                            let selected = composer.session.as_ref() == Some(&session)
                                && composer.session_key.as_ref() == Some(&prompt_key);
                            if composer
                                .delivered
                                .get(&prompt_key)
                                .is_some_and(|ids| ids.contains(&request.client_id))
                            {
                                return;
                            }
                            if let Some(pending) =
                                composer.pending.get_mut(&prompt_key).and_then(|items| {
                                    items
                                        .iter_mut()
                                        .find(|p| p.request.client_id == request.client_id)
                                })
                            {
                                pending.state = format!("Failed: {}", error.reason());
                            }
                            cx.emit(ComposerEvent::PendingChanged);
                            if selected {
                                composer.source_refused(&error,cx);
                                let hint = if matches!(error, CallError::Unreachable { .. }) {
                                    " Check Settings > Connections."
                                } else {
                                    ""
                                };
                                composer.error = Some(
                                    format!(
                                        "Couldn't send: {}.{hint}",
                                        error.reason().trim_end_matches('.')
                                    )
                                    .into(),
                                );
                                cx.notify();
                            }
                            composer.retry_ids.insert(prompt_key.clone(), request);
                            if !selected {
                                let draft=composer.drafts.entry(prompt_key.clone()).or_default();
                                if draft.0.is_empty()&&draft.1.is_empty() {
                                    *draft=(typed,attached);
                                } else {
                                    composer.failed_drafts.insert(prompt_key.clone(), (typed, attached));
                                }
                            } else if composer.draft.read(cx).value().is_empty()
                                && composer.attachments.is_empty()
                            {
                                composer
                                    .draft
                                    .update(cx, |draft, cx| draft.set_value(typed, window, cx));
                                composer.attachments = attached;
                                cx.notify();
                            } else {
                                composer.failed_drafts.insert(prompt_key.clone(), (typed, attached));
                            }
                        }
                    })
                    .ok();
            }));
    }
}
