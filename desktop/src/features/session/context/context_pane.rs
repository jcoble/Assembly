//! `ContextPane` owns the drawer's Context tab, four cards in a scrolling column: the selected session's agent settings
//! and its context usage (session_cards.rs), the files its calls and prompts named, newest first, each with a count
//! and its lines added and removed (files_touched.rs), and the images its prompts carried (attachments_section.rs). Replaces SessionContextPanel.svelte.
//! Like it, the tab reads the lists (`rows/files`, `rows/attachments`) and the session's total (`rows/tokens`) when
//! it's shown, and again when another session is picked while it's shown, and doesn't follow the live rows: Chat
//! already does, and nobody watches these lists while they change. The two cards above follow the session's live facts
//! (live_facts.rs), which read nothing, except that a turn ending while shown reads the total again. The Attachments
//! card lists the draft's images from the same facts, and a prompt sent while shown reads `rows/attachments` again.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::rc::Rc;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::context::live_facts::LiveFacts;
use listing::Listing;

/// The layout's name for the tab (default_layout.rs).
const CONTEXT: &str = "Context";

/// One file, as `rows/files` sends it (RowsFeature.cs).
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct FileTouched {
    pub path: String,
    pub added: i64,
    pub removed: i64,
    /// How many calls and prompts named it; an older remote doesn't send it.
    #[serde(default)]
    pub count: i64,
    /// The name an attached image was attached with; only those have one.
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Clone, PartialEq)]
struct ContextScope {
    session: String,
    key: Option<crate::core::engine::SessionKey>,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    valid: bool,
}

impl ContextScope {
    /// The same session from the same source, whether or not a refusal has since marked it invalid.
    fn same(&self, other: &Self) -> bool {
        (&self.session, &self.key, &self.source) == (&other.session, &other.key, &other.source)
    }

    fn args(&self) -> serde_json::Value {
        let mut args = json!({"session": self.session});
        if let Some(source) = &self.source {
            args["expectedSource"] = json!(source);
        }
        args
    }
}

pub struct ContextPane {
    context_scope: Option<ContextScope>,
    read_serial: u64,
    focus: FocusHandle,
    client: Rc<dyn SessionClient>,
    /// The kit shows this tab; only then does a pick read.
    active: bool,
    files: Listing<FileTouched>,
    /// Each attached image's (path, name), newest prompt first.
    attachments: Listing<(String, String)>,
    /// The machine that has a remote session's images, from the same read.
    machine: Option<String>,
    /// The read left out older prompts' images.
    more: bool,
    /// Why the last image clicked didn't open.
    open_failed: Option<String>,
    /// The session's total once `rows/tokens` has answered: `None` until then, `Some(None)` when it wasn't reported.
    total: Option<Option<i64>>,
    /// The newest ended turn's run when the total was read, so another turn ending while shown reads it again. `None`
    /// until the session's facts are first seen after a read.
    ended_read: Option<Option<String>>,
    /// The newest prompt's seq when the attachments were read, so a prompt sent while shown reads them again. `None`
    /// until the session's facts are first seen after a read.
    prompt_read: Option<Option<i64>>,
    /// Counts the attachments' reads, so only the newest one's answer is kept.
    attachments_serial: u64,
    _selected: Subscription,
    /// Follows the selected session's live facts, where they've been set up.
    _facts: Option<Subscription>,
    /// The reads going now; dropping it cancels them.
    _read: Option<Task<()>>,
    /// The total's read, apart from the lists so its failure can't disturb them.
    _tokens: Option<Task<()>>,
    /// The attachments' own read after a prompt was sent, which leaves the files list alone.
    _attachments: Option<Task<()>>,
    /// The image opening now.
    _open: Option<Task<()>>,
}

