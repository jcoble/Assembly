//! Chat's workspace codec persists only its actual row anchor and following state; its offset field stays 0 until the
//! kit can scroll into a row.
use super::SessionPane;
use crate::core::{
    state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes},
    ui::center_panes::CenterPanes,
};
use gpui_kit::{App, Context, Window};
use serde::{Deserialize, Serialize};
use serde_json::Value;
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct ChatPlace {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub item_key: Option<String>,
    pub offset: f32,
    pub following: bool,
}
pub(super) fn decode(value: &Value) -> Result<ChatPlace, String> {
    if value.is_null() {
        return Ok(ChatPlace {
            item_key: None,
            offset: 0.,
            following: true,
        });
    }
    let place: ChatPlace = serde_json::from_value(value.clone())
        .map_err(|error| format!("Invalid Chat workspace: {error}"))?;
    if !place.offset.is_finite() || place.item_key.as_ref().is_some_and(|key| key.is_empty()) {
        return Err("Invalid Chat reading place.".into());
    }
    Ok(place)
}
pub(super) fn restore_supported(place: &ChatPlace) -> Result<(), String> {
    if !place.following && place.offset != 0. {
        return Err("This UI kit cannot yet restore logical pixel offsets. The current workspace was preserved.".into());
    }
    Ok(())
}
struct ChatCodec;
impl PaneCodec for ChatCodec {
    fn name(&self) -> &'static str {
        "chat"
    }
    fn capture(&self, _: &Window, cx: &App) -> Result<Value, String> {
        let pane = CenterPanes::find::<SessionPane>("Chat", cx)
            .ok_or("The actual Chat pane is unavailable.")?;
        serde_json::to_value(pane.read(cx).timeline.read(cx).capture_place(cx))
            .map_err(|error| error.to_string())
    }
    fn validate(&self, value: &Value, _: &WorkspaceKey, _: &Value, cx: &App) -> Result<(), String> {
        if CenterPanes::find::<SessionPane>("Chat", cx).is_none() {
            return Err("The actual Chat pane is unavailable.".into());
        }
        restore_supported(&decode(value)?)
    }
    fn restore(&self, value: &Value, _: &mut Window, cx: &mut App) {
        let result = decode(value).and_then(|place| {
            let pane = CenterPanes::find::<SessionPane>("Chat", cx)
                .ok_or("The actual Chat pane is unavailable.")?;
            let timeline = pane.read(cx).timeline.clone();
            timeline.update(cx, |timeline, cx| timeline.restore_place(&place, cx))
        });
        if let Err(error) = result {
            WorkspacePanes::refuse_restore(error, cx);
        }
    }
}
impl SessionPane {
    pub fn register_workspace(cx: &mut App) -> Result<(), String> {
        WorkspacePanes::register(Box::new(ChatCodec), cx)
    }
}
impl crate::features::session::view::timeline::Timeline {
    pub(super) fn capture_place(&self, cx: &App) -> ChatPlace {
        self.workspace_place(cx)
    }
    pub(super) fn restore_place(
        &mut self,
        place: &ChatPlace,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        restore_supported(place)?;
        self.restore_workspace_place(place, cx)
    }
}
