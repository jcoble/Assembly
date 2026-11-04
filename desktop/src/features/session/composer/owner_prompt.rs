//! Owner-prompt receipts and recovery through the existing bounded Engine reads.
use super::*;
use base64::Engine as _;
use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct OwnerPromptAccepted {
    pub session_id: String,
    pub message_id: String,
    pub client_id: String,
    pub state: String,
    pub message_version: i64,
    pub seq: i64,
    pub epoch: i64,
    pub run_id: Option<String>,
}

#[derive(Clone)]
pub(super) struct PromptRequest {
    pub text: String,
    pub client_id: String,
    pub attachment_ids: Vec<String>,
    pub epoch: Option<i64>,
}

#[derive(Clone)]
pub struct PendingPrompt {
    pub(super) request: PromptRequest,
    pub state: String,
    pub version: i64,
    pub at: i64,
    pub receipt: Option<OwnerPromptAccepted>,
}

impl PendingPrompt {
    pub(super) fn sending(request: PromptRequest) -> Self {
        Self {
            request,
            state: "sending".into(),
            version: 0,
            at: chrono::Utc::now().timestamp_millis(),
            receipt: None,
        }
    }
    /// Events carry normalized state only; snapshot items also carry the current display identity.
    fn apply_display(
        &mut self,
        item: &serde_json::Value,
        session: &str,
        message: &str,
        machine: Option<&str>,
    ) -> bool {
        let event = item["entityId"].is_string();
        let (id, selected, state) = if event {
            (&item["entityId"], &item["data"]["sessionId"], &item["data"])
        } else {
            if item["clientId"].as_str() != Some(self.client_id()) {
                return false;
            }
            (&item["id"], &item["toSessionId"], item)
        };
        if id.as_str() != Some(message)
            || selected.as_str() != Some(session)
            || item["entityKind"].as_str() != Some("message")
            || machine.is_some_and(|machine| item["machine"].as_str() != Some(machine))
        {
            return false;
        }
        let (Some(version), Some(state_name)) = (item["version"].as_i64(), state["state"].as_str())
        else {
            return false;
        };
        if version < self.version {
            return false;
        }
        self.version = version;
        self.state = match state["reason"].as_str().filter(|reason| !reason.is_empty()) {
            Some(reason) => format!("{state_name}: {reason}"),
            None => state_name.to_string(),
        };
        true
    }

    fn terminal(&self) -> bool {
        matches!(
            self.state.split(':').next(),
            Some("failed" | "unsupported" | "delivered_to_turn" | "next_turn")
        )
    }