impl ContextPane {
    /// Registers the tab under the layout's Context name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, CONTEXT, |_, _, cx| {
            panel_handle(cx.new(|cx| ContextPane::new(Rc::new(SocketSessionClient), cx)))
        });
    }

    pub fn new(client: Rc<dyn SessionClient>, cx: &mut Context<Self>) -> Self {
        Self {
            context_scope: None,
            read_serial: 0,
            focus: cx.focus_handle(),
            client,
            active: false,
            files: Listing::Reading,
            attachments: Listing::Reading,
            machine: None,
            more: false,
            open_failed: None,
            total: None,
            ended_read: None,
            prompt_read: None,
            attachments_serial: 0,
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.read(cx)),
            _facts: LiveFacts::try_global(cx)
                .map(|facts| cx.observe(&facts, |pane, _, cx| pane.facts_changed(cx))),
            _read: None,
            _tokens: None,
            _attachments: None,
            _open: None,
        }
    }

    /// Reads the selected session's files and attachments, if the tab is shown. A hidden tab drops the last session's
    /// lists, so the frame before it's shown again never draws them.
    fn read(&mut self, cx: &mut Context<Self>) {
        self.read_serial += 1;
        let serial = self.read_serial;
        self.context_scope = None;
        (
            self.files,
            self.attachments,
            self.machine,
            self.more,
            self.open_failed,
        ) = (Listing::Reading, Listing::Reading, None, false, None);
        (self.total, self.ended_read, self.prompt_read) = (None, None, None);
        (self._read, self._open, self._tokens, self._attachments) = (None, None, None, None);
        self.attachments_serial += 1;
        let attachments_serial = self.attachments_serial;
        cx.notify();
        if !self.active {
            return;
        }
        let selected = SelectedSession::global(cx);
        let selected = selected.read(cx);
        let Some(session) = selected.session_id().map(str::to_owned) else {
            return;
        };
        let scope = ContextScope {
            session: session.clone(),
            key: selected.key().cloned(),
            source: selected.source().cloned(),
            valid: true,
        };
        if (scope.key.is_some() && scope.source.is_none())
            || scope.source.as_ref().is_some_and(|source| {
                scope
                    .key
                    .as_ref()
                    .is_none_or(|key| key.machine != source.machine || key.id != session)
            })
        {
            self.files = Listing::Failed("The selected session source is unavailable.".into());
            self.attachments =
                Listing::Failed("The selected session source is unavailable.".into());
            // Nothing will read the total, so it reads as answered with none rather than "Reading…" for good.
            self.total = Some(None);
            return;
        }
        self.context_scope = Some(scope.clone());
        let args = scope.args();
        let files = self
            .client
            .call("rows", "files", args.clone(), Some("rows/files"), cx);
        let attachments =
            self.client
                .call("rows", "attachments", args, Some("rows/attachments"), cx);
        let tokens_scope = scope.clone();
        self._read = Some(cx.spawn(async move |pane, cx| {
            let files = files.await;
            let attachments = attachments.await;
            let refused=matches!(&files,Err(CallError::Refused{code,..}) if code=="source_changed")||matches!(&attachments,Err(CallError::Refused{code,..}) if code=="source_changed");
            pane.update(cx, |pane, cx| {
                if !pane.current(serial, &scope) {
                    return;
                }
                if refused {
                    pane.refuse_source(&scope, cx);
                }
                pane.files = touched(files);
                // A prompt sent since asked for the attachments again, and that newer answer wins.
                if pane.attachments_serial == attachments_serial {
                    pane.take_attachments(attachments);
                }
                cx.notify();
            })
            .ok();
        }));
        let live = self.live(cx);
        self.ended_read = live.as_ref().map(|facts| facts.newest_ended_run.clone());
        self.prompt_read = live.map(|facts| facts.newest_prompt_seq);
        self.read_tokens(tokens_scope, cx);
    }

    /// Whether an answer from read `serial` of `scope` is still wanted.
    fn current(&self, serial: u64, scope: &ContextScope) -> bool {
        self.read_serial == serial
            && self
                .context_scope
                .as_ref()
                .is_some_and(|held| held.same(scope))
    }

    /// The owner was replaced: nothing more is read or opened from this source.
    fn refuse_source(&mut self, scope: &ContextScope, cx: &mut Context<Self>) {
        if let Some(held) = self.context_scope.as_mut() {
            held.valid = false;
        }
        if let Some(source) = &scope.source {
            crate::features::session::view::session_pane::SessionPane::refuse_image_source(
                &scope.session,
                source,
                "The Context session source changed.".into(),
                cx,
            );
        }
    }

    /// Reads the session's total in its own task. Any failure, an older remote's not found included, reads as
    /// answered with nothing, so the card says it wasn't reported; a changed source is refused as `read` does.
    fn read_tokens(&mut self, scope: ContextScope, cx: &mut Context<Self>) {
        let serial = self.read_serial;
        let tokens = self
            .client
            .call("rows", "tokens", scope.args(), Some("rows/tokens"), cx);
        self._tokens = Some(cx.spawn(async move |pane, cx| {
            let answer = tokens.await;
            pane.update(cx, |pane, cx| {
                if !pane.current(serial, &scope) {
                    return;
                }
                if matches!(&answer, Err(CallError::Refused { code, .. }) if code == "source_changed") {
                    pane.refuse_source(&scope, cx);
                }
                pane.total = Some(answer.ok().and_then(|answer| answer["total"].as_i64()));
                cx.notify();
            })
            .ok();
        }));
    }

    /// The selected session's live facts, if the value held is that session's from the same owner.
    fn live(&self, cx: &App) -> Option<LiveFacts> {
        let facts = LiveFacts::try_global(cx)?;
        let selected = SelectedSession::global(cx).read(cx);
        (facts.read(cx).as_ref())
            .filter(|facts| {
                selected.session_id() == Some(facts.session.as_str())
                    && selected.key() == facts.key.as_ref()
            })
            .cloned()
    }

    /// Redraws the cards; a turn that ended while shown reads the total again, and a prompt sent while shown reads the
    /// attachments again.
    fn facts_changed(&mut self, cx: &mut Context<Self>) {
        cx.notify();
        let Some(scope) = self.context_scope.clone().filter(|scope| scope.valid) else {
            return;
        };
        let Some(facts) = self.live(cx) else {
            return;
        };
        let ended = facts.newest_ended_run;
        if matches!(self.ended_read.replace(ended.clone()), Some(read) if read != ended) {
            self.read_tokens(scope, cx);
        }
        let prompt = facts.newest_prompt_seq;
        if matches!(self.prompt_read.replace(prompt), Some(read) if read != prompt) {
            self.reread_attachments(cx);
        }
    }
}

