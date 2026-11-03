//! The existing bulk socket owns all requests. A read captures one socket generation;
//! opaque entity continuations never become stream cursors or acknowledgments.
use super::{EngineKey, EngineRefusal};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::HashSet;

#[derive(Deserialize)]
struct EntityPage {
    items: Vec<Value>,
    next: Option<String>,
}

pub(super) async fn read(
    refs: &[EngineKey],
    cx: &mut AsyncApp,
) -> Result<Vec<Value>, EngineRefusal> {
    if refs.is_empty() {
        return Ok(Vec::new());
    }
    let (socket, generation) = cx.update(|cx| {
        let socket = SocketClient::try_bulk(cx)
            .ok_or_else(|| connection_changed("Bulk connection is not available."))?;
        let generation = socket
            .read(cx)
            .connection_generation()
            .ok_or_else(|| connection_changed("Bulk connection is not ready."))?;
        Ok::<_, EngineRefusal>((socket, generation))
    })?;
    let mut rows = Vec::new();
    for chunk in refs.chunks(200) {
        let source = &chunk[0].machine;
        if chunk.iter().any(|key| key.machine != *source) {
            return Err(refused("Mixed entity sources."));
        }
        let references: Vec<_> = chunk
            .iter()
            .map(|key| json!({"kind":key.kind,"id":key.id}))
            .collect();
        let mut after: Option<String> = None;
        let mut seen = HashSet::new();
        let mut count = 0;
        loop {
            let call = cx.update(|cx| {
                if socket.read(cx).connection_generation() != Some(generation) {
                    return Err(connection_changed("Bulk connection was replaced."));
                }
                Ok(socket.read(cx).call::<EntityPage>(
                    "engine",
                    "entities",
                    json!({"refs":references,"after":after,"machine":source}),
                    None,
                ))
            })?;
            let page = match call.await {
                Outcome::Ok(page) => page,
                Outcome::Aborted => return Err(connection_changed("Entity read was cancelled.")),
                Outcome::Failed(error) => return Err(refused(&error.message)),
            };
            if cx.update(|cx| socket.read(cx).connection_generation()) != Some(generation) {
                return Err(connection_changed("Bulk connection was replaced."));
            }
            count += 1;
            if count > chunk.len() + 1
                || page.items.len() > chunk.len()
                || rows.len() + page.items.len() > refs.len()
            {
                return Err(refused("Entity page exceeded the requested bounds."));
            }
            rows.extend(page.items);
            match page.next {
                Some(next) if seen.insert(next.clone()) => after = Some(next),
                Some(_) => return Err(refused("Entity continuation did not progress.")),
                None => break,
            }
        }
    }
    Ok(rows)
}
fn refused(message: &str) -> EngineRefusal {
    EngineRefusal {
        code: "entity_read_unavailable".into(),
        message: message.into(),
    }
}

fn connection_changed(message: &str) -> EngineRefusal {
    EngineRefusal {
        code: "entity_connection_changed".into(),
        message: message.into(),
    }
}