    pub fn client_id(&self) -> &str {
        &self.request.client_id
    }
    pub fn text(&self) -> &str {
        &self.request.text
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Page {
    engine_machine: String,
    items: Vec<Manifest>,
    next_created_ms: Option<i64>,
    next_client_id: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    client_id: String,
    content_sha256: String,
    text_preview: String,
    text_complete: bool,
    text_utf8_bytes: u64,
    attachment_json_utf8_bytes: u64,
    target_epoch: Option<i64>,
    state: String,
    version: i64,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Chunk {
    content_sha256: String,
    part: String,
    offset: u64,
    total_bytes: u64,
    data: String,
    next_offset: Option<u64>,
}

fn invalid() -> CallError {
    CallError::Failed("Invalid pending prompt response".into())
}

impl Composer {
    pub fn pending(&self, session: &str) -> &[PendingPrompt] {
        self.session_key
            .as_ref()
            .filter(|key| key.id == session)
            .and_then(|key| self.pending.get(key))
            .map(Vec::as_slice)
            .unwrap_or_default()
    }
    /// A row acknowledgment applies only to the currently source-qualified owner.
    pub fn delivered(&mut self, session: &str, client_id: &str) {
        let Some(key) = self.session_key.clone().filter(|key| key.id == session) else {
            return;
        };
        self.message_subscriptions
            .remove(&format!("prompt/{session}/{client_id}"));
        self.delivered
            .entry(key.clone())
            .or_default()
            .insert(client_id.into());
        if let Some(items) = self.pending.get_mut(&key) {
            items.retain(|item| item.client_id() != client_id);
        }
    }

    /// Each selected owner's pending message owns one keyed subscription, released on switch/delivery.
    pub(super) fn watch_pending(&mut self, cx: &mut Context<Self>) {
        if !self.operational() {
            return;
        }
        let key = self.session_key.clone().expect("operational owner");
        let generation = self.source_serial;
        let Some(session) = self.session.clone() else {
            return;
        };
        let machine = self.machine.clone();
        let clients: Vec<String> = self
            .pending(&session)
            .iter()
            .filter(|pending| !pending.terminal())
            .map(|p| p.client_id().to_string())
            .collect();
        for client_id in clients {
            let message = format!("prompt/{session}/{client_id}");
            if self.message_subscriptions.contains_key(&message) {
                continue;
            }
            let weak = cx.entity().downgrade();
            let (selected, owner, expected) = (session.clone(), machine.clone(), message.clone());
            let key = key.clone();
            let subscription = self.client.subscribe_message(
                &message,
                machine.as_deref(),
                self.expected_source.as_ref(),
                Box::new(move |item, cx| {
                    _ = weak.update(cx, |composer, cx| {
                        if !composer.operational() || composer.source_serial!=generation || composer.session_key.as_ref()!=Some(&key) || composer.session.as_ref() != Some(&selected) || composer.machine != owner
                        {
                            return;
                        }
                        let item=match item {Ok(item)=>item,Err(error)=>{
                            if matches!(&error,CallError::Refused{code,..} if code=="source_changed"){composer.invalidate_source(cx);}
                            composer.error=Some(error.reason().into());cx.notify();return;
                        }};
                        let mut delivered = false;
                        if let Some(pending) = composer
                            .pending
                            .get_mut(&key)
                            .and_then(|items| items.iter_mut().find(|p| p.client_id() == client_id))
                        {
                            if pending.apply_display(&item, &selected, &expected, owner.as_deref())
                            {
                                cx.emit(ComposerEvent::PendingChanged);
                            }
                            delivered = matches!(
                                pending.state.split(':').next(),
                                Some("next_turn" | "delivered_to_turn")
                            );
                            if pending.terminal() {
                                composer.message_subscriptions.remove(&expected);
                            }
                        }
                        if delivered {
                            composer.delivered(&selected, &client_id);
                            cx.emit(ComposerEvent::PendingChanged);
                        }
                    });
                }),
                cx,
            );
            self.message_subscriptions.insert(message, subscription);
        }
    }

    pub(super) fn read_pending(&mut self, cx: &mut Context<Self>) {
        self._pending_read = None;
        self.message_subscriptions.clear();
        self.watch_pending(cx);
        if !self.operational() {
            return;
        }
        let generation = self.source_serial;
        let Some(session) = self.session.clone() else {
            return;
        };
        let client = self.client.clone();
        let mut machine = self.machine.clone();
        let key = self.session_key.clone().expect("operational owner");
        let expected_machine = key.machine.clone();
        let source = self.expected_source.clone().expect("operational source");
        self._pending_read = Some(cx.spawn(async move |composer, cx| {
            let mut after_created = None;
            let mut after_client: Option<String> = None;
            let result: Result<Vec<PendingPrompt>, CallError> = async {
                let mut prompts = Vec::new();
                loop {
                    let call = cx.update(|cx| {
                        client.call("engine", "owner_prompts",
                        json!({"session":session,"machine":machine,"afterCreatedMs":after_created,
                            "afterClientId":after_client,"limit":50,"expectedSource":source}), None, cx)
                    });
                    let page: Page = serde_json::from_value(call.await?).map_err(|_| invalid())?;
                    if page.engine_machine!=expected_machine{return Err(invalid());}
                    machine = Some(page.engine_machine);
                    for manifest in page.items {
                        let text = if manifest.text_complete {
                            manifest.text_preview.clone()
                        } else {
                            String::from_utf8(
                                read_part(
                                    &client,
                                    &session,
                                    &manifest,
                                    "text",
                                    manifest.text_utf8_bytes,
                                    machine.as_deref(),
                                    &source,
                                    cx,
                                )
                                .await?,
                            )
                            .map_err(|_| invalid())?
                        };
                        let attachments = read_part(
                            &client,
                            &session,
                            &manifest,
                            "attachments",
                            manifest.attachment_json_utf8_bytes,
                            machine.as_deref(),
                            &source,
                            cx,
                        )
                        .await?;
                        let attachment_ids =
                            serde_json::from_slice(&attachments).map_err(|_| invalid())?;
                        prompts.push(PendingPrompt {
                            request: PromptRequest {
                                text,
                                client_id: manifest.client_id,
                                attachment_ids,
                                epoch: manifest.target_epoch,
                            },
                            state: manifest.state,
                            version: manifest.version,
                            at: chrono::Utc::now().timestamp_millis(),
                            receipt: None,
                        });
                    }
                    match (page.next_created_ms, page.next_client_id) {
                        (None, None) => break,
                        (Some(created), Some(id))
                            if (Some(created), Some(&id))
                                != (after_created, after_client.as_ref()) =>
                        {
                            after_created = Some(created);
                            after_client = Some(id);
                        }
                        _ => return Err(invalid()),
                    }
                }
                Ok(prompts)
            }
            .await;
            _ = composer.update(cx, |composer, cx| {
                if !composer.operational()||composer.source_serial!=generation||composer.session_key.as_ref()!=Some(&key){return;}
                match result {
                Ok(prompts) => {
                    let items = composer.pending.entry(key.clone()).or_default();
                    for prompt in prompts {
                        if composer
                            .delivered
                            .get(&key)
                            .is_some_and(|ids| ids.contains(prompt.client_id()))
                        {
                            continue;
                        }
                        match items
                            .iter_mut()
                            .find(|item| item.client_id() == prompt.client_id())
                        {
                            Some(item) if item.version < prompt.version => *item = prompt,
                            None => items.push(prompt),
                            _ => {}
                        }
                    }
                    composer.watch_pending(cx);
                    cx.emit(ComposerEvent::PendingChanged);
                }
                Err(error) => {
                    if matches!(&error,CallError::Refused{code,..} if code=="source_changed"){composer.invalidate_source(cx);}
                    composer.error = Some(
                        format!("Couldn't recover pending prompts: {}", error.reason()).into(),
                    );
                    cx.notify();
                }
                }
            });
        }));
    }
}

async fn read_part(
    client: &Rc<dyn SessionClient>,
    session: &str,
    manifest: &Manifest,
    part: &str,
    total: u64,
    machine: Option<&str>,
    source: &crate::core::socket::row_source::ExpectedRowSource,
    cx: &mut AsyncApp,
) -> Result<Vec<u8>, CallError> {
    let mut bytes = Vec::new();
    let mut offset = 0;
    loop {
        let call = cx.update(|cx| client.call("engine", "owner_prompt_chunk",
            json!({"session":session,"clientId":manifest.client_id,"contentSha256":manifest.content_sha256,
                "part":part,"offset":offset,"count":1048576,"machine":machine,"expectedSource":source}), None, cx));
        let chunk: Chunk = serde_json::from_value(call.await?).map_err(|_| invalid())?;
        let data = base64::engine::general_purpose::STANDARD
            .decode(&chunk.data)
            .map_err(|_| invalid())?;
        let end = offset + data.len() as u64;
        if chunk.content_sha256 != manifest.content_sha256
            || chunk.part != part
            || chunk.offset != offset
            || chunk.total_bytes != total
            || end > total
            || chunk.next_offset != (end < total).then_some(end)
            || (end == offset && end < total)
        {
            return Err(invalid());
        }
        bytes.extend(data);
        if chunk.next_offset.is_none() {
            return Ok(bytes);
        }
        offset = end;
    }
}

#[cfg(test)]
mod tests {
    use super::{PendingPrompt, PromptRequest};
    use serde_json::json;
    #[test]
    fn pushed_held_failed_and_delivered_state_preserve_the_original_request() {
        let mut pending = PendingPrompt::sending(PromptRequest {
            text: "完整 text".into(),
            client_id: "c".into(),
            attachment_ids: vec!["second".into(), "first".into()],
            epoch: None,
        });
        let event = |version, state: &str| {
            json!({"machine":"owner", "entityKind":"message", "entityId":"prompt/s/c", "version":version,
            "data":{"sessionId":"s", "state":state,"reason":"provider_unavailable"}})
        };
        assert!(pending.apply_display(&event(2, "held"), "s", "prompt/s/c", Some("owner")));
        assert!(pending.apply_display(&event(3, "failed"), "s", "prompt/s/c", Some("owner")));
        assert_eq!(pending.state, "failed: provider_unavailable");
        assert!(!pending.apply_display(&event(2, "queued"), "s", "prompt/s/c", Some("owner")));
        assert!(!pending.apply_display(&event(4, "next_turn"), "s", "prompt/s/c", Some("other")));
        assert!(!pending.apply_display(
            &event(4, "next_turn"),
            "other",
            "prompt/s/c",
            Some("owner")
        ));
        assert_eq!(pending.text(), "完整 text");
        assert_eq!(pending.request.attachment_ids, ["second", "first"]);
        assert_eq!(pending.request.epoch, None);
        assert_eq!(pending.client_id(), "c");
        assert!(pending.receipt.is_none());
    }
}
