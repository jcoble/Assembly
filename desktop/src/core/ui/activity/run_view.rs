//! Lazy workflow display: one shared runs lease and one cancellable coalesced paged read.
use crate::core::{
    engine::{
        EngineChanged, EngineFeed, EngineScope, EngineWatch, SessionKey, workflow_items::RunView,
    },
    socket::client::SocketClient,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;
pub struct RunActivity {
    parent: SessionKey,
    feed: Entity<EngineFeed>,
    scope: Option<EngineScope>,
    watch: Option<EngineWatch>,
    view: Option<RunView>,
    error: Option<String>,
    dirty: bool,
    serial: u64,
    read: Option<Task<()>>,
    connections: Vec<(Entity<SocketClient>, u64)>,
    _connections: Vec<Subscription>,
    _changed: Subscription,
}
impl RunActivity {
    pub fn new(parent: SessionKey, cx: &mut Context<Self>) -> Self {
        let feed = EngineFeed::global(cx);
        let changed = cx.subscribe(&feed, |view, _, event: &EngineChanged, cx| {
            if event.sessions.contains(&view.parent)
                || view
                    .scope
                    .as_ref()
                    .is_some_and(|scope| event.scopes.contains(scope))
            {
                view.dirty = true;
                cx.notify();
            }
        });
        let mut view = Self {
            parent,
            feed,
            scope: None,
            watch: None,
            view: None,
            error: None,
            dirty: true,
            serial: 0,
            read: None,
            connections: Vec::new(),
            _connections: Vec::new(),
            _changed: changed,
        };
        for socket in [SocketClient::try_global(cx), SocketClient::try_bulk(cx)]
            .into_iter()
            .flatten()
        {
            if let Some(generation) = socket.read(cx).connection_generation() {
                view.connections.push((socket.clone(), generation));
            }
            view._connections.push(cx.subscribe(&socket,|view,_,_:&crate::core::socket::connection_state::ConnectionState,cx| {
                if !view.current(cx){view.serial+=1;view.read=None;view.watch=None;view.view=None;view.error=Some("The workflow source connection changed. Reopen this view to revalidate it.".into());view.dirty=false;cx.notify();}
            }));
        }
        view
    }
    fn current(&self, cx: &App) -> bool {
        self.connections.len() == 2
            && SocketClient::try_global(cx).as_ref() == Some(&self.connections[0].0)
            && SocketClient::try_bulk(cx).as_ref() == Some(&self.connections[1].0)
            && self.connections.iter().all(|(socket, generation)| {
                socket.read(cx).connection_generation() == Some(*generation)
            })
    }
    fn refresh(&mut self, cx: &mut Context<Self>) {
        if !self.dirty || self.read.is_some() {
            return;
        }
        self.dirty = false;
        if !self.current(cx) {
            self.error = Some("The workflow connections are unavailable.".into());
            return;
        }
        let session_scope = EngineScope {
            machine: self.parent.machine.clone(),
            topic: "sessions".into(),
            key: None,
        };
        if !self.feed.read(cx).loaded(&session_scope) {
            self.view = None;
            self.error = Some("The owning session display is unavailable.".into());
            return;
        }
        let run_id = self
            .feed
            .read(cx)
            .session(&self.parent)
            .and_then(|session| session.run_id);
        let Some(run_id) = run_id else {
            self.scope = None;
            self.watch = None;
            self.view = None;
            self.error = None;
            return;
        };
        let scope = EngineScope {
            machine: self.parent.machine.clone(),
            topic: "runs".into(),
            key: Some(run_id.clone()),
        };
        if self.scope.as_ref() != Some(&scope) {
            self.view = None;
            self.watch = None;
            self.scope = Some(scope.clone());
            match self
                .feed
                .update(cx, |feed, cx| feed.watch(scope.clone(), cx))
            {
                Ok(watch) => self.watch = Some(watch),
                Err(error) => {
                    self.error = Some(error.message);
                    return;
                }
            }
        }
        if !self.feed.read(cx).loaded(&scope) {
            self.error = Some("Loading the current workflow…".into());
            return;
        }
        self.serial += 1;
        let serial = self.serial;
        let machine = self.parent.machine.clone();
        self.error = None;
        self.read = Some(cx.spawn(async move |view, cx| {
            let result = crate::core::engine::workflow_read::run(&machine, &run_id, cx).await;
            let _ = view.update(cx, |view, cx| {
                if view.serial != serial || !view.current(cx) {
                    return;
                }
                view.read = None;
                if !view
                    .scope
                    .as_ref()
                    .is_some_and(|scope| view.feed.read(cx).loaded(scope))
                {
                    view.view = None;
                    view.error = Some("The workflow source is no longer coherent.".into());
                } else {
                    match result {
                        Ok(page) => view.view = Some(page),
                        Err(error) => view.error = Some(error.message),
                    }
                }
                // A push received during this bounded pass schedules exactly one following pass.
                cx.notify();
            });
        }));
    }
}
impl Render for RunActivity {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.refresh(cx);
        let muted = cx.theme().muted_foreground;
        let mut body = v_flex().gap_2();
        if let Some(error) = &self.error {
            body = body.child(div().text_color(muted).child(error.clone()));
            if self.current(cx) && self.read.is_none() {
                body = body.child(
                    Button::new("reload-workflow")
                        .ghost()
                        .label("Retry")
                        .on_click(cx.listener(|view, _, _, cx| {
                            view.dirty = true;
                            if let Some(watch) = &view.watch {
                                watch.request_snapshot();
                            }
                            cx.notify();
                        })),
                );
            }
        }
        if let Some(page) = &self.view {
            body = body
                .child(
                    div().font_weight(FontWeight::MEDIUM).child(
                        page.run
                            .title
                            .clone()
                            .unwrap_or_else(|| page.run.id.clone()),
                    ),
                )
                .child(div().text_color(muted).child(page.run.state.clone()));
            if let Some(reason) = &page.run.display_prerequisite {
                body = body.child(reason.clone());
            }
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .ok()
                .map(|time| time.as_millis().min(i64::MAX as u128) as i64)
                .unwrap_or(0);
            for step in &page.steps {
                body = body.child(crate::core::ui::activity::step_row::render(step, now, cx));
            }
        } else if self.read.is_some() {
            body = body.child("Loading workflow…");
        } else if self.error.is_none() {
            body = body.child("No workflow is attached to this session.");
        }
        body
    }
}
