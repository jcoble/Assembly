use super::PrSection;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::features::source_control::git_action::GitWrites;
use crate::features::source_control::git_target::GitTarget;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::{Entity, Modifiers};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

const NO_GH: &str = "GitHub CLI (gh) isn't installed on this machine. Install it and run gh auth login.";

struct Open<'a> {
    section: Entity<PrSection>,
    cx: &'a mut VisualTestContext,
    reads: Arc<Mutex<usize>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: gpui_kit::Subscription,
}
/// A section on a session whose Pull requests read answers `answer`, given which read this is.
fn open(cx: &mut TestAppContext, answer: impl Fn(usize, &Value) -> Value + Send + Sync + 'static) -> Open<'_> {
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let reads = Arc::new(Mutex::new(0usize));
    let counted = reads.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let reply = if frame["method"] == "prs" {
            let mut reads = counted.lock().unwrap();
            *reads += 1;
            answer(*reads, frame)
        } else {
            json!({"t":"result","id":frame["id"],"result":{}})
        };
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(reply)]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let (section, visual) = cx.add_window_view(|_, cx| {
        let writes = GitWrites::new(cx);
        PrSection::new(GitTarget::new("s1"), writes, true, cx)
    });
    let tell = changed.clone();
    let heard = visual.update(|_, cx| {
        cx.observe(&section, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    Open {
        section,
        cx: visual,
        reads,
        changed,
        changes,
        _backend: backend,
        _heard: heard,
    }
}
fn page(frame: &Value) -> Value {
    json!({"t":"result","id":frame["id"],"result":{"items":[],"cursor":null,"total":0}})
}
impl Open<'_> {
    /// Everything sent before this has reached the backend by the time it answers.
    async fn barrier(&mut self) {
        let call = self.cx.update(|_, cx| {
            SocketClient::global(cx)
                .read(cx)
                .call::<Value>("test", "test_barrier", json!({}), None)
        });
        let _ = call.await;
        self.cx.run_until_parked();
    }
    fn drawn(&mut self) {
        self.cx.run_until_parked();
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
    }
    async fn settled(&mut self) {
        let (section, cx) = (self.section.clone(), &mut *self.cx);
        until("the read to answer", &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            cx.update(|_, cx| !section.read(cx).queue.read(cx).loading)
        })
        .await;
    }
}
// Pins: g9#12
#[gpui::test]
async fn the_section_reads_only_when_expanded(cx: &mut TestAppContext) {
    let mut open = open(cx, |_, frame| page(frame));
    open.barrier().await;
    assert_eq!(*open.reads.lock().unwrap(), 0, "folded, it reads nothing");
    let section = open.section.clone();
    section.update(open.cx, |section, cx| section.toggle(cx));
    open.barrier().await;
    open.settled().await;
    assert_eq!(*open.reads.lock().unwrap(), 1);
    section.update(open.cx, |section, cx| section.toggle(cx));
    section.update(open.cx, |section, cx| section.toggle(cx));
    open.barrier().await;
    assert_eq!(*open.reads.lock().unwrap(), 1, "opening it again keeps what it read");
}
// Pins: g9#12, g9#14
#[gpui::test]
async fn missing_gh_shows_how_to_install(cx: &mut TestAppContext) {
    let mut open = open(cx, |read, frame| {
        if read == 1 {
            json!({"t":"error","id":frame["id"],"code":"git_failed","message":NO_GH,"machine":"machine"})
        } else {
            page(frame)
        }
    });
    let section = open.section.clone();
    section.update(open.cx, |section, cx| section.toggle(cx));
    open.barrier().await;
    open.settled().await;
    let failure = open
        .cx
        .update(|_, cx| section.read(cx).queue.read(cx).failure.clone());
    assert_eq!(failure.as_deref(), Some(NO_GH));
    open.drawn();
    let retry = open
        .cx
        .debug_bounds("pr-section-retry")
        .expect("the failure stays with Try again");
    open.cx.simulate_click(retry.center(), Modifiers::none());
    open.barrier().await;
    open.settled().await;
    assert_eq!(*open.reads.lock().unwrap(), 2);
    open.drawn();
    assert!(open.cx.debug_bounds("pr-section-retry").is_none());
    let failure = open
        .cx
        .update(|_, cx| section.read(cx).queue.read(cx).failure.clone());
    assert_eq!(failure, None);
}
