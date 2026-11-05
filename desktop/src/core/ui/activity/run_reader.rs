//! One lazy read of a workflow run: one shared runs lease and one cancellable coalesced paged read. It notifies when the
//! run or its error changes. The rail follows a session's run, the Board a run by its id.
use gpui_kit::*;

use crate::core::engine::workflow_items::{RunItem, RunView, StepItem};
use crate::core::engine::workflow_read::{self, UNREACHABLE};
use crate::core::engine::{EngineChanged, EngineFeed, EngineScope, EngineWatch, SessionKey};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RunSource {
    Session(SessionKey),
    Run { machine: String, id: String },
}

pub struct RunReader {
    source: RunSource,
    feed: Entity<EngineFeed>,
    scope: Option<EngineScope>,
    watch: Option<EngineWatch>,
    view: Option<RunView>,
    error: Option<String>,
    /// The view is what the shared rows last said, since the run's machine can't be asked now.
    last_known: bool,
    dirty: bool,
    serial: u64,
    read: Option<Task<()>>,
    connections: Vec<(Entity<SocketClient>, u64)>,
    _connections: Vec<Subscription>,
    _changed: Subscription,
}

impl RunReader {
    pub fn new(source: RunSource, cx: &mut Context<Self>) -> Self {
        let feed = EngineFeed::global(cx);
        let changed = cx.subscribe(&feed, |reader, _, event: &EngineChanged, cx| {
            let owner = matches!(&reader.source, RunSource::Session(parent) if event.sessions.contains(parent));
            // While it shows last known steps, anything new from that machine is worth asking again.
            let back = reader.last_known
                && reader.scope.as_ref().is_some_and(|scope| {
                    event.keys.iter().any(|key| key.machine == scope.machine)
                        || event.scopes.iter().any(|changed| changed.machine == scope.machine)
                });
            if owner || back || reader.scope.as_ref().is_some_and(|scope| event.scopes.contains(scope)) {
                reader.dirty = true;
                reader.refresh(cx);
                cx.notify();
            }
        });
        let mut reader = Self {
            source,
            feed,
            scope: None,
            watch: None,
            view: None,
            error: None,
            last_known: false,
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
                reader.connections.push((socket.clone(), generation));
            }
            reader._connections.push(cx.subscribe(&socket, |reader, _, _: &ConnectionState, cx| {
                if !reader.current(cx) {
                    reader.serial += 1;
                    reader.read = None;
                    reader.watch = None;
                    reader.view = None;
                    reader.error = Some("The workflow source connection changed. Reopen this view to revalidate it.".into());
                    reader.dirty = false;
                    cx.notify();
                }
            }));
        }
        reader
    }

    pub fn view(&self) -> Option<&RunView> {
        self.view.as_ref()
    }

    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }

    pub fn is_last_known(&self) -> bool {
        self.last_known
    }

    /// Shows what's held, or what the shared rows last said. False when they've nothing on this run.
    fn show_last_known(&mut self, machine: &str, run_id: &str, cx: &App) -> bool {
        if self.view.is_none() {
            let feed = self.feed.read(cx);
            let rows = feed.topic_items("runs");
            let of = |kind: &str| {
                rows.iter()
                    .filter(move |item| item.key.machine == machine && item.key.kind == kind)
                    .map(|item| item.value.clone())
                    .collect::<Vec<_>>()
            };
            let run = of("run")
                .into_iter()
                .filter_map(|value| RunItem::decode(value).ok())
                .find(|run| run.id == run_id);
            let Some(run) = run else {
                return false;
            };
            let steps = of("step")
                .into_iter()
                .filter_map(|value| StepItem::decode(value).ok())
                .filter(|step| step.run_id == run_id)
                .collect();
            self.view = Some(RunView::last_known(run, steps));
        }
        self.error = None;
        self.last_known = true;
        true
    }

    pub fn reading(&self) -> bool {
        self.read.is_some()
    }

    pub fn can_retry(&self, cx: &App) -> bool {
        self.current(cx) && self.read.is_none()
    }

    pub fn retry(&mut self, cx: &mut Context<Self>) {
        self.dirty = true;
        if let Some(watch) = &self.watch {
            watch.request_snapshot();
        }
        self.refresh(cx);
        cx.notify();
    }

    fn current(&self, cx: &App) -> bool {
        self.connections.len() == 2
            && SocketClient::try_global(cx).as_ref() == Some(&self.connections[0].0)
            && SocketClient::try_bulk(cx).as_ref() == Some(&self.connections[1].0)
            && self.connections.iter().all(|(socket, generation)| {
                socket.read(cx).connection_generation() == Some(*generation)
            })
    }

    /// The run to read, or an empty answer when a session has none.
    fn target(&mut self, cx: &App) -> Result<Option<(String, String)>, String> {
        match &self.source {
            RunSource::Run { machine, id } => Ok(Some((machine.clone(), id.clone()))),
            RunSource::Session(parent) => {
                let scope = EngineScope {
                    machine: parent.machine.clone(),
                    topic: "sessions".into(),
                    key: None,
                };
                let feed = self.feed.read(cx);
                if !feed.loaded(&scope) {
                    return Err("The owning session display is unavailable.".into());
                }
                Ok(feed
                    .session(parent)
                    .and_then(|session| session.run_id)
                    .map(|id| (parent.machine.clone(), id)))
            }
        }
    }

    /// Runs when asked, whether first drawn or told something changed. Nothing runs unless it's marked dirty.
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        if !self.dirty || self.read.is_some() {
            return;
        }
        self.dirty = false;
        if !self.current(cx) {
            self.error = Some("The workflow connections are unavailable.".into());
            return;
        }
        let (machine, run_id) = match self.target(cx) {
            Ok(Some(target)) => target,
            Ok(None) => {
                self.scope = None;
                self.watch = None;
                self.view = None;
                self.error = None;
                return;
            }
            Err(message) => {
                self.view = None;
                self.error = Some(message);
                return;
            }
        };
        let scope = EngineScope {
            machine: machine.clone(),
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
            if !self.show_last_known(&machine, &run_id, cx) {
                self.error = Some("Loading the current workflow…".into());
            }
            return;
        }
        self.serial += 1;
        let serial = self.serial;
        self.error = None;
        self.read = Some(cx.spawn(async move |reader, cx| {
            let result = workflow_read::run(&machine, &run_id, cx).await;
            let _ = reader.update(cx, |reader, cx| {
                if reader.serial != serial || !reader.current(cx) {
                    return;
                }
                reader.read = None;
                match result {
                    Err(error) if error.code == UNREACHABLE => {
                        if !reader.show_last_known(&machine, &run_id, cx) {
                            reader.error = Some("Loading the current workflow…".into());
                        }
                    }
                    _ if !reader
                        .scope
                        .as_ref()
                        .is_some_and(|scope| reader.feed.read(cx).loaded(scope)) =>
                    {
                        reader.view = None;
                        reader.error = Some("The workflow source is no longer coherent.".into());
                    }
                    Ok(page) => {
                        reader.view = Some(page);
                        reader.last_known = false;
                    }
                    Err(error) => {
                        reader.last_known = false;
                        reader.view = None;
                        reader.error = Some(error.message);
                    }
                }
                // A push received during this bounded pass schedules exactly one following pass.
                reader.refresh(cx);
                cx.notify();
            });
        }));
    }
}
