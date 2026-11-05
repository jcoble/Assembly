//! Typed desktop calls only; all transport and cancellation belong to the existing socket client.
use super::task_export::*;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::*;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

/// A change to one block of a task's page.
pub struct BlockEdit {
    pub block_id: String,
    pub kind: String,
    pub text: String,
    pub checked: Option<bool>,
}

#[derive(Clone, Default)]
pub struct NotionClient;
impl NotionClient {
    fn call<T: DeserializeOwned + Send + 'static>(
        &self,
        method: &'static str,
        args: Value,
        bulk: bool,
        cx: &mut App,
    ) -> Task<Result<T, NotionError>> {
        let socket = if bulk {
            SocketClient::try_bulk(cx)
        } else {
            SocketClient::try_global(cx)
        };
        let call = socket.map(|socket| socket.read(cx).call("notion", method, args, None));
        cx.spawn(async move |_| {
            let Some(call) = call else {
                return Err(NotionError {
                    code: "disconnected".into(),
                    message: "The desktop backend is unavailable.".into(),
                });
            };
            match call.await {
                Outcome::Ok(value) => Ok(value),
                Outcome::Failed(error) => Err(NotionError {
                    code: error.message.clone(),
                    message: error.message,
                }),
                Outcome::Aborted => Err(NotionError {
                    code: "cancelled".into(),
                    message: "The Notion call was cancelled; an edit may need reconciliation."
                        .into(),
                }),
            }
        })
    }
    #[cfg_attr(
        not(test),
        expect(
            dead_code,
            reason = "The task export and image upload calls have no pane yet; only their tests call them."
        )
    )]
    pub fn export_task(
        &self,
        source: NotionTaskRef,
        cx: &mut App,
    ) -> Task<Result<NotionTaskExport, NotionError>> {
        self.call("export", json!(source), true, cx)
    }
    #[cfg_attr(
        not(test),
        expect(
            dead_code,
            reason = "The task export and image upload calls have no pane yet; only their tests call them."
        )
    )]
    pub fn settings(&self, cx: &mut App) -> Task<Result<NotionSettingsView, NotionError>> {
        self.call("settings", json!({}), false, cx)
    }
    pub fn save_settings(
        &self,
        data_source_id: Option<String>,
        token: Option<String>,
        cx: &mut App,
    ) -> Task<Result<NotionSettingsView, NotionError>> {
        self.call(
            "saveSettings",
            json!({"dataSourceId":data_source_id,"token":token}),
            false,
            cx,
        )
    }
    pub fn clear_settings(&self, cx: &mut App) -> Task<Result<NotionSettingsView, NotionError>> {
        self.call("clearSettings", json!({}), false, cx)
    }
    pub fn oauth_begin(&self, cx: &mut App) -> Task<Result<NotionOAuthStart, NotionError>> {
        self.call("oauthBegin", json!({}), false, cx)
    }
    pub fn oauth_claim(
        &self,
        operation_id: String,
        state: String,
        claim_code: String,
        cx: &mut App,
    ) -> Task<Result<NotionSettingsView, NotionError>> {
        self.call(
            "oauthClaim",
            json!({"operationId":operation_id,"state":state,"claimCode":claim_code}),
            false,
            cx,
        )
    }
    pub fn oauth_cancel(
        &self,
        operation_id: String,
        cx: &mut App,
    ) -> Task<Result<NotionEmpty, NotionError>> {
        self.call(
            "oauthCancel",
            json!({"operationId":operation_id}),
            false,
            cx,
        )
    }
    pub fn detail(
        &self,
        source: NotionTaskRef,
        after: Option<String>,
        cx: &mut App,
    ) -> Task<Result<DetailPage, NotionError>> {
        self.call("detail", json!({"source":source,"after":after}), true, cx)
    }
    pub fn list(
        &self,
        query: NotionQuery,
        cx: &mut App,
    ) -> Task<Result<NotionTaskPage, NotionError>> {
        self.call("list", json!(query), false, cx)
    }
    pub fn refresh(
        &self,
        expected_generation: i64,
        cx: &mut App,
    ) -> Task<Result<NotionRefreshReceipt, NotionError>> {
        self.call(
            "refresh",
            json!({"expectedGeneration":expected_generation}),
            false,
            cx,
        )
    }
    pub fn view(&self, cx: &mut App) -> Task<Result<NotionQuery, NotionError>> {
        self.call("view", json!({}), false, cx)
    }
    pub fn save_view(
        &self,
        query: NotionQuery,
        cx: &mut App,
    ) -> Task<Result<NotionQuery, NotionError>> {
        self.call("saveView", json!(query), false, cx)
    }
    pub fn property(
        &self,
        source: NotionTaskRef,
        property: String,
        value: String,
        observed_revision: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.call("property", json!({"source":source,"property":property,"value":value,"observedRevision":observed_revision}), false, cx)
    }
    pub fn block(
        &self,
        source: NotionTaskRef,
        edit: BlockEdit,
        observed_revision: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.call("block", json!({"source":source,"blockId":edit.block_id,"kind":edit.kind,"text":edit.text,"checked":edit.checked,"observedRevision":observed_revision}), false, cx)
    }
    pub fn append_text(
        &self,
        source: NotionTaskRef,
        markdown: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.call(
            "appendText",
            json!({"source":source,"markdown":markdown}),
            false,
            cx,
        )
    }
    pub fn image_begin(
        &self,
        source: NotionTaskRef,
        mime: String,
        size: i64,
        cx: &mut App,
    ) -> Task<Result<NotionImageLease, NotionError>> {
        self.call(
            "imageBegin",
            json!({"source":source,"mime":mime,"size":size}),
            true,
            cx,
        )
    }
    pub fn image_chunk(
        &self,
        upload_id: String,
        offset: i64,
        data: String,
        cx: &mut App,
    ) -> Task<Result<NotionEmpty, NotionError>> {
        self.call(
            "imageChunk",
            json!({"uploadId":upload_id,"offset":offset,"data":data}),
            true,
            cx,
        )
    }
    pub fn image_commit(
        &self,
        upload_id: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.call("imageCommit", json!({"uploadId":upload_id}), true, cx)
    }
    pub fn image_drop(
        &self,
        upload_id: String,
        cx: &mut App,
    ) -> Task<Result<NotionEmpty, NotionError>> {
        self.call("imageDrop", json!({"uploadId":upload_id}), true, cx)
    }
}
