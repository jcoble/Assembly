//! Lazy workflow display: one shared runs lease and one cancellable coalesced paged read.
use crate::activity::{CardBody, RunRow, StatusChip, Tone, step_list};
use crate::core::ui::activity::step_row::step_view;
use crate::core::ui::row_drop_down::RowDropDowns;
use crate::core::{
    engine::{
        EngineChanged, EngineFeed, EngineScope, EngineWatch, SessionKey, workflow_items::RunView,
    },
    socket::client::SocketClient,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;
use std::rc::Rc;
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
    /// The run's row and its steps each open on their own.
    runs: Entity<RowDropDowns>,
    steps: Entity<RowDropDowns>,
    opened: Option<String>,
    connections: Vec<(Entity<SocketClient>, u64)>,
    _connections: Vec<Subscription>,
    _changed: Subscription,
    _drops: [Subscription; 2],
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
        let (runs, steps) = (
            cx.new(|_| RowDropDowns::default()),
            cx.new(|_| RowDropDowns::default()),
        );
        let drops = [
            cx.observe(&runs, |_, _, cx| cx.notify()),
            cx.observe(&steps, |_, _, cx| cx.notify()),
        ];
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
            runs,
            steps,
            opened: None,
            connections: Vec::new(),
            _connections: Vec::new(),
            _changed: changed,
            _drops: drops,
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
                        Ok(page) => {
                            view.view = Some(page);
                            view.open_first_time(cx);
                        }
                        Err(error) => view.error = Some(error.message),
                    }
                }
                // A push received during this bounded pass schedules exactly one following pass.
                cx.notify();
            });
        }));
    }
    /// A run shows its steps when it first arrives, since it's already inside what the user opened.
    fn open_first_time(&mut self, cx: &mut Context<Self>) {
        let Some(id) = self.view.as_ref().map(|page| page.run.id.clone()) else {
            return;
        };
        if self.opened.as_ref() == Some(&id) {
            return;
        }
        self.opened = Some(id.clone());
        RunRow::open(
            id.into(),
            &self.runs,
            Self::steps_body(cx.entity().downgrade()),
            cx,
        );
    }
    /// Reads the run as it is now on every frame, so an open list follows the steps.
    fn steps_body(this: WeakEntity<Self>) -> CardBody {
        Rc::new(move |_, cx| {
            let Some((key, steps, drops)) = this.upgrade().and_then(|this| {
                let this = this.read(cx);
                let page = this.view.as_ref()?;
                let now = now_ms();
                Some((
                    page.run.id.clone(),
                    page.steps
                        .iter()
                        .map(|step| step_view(step, now))
                        .collect::<Vec<_>>(),
                    this.steps.clone(),
                ))
            }) else {
                return Empty.into_any_element();
            };
            step_list(key, steps, &drops, cx)
        })
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
            let tone = run_tone(&page.run.state);
            let done = page
                .steps
                .iter()
                .filter(|step| step.state == "succeeded")
                .count();
            let current = page
                .steps
                .iter()
                .find(|step| step.state == "running")
                .map(|step| step.label_present.clone());
            let step_line = page
                .run
                .display_prerequisite
                .clone()
                .or(current)
                .unwrap_or_else(|| page.run.state.clone());
            body = body.child(RunRow {
                key: page.run.id.clone().into(),
                title: page
                    .run
                    .title
                    .clone()
                    .unwrap_or_else(|| page.run.id.clone())
                    .into(),
                task_key: None,
                step_line: step_line.into(),
                done,
                total: page.steps.len(),
                tone,
                pill: Some(StatusChip::new(page.run.state.clone(), tone).into_any_element()),
                body: Self::steps_body(cx.entity().downgrade()),
                drops: self.runs.clone(),
            });
        } else if self.read.is_some() {
            body = body.child("Loading workflow…");
        } else if self.error.is_none() {
            body = body.child("No workflow is attached to this session.");
        }
        body
    }
}
fn run_tone(state: &str) -> Tone {
    match state {
        "running" => Tone::Info,
        "succeeded" => Tone::Success,
        "failed" => Tone::Danger,
        "waiting" => Tone::Warning,
        _ => Tone::Neutral,
    }
}
fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .ok()
        .map(|time| time.as_millis().min(i64::MAX as u128) as i64)
        .unwrap_or(0)
}
