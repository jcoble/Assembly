//! The Connections form against a fake backend: an edit follows its machine as the `machines` topic changes.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::{Arc, Mutex};

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Entity, Subscription, VisualTestContext, px, size};
use serde_json::{Value, json};

use super::SettingsPage;
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::state::machines::Machines;

fn boxed(reachable: bool) -> Value {
    json!({"name": "box", "kind": "remote", "reachable": reachable, "reason": null, "checked": true,
        "ssh": "host-a", "port": 9797, "tokenFile": ".config/assembly-dotnet/env"})
}

/// A backend with this Mac and "box" that never answers `machines/set` by itself. The test's own calls make it:
/// `test/fail` fails the waiting save, `test/flip` sends box as not answering, `test/remove` sends box removed.
fn backend() -> FakeBackend {
    let held = Arc::new(Mutex::new((Value::Null, Value::Null)));
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let mut held = held.lock().unwrap();
        let (sub, set) = &mut *held;
        let id = frame["id"].clone();
        let change =
            |item| Reply::Frame(json!({"t": "change", "sub": sub, "key": "box", "item": item}));
        let done = Reply::Frame(json!({"t": "result", "id": id, "result": {}}));
        match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => {
                *sub = id.clone();
                let local = json!({"name": null, "kind": "local", "reachable": true, "reason": null, "checked": true});
                vec![Reply::Frame(json!({"t": "state", "sub": id, "items": [
                    {"key": "local", "item": local}, {"key": "box", "item": boxed(true)}]}))]
            }
            (Some("call"), Some("set")) => {
                *set = id;
                Vec::new()
            }
            (Some("call"), Some("fail")) => vec![
                Reply::Frame(
                    json!({"t": "error", "id": set, "code": "failed", "message": "disk full"}),
                ),
                done,
            ],
            (Some("call"), Some("flip")) => vec![change(boxed(false)), done],
            (Some("call"), Some("remove")) => vec![change(Value::Null), done],
            _ => Vec::new(),
        }
    })
}

struct Page<'a> {
    cx: &'a mut VisualTestContext,
    page: Entity<SettingsPage>,
    changes: UnboundedReceiver<()>,
    changed: UnboundedSender<()>,
    _heard: Subscription,
}

/// The Settings page on Connections, once the machines list has box, and editing box.
async fn editing_box<'a>(cx: &'a mut TestAppContext, backend: &FakeBackend) -> Page<'a> {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint, cx);
        Machines::start(cx);
    });
    let slot = Rc::new(RefCell::new(None));
    let made = slot.clone();
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let page = cx.new(|cx| SettingsPage::new(window, cx));
        made.replace(Some(page.clone()));
        gpui_kit::base::Root::new(page, window, cx)
    });
    cx.simulate_resize(size(px(1200.), px(900.)));
    let page: Entity<SettingsPage> = slot.take().expect("the page");
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let heard = cx.update(|_, cx| cx.observe(&page, move |_, _| _ = tell.unbounded_send(())));
    until("box listed", &mut changes, &changed, || {
        cx.update(|_, cx| {
            Machines::try_global(cx)
                .is_some_and(|machines| machines.read(cx).machines().count() == 2)
        })
    })
    .await;
    let machine = cx.update(|_, cx| {
        let machines = Machines::try_global(cx).expect("the machines list");
        machines.read(cx).machines().nth(1).cloned().expect("box")
    });
    cx.update(|window, cx| {
        page.update(cx, |page, cx| {
            page.chosen = "connections";
            page.edit_machine(&machine, window, cx);
        })
    });
    draw(cx);
    Page {
        cx,
        page,
        changes,
        changed,
        _heard: heard,
    }
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

impl Page<'_> {
    /// Types `text` into the SSH destination field, as a person would.
    fn type_ssh(&mut self, text: &str) {
        let ssh = self
            .cx
            .update(|_, cx| self.page.read(cx).machine_fields[1].clone());
        self.cx
            .update(|window, cx| ssh.update(cx, |ssh, cx| ssh.focus(window, cx)));
        draw(self.cx);
        self.cx.simulate_input(text);
        draw(self.cx);
    }

    fn ssh(&mut self) -> String {
        self.cx.update(|_, cx| {
            self.page.read(cx).machine_fields[1]
                .read(cx)
                .value()
                .to_string()
        })
    }

    fn edited(&mut self) -> Option<String> {
        self.cx.update(|_, cx| {
            let edits = &self.page.read(cx).machine_edits;
            edits
                .editing
                .as_ref()
                .and_then(|machine| machine.name.clone())
        })
    }

    fn failure(&mut self) -> Option<String> {
        self.cx
            .update(|_, cx| (self.page.read(cx).machine_edits.failure.clone()).map(|(_, why)| why))
    }

    /// Makes the backend do `method` (see `backend`) and waits until `done` holds.
    async fn backend_does(
        &mut self,
        method: &str,
        what: &str,
        mut done: impl FnMut(&mut Self) -> bool,
    ) {
        let call = self.cx.update(|_, cx| {
            SocketClient::global(cx)
                .read(cx)
                .call::<Value>("test", method, json!({}), None)
        });
        _ = call.await;
        // The wait reads the page between changes, so it holds the receiver apart from the page meanwhile.
        let (mut changes, changed) = (
            std::mem::replace(&mut self.changes, unbounded().1),
            self.changed.clone(),
        );
        until(what, &mut changes, &changed, || done(self)).await;
        self.changes = changes;
        draw(self.cx);
    }
}

// box stops answering mid-edit: what was typed stays. box removed elsewhere: the edit ends and says so, and the form
// is empty, so Save can't bring box back.
#[gpui::test]
async fn an_edit_keeps_its_typing_as_its_machine_answers_and_ends_when_it_is_removed(
    cx: &mut TestAppContext,
) {
    let backend = backend();
    let mut page = editing_box(cx, &backend).await;
    page.type_ssh("-b");

    page.backend_does("flip", "box not answering", |page| {
        page.cx.update(|_, cx| {
            Machines::try_global(cx)
                .is_some_and(|machines| machines.read(cx).machines().any(|held| !held.reachable))
        })
    })
    .await;
    assert_eq!(page.ssh(), "host-a-b");
    assert_eq!(page.edited().as_deref(), Some("box"));

    page.backend_does("remove", "the edit ended", |page| page.edited().is_none())
        .await;
    assert_eq!(page.ssh(), "");
    assert_eq!(
        page.failure().as_deref(),
        Some("Edit ended: box was removed, so nothing was saved.")
    );
}