impl EventEmitter<PanelEvent> for ContextPane {}

impl Focusable for ContextPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for ContextPane {
    fn panel_name(&self) -> &'static str {
        CONTEXT
    }

    /// A default layout pane: a permanent tab, with no close button.
    fn closable(&self, _: &App) -> bool {
        false
    }

    /// The kit tells the tab when it's shown (its pill picked, or the drawer opened on it) and when it's hidden.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        match active {
            true => self.read(cx),
            false => {
                self._read = None;
                self._open = None;
                self._tokens = None;
                self._attachments = None;
                self.context_scope = None;
                self.read_serial += 1;
            }
        }
    }
}

impl Panel for ContextPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        CONTEXT
    }
}

impl Render for ContextPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let picked = SelectedSession::global(cx).read(cx).session_id().is_some();
        let column = v_flex()
            .id("context-pane")
            .track_focus(&self.focus)
            .size_full()
            .overflow_y_scroll()
            .p_2()
            .gap_2()
            .text_role(TextRole::Body, cx);
        if !picked {
            return column.child(no_session(cx));
        }
        let facts = self.live(cx);
        column
            .child(self.session_card(facts.as_ref(), cx))
            .child(self.usage_card(facts.as_ref(), cx))
            .child(self.files_card(cx))
            .child(self.attachments_section(cx))
    }
}

/// With no session picked, one message replaces every card.
fn no_session(cx: &App) -> impl IntoElement {
    v_flex()
        .debug_selector(|| "context-note-pick".into())
        .px_2()
        .gap_1()
        .child(
            div()
                .text_role(TextRole::Title, cx)
                .font_weight(FontWeight::SEMIBOLD)
                .child("No session selected"),
        )
        .child(div().text_color(cx.theme().muted_foreground).child(
            "Pick a session on the left and this panel shows what it is running with, how much of its context it \
             has used, and the files it has touched.",
        ))
}

#[path = "attachments_section.rs"]
mod attachments_section;
#[path = "files_touched.rs"]
mod files_touched;
use files_touched::{names, touched};
#[path = "listing.rs"]
mod listing;
#[path = "session_cards.rs"]
mod session_cards;

#[cfg(test)]
#[path = "context_pane_tests.rs"]
mod tests;
