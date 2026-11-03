//! `Composer` owns the draft (a multi-line kit input: Enter sends, Shift-Enter adds a line) and each session's unsent
//! draft while the window runs. Send is `send/send` with the draft and a clientId, which a retry of the same text
//! reuses. While a turn runs, a send steers it and a Stop button beside the draft stops it (`send/stop` with the run
//! id). The ring beside Send shows how much of the agent's context window is left (context_ring.rs), and the agent's
//! own menus and "/" commands come from its config (composer_config.rs). Images attach by drop, paste or the picker
//! and upload at once (composer_attach.rs); a send waits until each has uploaded. A prompt stays pending until its
//! own pushed user row arrives. Drawn in composer_render.rs. Replaces Composer.svelte.

use std::collections::HashMap;
use std::rc::Rc;

use gpui_kit::component::input::{InputEvent, TextareaState};
use gpui_kit::*;
use serde_json::json;

use super::agent_config::AgentConfig;
use super::attachment::{Attachment, AttachmentState};
use super::context_meter::ContextMeter;
use crate::features::session::client::session_client::{CallError, SessionClient};

pub struct Composer {
    client: Rc<dyn SessionClient>,
    start_draft: Option<start::StartDraft>,
    initial_upload:Option<std::rc::Weak<()>>,
    session: Option<String>,
    session_key: Option<crate::core::engine::SessionKey>,
    expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    source_valid: bool,
    source_serial:u64,
    /// Identifies this visit, including leaving and returning to the same session.
    target_generation: u64,
    owner: Option<(String, Option<String>, String)>,
    advertised_target: Option<String>,
    backend_connected: bool,
    _target: Option<Task<()>>,
    _warm: Option<Task<()>>,
    _connection: Option<Subscription>,
    draft: Entity<TextareaState>,
    /// The other sessions' unsent drafts, by session id.
    drafts: HashMap<crate::core::engine::SessionKey, (String,Vec<Attachment>)>,
    attachments: Vec<Attachment>,
    /// Each attachment's upload while it goes, by its id; a chip removed drops its upload.
    uploads: HashMap<u64, Task<()>>,
    /// How many attachments this composer has named, for the next id.
    attached: u64,
    /// The "/" menu's highlighted row, or `None` while it's closed or Esc hid it.
    slash_selected: Option<usize>,
    /// Keeps the highlighted row in sight as the keys move it.
    slash_scroll: ScrollHandle,
    /// The agent's menus and commands for this session, once read.
    config: Option<AgentConfig>,
    /// Why the last pick failed, above the box until the next works or the session changes.
    error: Option<SharedString>,
    /// The run going now, which Stop stops.
    running: Option<String>,
    /// The open session's machine while it isn't answering: Send and Stop wait for it.
    offline: Option<String>,
    /// Connect's `machines/connect` while it goes.
    connecting: Option<Task<()>>,
    /// Connect answered and the machine still didn't, until it answers or the session's machine changes.
    still_offline: bool,
    /// How full the agent's context window is, from the session's newest report.
    meter: Option<ContextMeter>,
    /// The branch the session's newest turn started on, shown in the footer; `None` before its first turn.
    branch: Option<String>,
    /// The open session has messages: the draft's hint asks for follow-up changes.
    messages: bool,
    /// The draft's hint as last set (`placeholder`).
    hint: &'static str,
    /// A failed send's immutable request: a retry reuses its original receipt identity.
    retry_ids: HashMap<crate::core::engine::SessionKey, PromptRequest>,
    pending: HashMap<crate::core::engine::SessionKey, Vec<PendingPrompt>>,
    _pending_read: Option<Task<()>>,
    message_subscriptions: HashMap<String, Subscription>,
    machine: Option<String>,
    failed_drafts: HashMap<crate::core::engine::SessionKey, (String, Vec<Attachment>)>,
    delivered: HashMap<crate::core::engine::SessionKey, std::collections::HashSet<String>>,
    /// How many sends this composer has named, for the next clientId.
    named: u64,
    /// Counts each pick's start and answer; a config read that saw it change answers what came before.
    picks: u64,
    _enter: Subscription,
    /// The config read going now.
    _config: Option<Task<()>>,
    /// Sends and Stops still going; finished ones are let go at the next.
    _tasks: Vec<Task<()>>,
}

