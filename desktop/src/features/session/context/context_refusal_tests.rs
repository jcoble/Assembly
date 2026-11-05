//! A thumbnail's read refused because the owner was replaced tells Chat, which stops reading that session. Declared
//! from session_pane.rs so it can see the session pane's source and status.

use std::rc::Rc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::component::dock::{BasePanel as _, DockArea, DockPlacement, PanelId, panel_handle};
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};
use serde_json::json;

use super::SessionPane;
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;
use crate::features::session::context::context_pane::ContextPane;

/// Holds the window's dock, as the shell's frame does.
struct Host;

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

#[gpui::test]
fn a_thumbnails_changed_source_tells_chat(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    let source = ExpectedRowSource {
        machine: "a".into(),
        generation: 3,
        route_machine: None,
    };
    let chat_client = FakeClient::default();
    let (_dock, chat) = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("context-refusal", None, window, cx));
        let chat = cx.new(|cx| SessionPane::new(Rc::new(chat_client.clone()), window, cx));
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(
                panel_handle(chat.clone()),
                DockPlacement::Center,
                None,
                window,
                cx,
            )
        });
        CenterPanes::init(&dock, cx);
        CenterPanes::remember("Chat", PanelId::from(chat.entity_id()), cx);
        (dock, chat)
    });
    chat.update(cx, |chat, _| {
        chat.session = Some("s1".into());
        chat.row_source = Some(source.clone());
    });
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key, source, false, cx)
        })
    });

    let client = FakeClient::default();
    let pane = cx.update(|_, cx| {
        let fake = client.clone();
        cx.new(|cx| ContextPane::with_thumbnails(Rc::new(fake), cx))
    });
    let png = {
        let mut bytes = Vec::new();
        image::RgbaImage::new(4, 4)
            .write_to(
                &mut std::io::Cursor::new(&mut bytes),
                image::ImageFormat::Png,
            )
            .unwrap();
        bytes
    };
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(json!({ "prompts": [
        "again\nAttached image: /h/attachments/s1/b0.png (new.png)",
        "look\nAttached image: /h/attachments/s1/9f.png (shot.png)",
    ] })));
    client.queue_answer(Ok(json!({})));
    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Owner replaced".into(),
    }));
    client.queue_answer(Ok(
        json!({ "data": STANDARD.encode(&png), "size": png.len(), "mime": "image/png" }),
    ));
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    cx.run_until_parked();

    assert_eq!(
        chat.read_with(cx, |chat, _| chat.status.clone()),
        "The Context session source changed."
    );
    let reads = client
        .calls()
        .into_iter()
        .filter(|(call, _)| call == "attachments/read")
        .count();
    assert_eq!(reads, 1, "the queue stopped");
}
