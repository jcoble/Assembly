//! Engine helper Stop uses the existing canonical session command, never native ACP task cancellation.
use super::SessionPane;
use crate::core::engine::{
    EngineFeed, EngineRefusal, SessionKey, commands::WindowCommand, engine_items::SessionItem,
};
use crate::features::session::model::item::ItemKind;
use gpui_kit::*;
pub(super) fn command(
    parent: &SessionKey,
    children: &[SessionItem],
    child_id: &str,
) -> Result<WindowCommand, EngineRefusal> {
    let mut matching = children.iter().filter(|child| {
        child.key.id == child_id
            && child.parent_id.as_deref() == Some(parent.id.as_str())
            && child.parent_machine.as_deref() == Some(parent.machine.as_str())
    });
    let child = matching
        .next()
        .filter(|child| child.version >= 0 && child.epoch >= 0)
        .ok_or_else(unavailable)?;
    if matching.next().is_some() {
        return Err(unavailable());
    }
    Ok(WindowCommand {
        command_id: uuid::Uuid::new_v4().to_string(),
        entity_id: child.key.id.clone(),
        expected_version: child.version,
        epoch: child.epoch,
        machine: Some(child.key.machine.clone()),
        surface: None,
        command: serde_json::json!({"kind":"stop_session"}),
    })
}
fn unavailable() -> EngineRefusal {
    EngineRefusal {
        code: "helper_unavailable".into(),
        message: "The helper's current source-qualified session is unavailable.".into(),
    }
}
impl SessionPane {
    pub(super) fn stop_engine_child(&mut self, key: &str, child_id: &str, cx: &mut Context<Self>) {
        let valid=self.view.items.iter().find(|item|item.key==key).is_some_and(|item|matches!(&item.kind,ItemKind::Subagent{subagent,..} if subagent.helper&&subagent.session_id==child_id&&subagent.can_stop&&subagent.state!="idle"));
        let Some(parent) = self
            .row_source
            .as_ref()
            .zip(self.session.as_ref())
            .filter(|_| valid)
            .map(|(source, id)| SessionKey {
                machine: source.machine.clone(),
                id: id.clone(),
            })
        else {
            self.status = unavailable().message.into();
            cx.notify();
            return;
        };
        let feed = EngineFeed::global(cx);
        let children = feed.read(cx).children_of(&parent);
        let command = match command(&parent, &children, child_id) {
            Ok(command) => command,
            Err(error) => {
                self.status = error.message.into();
                cx.notify();
                return;
            }
        };
        let request = command.command_id.clone();
        let call = feed.update(cx, |feed, cx| feed.submit(command, cx));
        let task = cx.spawn({
            let request = request.clone();
            async move |pane, cx| {
                let result = call.await;
                let _ = pane.update(cx, |pane, cx| {
                    pane._writes.remove(&request);
                    if pane.session.as_deref() != Some(parent.id.as_str())
                        || pane
                            .row_source
                            .as_ref()
                            .is_none_or(|source| source.machine != parent.machine)
                    {
                        return;
                    }
                    let error = match result {
                        Ok(receipt) if receipt.applied => None,
                        Ok(receipt) => Some(
                            receipt
                                .error
                                .unwrap_or_else(|| "The helper Stop was refused.".into()),
                        ),
                        Err(error) => Some(error.message),
                    };
                    if let Some(error) = error {
                        pane.status = error.into();
                        cx.notify();
                    }
                    // Only the owner's real pushed card/session state can mark the helper stopped.
                });
            }
        });
        self._writes.insert(request, task);
    }
}