/// What the composer tells the session pane.
pub enum ComposerEvent {
    /// Local draft intent; the form owns creation and checkout, and the draft remains intact.
    StartRequested { text:String, images:Vec<StagedAttachment> },
    /// A local pending prompt was captured before the call.
    Sending,
    /// The owner committed the prompt. Its provider turn arrives later through rows.
    Accepted { key:crate::core::engine::SessionKey,receipt:OwnerPromptAccepted },
    PendingChanged,
}
#[path = "owner_prompt.rs"]
mod owner_prompt;
pub use owner_prompt::OwnerPromptAccepted;
use owner_prompt::{PendingPrompt, PromptRequest};

/// The draft's hint: "Message" for a new session's first message, the mock's words once it has messages.
pub fn placeholder(messages: bool) -> &'static str {
    if messages {
        "Ask for follow-up changes…"
    } else {
        "Message"
    }
}

impl Composer {
    pub fn new(client: Rc<dyn SessionClient>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let draft = cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(1, DRAFT_ROWS)
                .submit_on_enter(true)
                .placeholder(placeholder(false))
        });
        let enter = cx.subscribe_in(
            &draft,
            window,
            |composer, _, event, window, cx| match event {
                InputEvent::PressEnter { shift: false, .. } => composer.send(window, cx),
                InputEvent::Change => composer.draft_changed(cx),
                // The box's edge shows only while the draft has focus.
                InputEvent::Focus | InputEvent::Blur => cx.notify(),
                _ => {}
            },
        );
        let backend_connected = crate::core::socket::client::SocketClient::try_global(cx)
            .map(|socket| {
                matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                )
            })
            .unwrap_or(true);
        let connection = crate::core::socket::client::SocketClient::try_global(cx).map(|socket| {
            cx.observe(&socket, |composer: &mut Self, socket, cx| {
                let connected = matches!(
                    socket.read(cx).state(),
                    crate::core::socket::connection_state::ConnectionState::Connected { .. }
                );
                composer.connection_changed(connected, cx);
            })
        });
        Composer {
            client,
            start_draft:None,initial_upload:None,
            session: None,
            session_key:None,expected_source:None,source_valid:false,source_serial:0,
            target_generation: 0,
            owner: None,
            advertised_target: None,
            backend_connected,
            _target: None,
            _warm: None,
            _connection: connection,
            draft,
            drafts: HashMap::new(),
            attachments: Vec::new(),
            uploads: HashMap::new(),
            attached: 0,
            slash_selected: None,
            slash_scroll: ScrollHandle::new(),
            config: None,
            error: None,
            running: None,
            offline: None,
            connecting: None,
            still_offline: false,
            meter: None,
            branch: None,
            messages: false,
            hint: placeholder(false),
            retry_ids: HashMap::new(),
            pending: HashMap::new(),
            _pending_read: None,
            message_subscriptions: HashMap::new(),
            machine: None,
            failed_drafts: HashMap::new(),
            delivered: HashMap::new(),
            named: 0,
            picks: 0,
            _enter: enter,
            _config: None,
            _tasks: Vec::new(),
        }
    }

    /// The run going now; when one ends, the agent's menus are read again.
    pub fn set_running(&mut self, run: Option<String>, cx: &mut Context<Self>) {
        if self.running != run {
            let ended = run.is_none();
            self.running = run;
            if ended {
                self.read_config(cx);
            }
            cx.notify();
        }
    }

    /// The newest context report as (used, size), or `None` before the agent sends one.
    pub fn set_context(&mut self, context: Option<(i64, i64)>, cx: &mut Context<Self>) {
        let meter = context.and_then(|(used, size)| ContextMeter::of(used, size));
        if self.meter != meter {
            self.meter = meter;
            cx.notify();
        }
    }

    /// The branch the newest turn started on (model/turn_line.rs `latest_branch`).
    pub fn set_branch(&mut self, branch: Option<String>, cx: &mut Context<Self>) {
        if self.branch != branch {
            self.branch = branch;
            cx.notify();
        }
    }

    /// Whether the open session has messages, for the draft's hint (`placeholder`).
    pub fn set_messages(&mut self, messages: bool, cx: &mut Context<Self>) {
        if self.messages != messages {
            self.messages = messages;
            cx.notify();
        }
    }

    #[cfg(test)]
    pub fn meter(&self) -> Option<&ContextMeter> {
        self.meter.as_ref()
    }

    /// Every attached image has uploaded.
    fn all_saved(&self) -> bool {
        (self.attachments.iter())
            .all(|attachment| matches!(attachment.state, AttachmentState::Saved { .. }))
    }

    /// The open session's machine while it isn't answering, from the machines list; `None` once it answers.
    pub fn set_offline(&mut self, machine: Option<String>, cx: &mut Context<Self>) {
        if self.offline != machine {
            self.offline = machine;
            (self.connecting, self.still_offline) = (None, false);
            self.refresh_config_target(cx);
            cx.notify();
        }
    }

    /// Connect: asks the backend to reach the offline machine now (`machines/connect`, up to 10 seconds). One that
    /// answers comes back through the machines list; one that doesn't says so.
    fn connect(&mut self, cx: &mut Context<Self>) {
        let Some(machine) = self.offline.clone().filter(|_| self.connecting.is_none()) else {
            return;
        };
        let call = (self.client).call(
            "machines",
            "connect",
            json!({ "machine": machine }),
            None,
            cx,
        );
        self.connecting = Some(cx.spawn(async move |composer, cx| {
            _ = call.await;
            _ = composer.update(cx, |composer, cx| {
                composer.connecting = None;
                composer.still_offline = composer.offline.is_some();
                cx.notify();
            });
        }));
        cx.notify();
    }

    /// Stops the run going now. It goes on if the call fails, so Stop stays to be pressed again.
    fn stop(&mut self, cx: &mut Context<Self>) {
        if !self.operational(){return;}
        let Some(run) = self.running.clone().filter(|_| self.offline.is_none()) else {
            return;
        };
        let Some(session)=self.session.clone()else{return;};
        let source_serial=self.source_serial;
        let args=self.source_args(json!({"runId":run,"session":session}));
        let call=self.client.call("send","stop",args,None,cx);
        self._tasks.retain(|task| !task.is_ready());
        self._tasks.push(cx.spawn(async move |composer,cx| {
            if let Err(error)=call.await {let _=composer.update(cx,|composer,cx| {
                if composer.source_serial==source_serial {composer.source_refused(&error,cx);composer.error=Some(error.reason().into());cx.notify();}
            });}
        }));
    }


}

/// The most lines the draft grows to before it scrolls.
const DRAFT_ROWS: usize = 10;

impl EventEmitter<ComposerEvent> for Composer {}

#[path = "composer_attach.rs"]
mod attach;
#[path = "composer_config.rs"]
mod config;
#[path = "composer_render.rs"]
mod render;
#[path = "composer_target.rs"]
mod target;

#[cfg(test)]
#[path = "composer_view_tests.rs"]
mod tests;

#[path="composer_selection.rs"]
mod selection;
#[path="composer_send.rs"]
mod send;
#[cfg(test)]
#[path="qualified_tests.rs"]
mod qualified_tests;

#[path="composer_start.rs"]
mod start;
pub use start::StagedAttachment;
#[cfg(test)]
#[path="tests/start_tests.rs"]
mod start_tests;

#[path="composer_initial.rs"]
mod initial;
