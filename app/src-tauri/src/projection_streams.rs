use crate::agent_conversation::agui_projection::AguiProjector;
use crate::agent_conversation::protocol::AgentConversationEvent;
use crate::terminal::TerminalOutputEvent;
use serde::Serialize;
use serde_json::Value;
use std::collections::VecDeque;
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use tauri::ipc::Channel;

const MAX_TOTAL_FRAMES: usize = 256;
const MAX_TOTAL_BYTES: u64 = 4 * 1024 * 1024;
const MAX_IN_FLIGHT_FRAMES: usize = 16;
const MAX_IN_FLIGHT_BYTES: u64 = 1024 * 1024;
const MAX_PROJECTED_CHUNK_BYTES: u64 = MAX_TOTAL_BYTES;

/// One ordered frame on either of the two high-volume projection streams.
/// `bytes` is the UTF-8 size of the serialized `chunk`, not the envelope.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamEnvelope<T> {
    pub session: String,
    pub generation: u64,
    pub sequence: i64,
    pub bytes: u64,
    pub kind: &'static str,
    pub resync: bool,
    pub chunk: Option<T>,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectionStreamDiagnostics {
    pub workers: usize,
    pub channels: usize,
    pub queued_frames: usize,
    pub queued_bytes: u64,
}

#[derive(Debug)]
struct PendingFrame<T> {
    session: String,
    generation: u64,
    sequence: i64,
    bytes: u64,
    resync: bool,
    chunk: Option<T>,
}

#[derive(Debug)]
struct InFlight {
    bytes: u64,
}

struct StreamState<T> {
    registration_id: Option<String>,
    channel: Option<Channel<StreamEnvelope<T>>>,
    queue: VecDeque<PendingFrame<T>>,
    queued_bytes: u64,
    in_flight: VecDeque<InFlight>,
    in_flight_bytes: u64,
    pending_resync: Option<PendingFrame<T>>,
}

impl<T> Default for StreamState<T> {
    fn default() -> Self {
        Self {
            registration_id: None,
            channel: None,
            queue: VecDeque::new(),
            queued_bytes: 0,
            in_flight: VecDeque::new(),
            in_flight_bytes: 0,
            pending_resync: None,
        }
    }
}

/// Small bounded queue shared by the agent, AG-UI and terminal roots. It is
/// private on purpose: the app has three concrete streams, not a registry.
struct BoundedProjectionStream<T: Serialize + Send + 'static> {
    kind: &'static str,
    state: Mutex<StreamState<T>>,
    wake: Condvar,
}

impl<T: Serialize + Send + 'static> BoundedProjectionStream<T> {
    fn new(kind: &'static str) -> Arc<Self> {
        let stream = Arc::new(Self {
            kind,
            state: Mutex::new(StreamState::default()),
            wake: Condvar::new(),
        });
        let worker = Arc::clone(&stream);
        thread::Builder::new()
            .name(format!("projection-stream-{kind}"))
            .spawn(move || worker.run())
            .expect("projection stream worker should start");
        stream
    }

    fn replace(&self, registration_id: String, channel: Channel<StreamEnvelope<T>>) {
        let old_channel = {
            let Ok(mut state) = self.state.lock() else {
                return;
            };
            let old_channel = state.channel.replace(channel);
            state.registration_id = Some(registration_id);
            state.queue.clear();
            state.queued_bytes = 0;
            state.in_flight.clear();
            state.in_flight_bytes = 0;
            self.wake.notify_all();
            old_channel
        };
        // Dropping this local channel after the lock is released lets the
        // worker finish and release the old channel without holding state.
        drop(old_channel);
    }

    fn unregister(&self, registration_id: &str) {
        let old_channel = {
            let Ok(mut state) = self.state.lock() else {
                return;
            };
            if state.registration_id.as_deref() != Some(registration_id) {
                return;
            }
            state.registration_id = None;
            let old_channel = state.channel.take();
            state.queue.clear();
            state.queued_bytes = 0;
            state.in_flight.clear();
            state.in_flight_bytes = 0;
            self.wake.notify_all();
            old_channel
        };
        drop(old_channel);
    }

    fn acknowledge(&self, registration_id: &str, frames: usize, _bytes: u64) {
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        if state.registration_id.as_deref() != Some(registration_id) {
            return;
        }
        for _ in 0..frames {
            let Some(frame) = state.in_flight.pop_front() else {
                break;
            };
            state.in_flight_bytes = state.in_flight_bytes.saturating_sub(frame.bytes);
        }
        self.wake.notify_all();
    }

    fn diagnostics(&self) -> ProjectionStreamDiagnostics {
        let Ok(state) = self.state.lock() else {
            return ProjectionStreamDiagnostics::default();
        };
        ProjectionStreamDiagnostics {
            workers: 1,
            channels: usize::from(state.channel.is_some()),
            queued_frames: state.queue.len()
                + state.in_flight.len()
                + usize::from(state.pending_resync.is_some()),
            queued_bytes: state.queued_bytes.saturating_add(state.in_flight_bytes),
        }
    }

    fn current_channel_registration(&self) -> Option<String> {
        self.state.lock().ok().and_then(|state| {
            state
                .registration_id
                .clone()
                .filter(|_| state.channel.is_some())
        })
    }

    fn can_admit(state: &StreamState<T>, bytes: u64, reserve_control: bool) -> bool {
        let total_frames = state.queue.len() + state.in_flight.len();
        total_frames < MAX_TOTAL_FRAMES.saturating_sub(usize::from(reserve_control))
            && state
                .queued_bytes
                .saturating_add(state.in_flight_bytes)
                .saturating_add(bytes)
                <= MAX_TOTAL_BYTES
    }

    fn enqueue_nonblocking(&self, session: String, generation: u64, sequence: i64, chunk: T) {
        let Some(registration_id) = self.current_channel_registration() else {
            return;
        };
        let bytes = match serde_json::to_vec(&chunk) {
            Ok(serialized) => serialized.len() as u64,
            Err(_) => return,
        };
        let frame = PendingFrame {
            session,
            generation,
            sequence,
            bytes,
            resync: false,
            chunk: Some(chunk),
        };
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        if state.registration_id.as_deref() != Some(registration_id.as_str())
            || state.channel.is_none()
        {
            return;
        }
        if state.pending_resync.is_some() {
            state.pending_resync = Some(PendingFrame {
                session: frame.session,
                generation: frame.generation,
                sequence: frame.sequence,
                bytes: 0,
                resync: true,
                chunk: None,
            });
            self.wake.notify_all();
            return;
        }
        if bytes > MAX_PROJECTED_CHUNK_BYTES || !Self::can_admit(&state, bytes, true) {
            state.queue.clear();
            state.queued_bytes = 0;
            state.pending_resync = Some(PendingFrame {
                session: frame.session,
                generation: frame.generation,
                sequence: frame.sequence,
                bytes: 0,
                resync: true,
                chunk: None,
            });
            self.wake.notify_all();
            return;
        }
        state.queued_bytes = state.queued_bytes.saturating_add(bytes);
        state.queue.push_back(frame);
        self.wake.notify_all();
    }

    fn enqueue_terminal(&self, session: String, generation: u64, sequence: i64, chunk: T) {
        let Some(registration_id) = self.current_channel_registration() else {
            return;
        };
        let bytes = match serde_json::to_vec(&chunk) {
            Ok(serialized) => serialized.len() as u64,
            Err(_) => return,
        };
        if bytes > MAX_PROJECTED_CHUNK_BYTES {
            return;
        }
        let frame = PendingFrame {
            session,
            generation,
            sequence,
            bytes,
            resync: false,
            chunk: Some(chunk),
        };
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        loop {
            if state.registration_id.as_deref() != Some(registration_id.as_str())
                || state.channel.is_none()
            {
                return;
            }
            if Self::can_admit(&state, frame.bytes, false) {
                state.queued_bytes = state.queued_bytes.saturating_add(frame.bytes);
                state.queue.push_back(frame);
                self.wake.notify_all();
                return;
            }
            state = match self.wake.wait(state) {
                Ok(next) => next,
                Err(_) => return,
            };
        }
    }

    fn can_send(state: &StreamState<T>, bytes: u64) -> bool {
        if state.in_flight.len() >= MAX_IN_FLIGHT_FRAMES {
            return false;
        }
        if bytes > MAX_IN_FLIGHT_BYTES {
            return state.in_flight.is_empty();
        }
        state.in_flight_bytes.saturating_add(bytes) <= MAX_IN_FLIGHT_BYTES
    }

    fn next_frame(
        state: &mut StreamState<T>,
    ) -> Option<(Channel<StreamEnvelope<T>>, String, PendingFrame<T>)> {
        let channel = state.channel.clone()?;
        let registration_id = state.registration_id.clone()?;
        let can_send_queue = state
            .queue
            .front()
            .map(|frame| Self::can_send(state, frame.bytes))
            .unwrap_or(false);
        let frame = if can_send_queue {
            let frame = state.queue.pop_front()?;
            state.queued_bytes = state.queued_bytes.saturating_sub(frame.bytes);
            frame
        } else if state.queue.is_empty() {
            let pending = state.pending_resync.take()?;
            if !Self::can_send(state, pending.bytes) {
                state.pending_resync = Some(pending);
                return None;
            }
            pending
        } else {
            return None;
        };
        state.in_flight_bytes = state.in_flight_bytes.saturating_add(frame.bytes);
        state.in_flight.push_back(InFlight { bytes: frame.bytes });
        Some((channel, registration_id, frame))
    }

    fn run(self: Arc<Self>) {
        loop {
            let next = {
                let Ok(mut state) = self.state.lock() else {
                    return;
                };
                loop {
                    if let Some(next) = Self::next_frame(&mut state) {
                        break Some(next);
                    }
                    state = match self.wake.wait(state) {
                        Ok(next) => next,
                        Err(_) => return,
                    };
                }
            };
            let Some((channel, registration_id, frame)) = next else {
                continue;
            };
            let failed_resync = (self.kind == "agent-conversation-event").then(|| PendingFrame {
                session: frame.session.clone(),
                generation: frame.generation,
                sequence: frame.sequence,
                bytes: 0,
                resync: true,
                chunk: None,
            });
            let envelope = StreamEnvelope {
                session: frame.session,
                generation: frame.generation,
                sequence: frame.sequence,
                bytes: frame.bytes,
                kind: self.kind,
                resync: frame.resync,
                chunk: frame.chunk,
            };
            let send_result = channel.send(envelope);
            if send_result.is_err() {
                let old_channel = if let Ok(mut state) = self.state.lock() {
                    if state.registration_id.as_deref() == Some(registration_id.as_str()) {
                        state.registration_id = None;
                        let old_channel = state.channel.take();
                        state.queue.clear();
                        state.queued_bytes = 0;
                        state.in_flight.clear();
                        state.in_flight_bytes = 0;
                        if let Some(marker) = failed_resync {
                            state.pending_resync = Some(marker);
                        }
                        self.wake.notify_all();
                        old_channel
                    } else {
                        None
                    }
                } else {
                    None
                };
                drop(old_channel);
            }
            // `channel` is deliberately dropped here. Replacement/unregister
            // can therefore release the old frontend callback as soon as this
            // worker finishes its one send.
        }
    }
}

#[derive(Clone)]
pub struct ProjectionStreams {
    agent_conversation: Arc<BoundedProjectionStream<AgentConversationEvent>>,
    terminal_output: Arc<BoundedProjectionStream<TerminalOutputEvent>>,
    /// The AG-UI chat stream: one envelope per native event, holding its chunks
    /// for the views that are open (`agent_conversation::agui_projection`).
    agui: Arc<BoundedProjectionStream<Vec<Value>>>,
    agui_views: Arc<Mutex<AguiProjector>>,
    /// Set on a remote server, which has no Tauri window: output goes to its sockets.
    terminal_tap: Option<tokio::sync::broadcast::Sender<crate::terminal::TerminalOutputFrame>>,
}

impl Default for ProjectionStreams {
    fn default() -> Self {
        Self {
            agent_conversation: BoundedProjectionStream::new("agent-conversation-event"),
            terminal_output: BoundedProjectionStream::new("terminal_output"),
            agui: BoundedProjectionStream::new("agui"),
            agui_views: Arc::default(),
            terminal_tap: None,
        }
    }
}

impl ProjectionStreams {
    pub fn with_terminal_tap(
        tap: tokio::sync::broadcast::Sender<crate::terminal::TerminalOutputFrame>,
    ) -> Self {
        Self { terminal_tap: Some(tap), ..Self::default() }
    }

    pub fn publish_agent_event(&self, event: AgentConversationEvent) {
        if self.agui.current_channel_registration().is_some() {
            self.with_agui_views(|views| {
                let chunks = views.project(&event);
                if !chunks.is_empty() {
                    self.send_agui(event.owned_id.clone(), event.generation, event.sequence, chunks);
                }
            });
        }
        let session = event.owned_id.clone();
        let generation = event.generation;
        let sequence = event.sequence;
        self.agent_conversation
            .enqueue_nonblocking(session, generation, sequence, event);
    }

    pub fn publish_terminal_output(
        &self,
        session: String,
        generation: u64,
        sequence: i64,
        chunk: TerminalOutputEvent,
    ) {
        if let Some(tap) = &self.terminal_tap {
            let _ = tap.send(crate::terminal::TerminalOutputFrame { generation, sequence, output: chunk });
            return;
        }
        self.terminal_output
            .enqueue_terminal(session, generation, sequence, chunk);
    }

    pub fn diagnostics(&self) -> ProjectionStreamDiagnostics {
        let [agent, terminal, agui] = [self.agent_conversation.diagnostics(), self.terminal_output.diagnostics(), self.agui.diagnostics()];
        ProjectionStreamDiagnostics {
            workers: agent.workers + terminal.workers + agui.workers,
            channels: agent.channels + terminal.channels + agui.channels,
            queued_frames: agent.queued_frames + terminal.queued_frames + agui.queued_frames,
            queued_bytes: agent.queued_bytes.saturating_add(terminal.queued_bytes).saturating_add(agui.queued_bytes),
        }
    }

    /// Runs `apply` on the open AG-UI views. Chunks sent from inside it keep
    /// their order against live events.
    pub fn with_agui_views<R>(&self, apply: impl FnOnce(&mut AguiProjector) -> R) -> R {
        apply(&mut self.agui_views.lock().unwrap_or_else(std::sync::PoisonError::into_inner))
    }

    pub fn send_agui(&self, session: String, generation: u64, sequence: i64, chunks: Vec<Value>) {
        self.agui.enqueue_nonblocking(session, generation, sequence, chunks);
    }

    /// A new AG-UI channel is a new view: nothing it showed before carries over.
    pub fn register_agui(&self, registration_id: String, channel: Channel<StreamEnvelope<Vec<Value>>>) {
        self.with_agui_views(|views| *views = AguiProjector::default());
        self.agui.replace(registration_id, channel);
    }

    pub fn acknowledge_agui(&self, registration_id: &str, frames: usize, bytes: u64) {
        self.agui.acknowledge(registration_id, frames, bytes);
    }

    pub fn unregister_agui(&self, registration_id: &str) {
        self.agui.unregister(registration_id);
    }

    pub fn register_agent_conversation(
        &self,
        registration_id: String,
        channel: Channel<StreamEnvelope<AgentConversationEvent>>,
    ) {
        self.agent_conversation.replace(registration_id, channel);
    }

    pub fn acknowledge_agent_conversation(&self, registration_id: &str, frames: usize, bytes: u64) {
        self.agent_conversation
            .acknowledge(registration_id, frames, bytes);
    }

    pub fn unregister_agent_conversation(&self, registration_id: &str) {
        self.agent_conversation.unregister(registration_id);
    }

    pub fn register_terminal_output(
        &self,
        registration_id: String,
        channel: Channel<StreamEnvelope<TerminalOutputEvent>>,
    ) {
        self.terminal_output.replace(registration_id, channel);
    }

    pub fn acknowledge_terminal_output(&self, registration_id: &str, frames: usize, bytes: u64) {
        self.terminal_output
            .acknowledge(registration_id, frames, bytes);
    }

    pub fn unregister_terminal_output(&self, registration_id: &str) {
        self.terminal_output.unregister(registration_id);
    }
}

#[tauri::command]
pub fn register_agent_conversation_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
    channel: Channel<StreamEnvelope<AgentConversationEvent>>,
) {
    streams.register_agent_conversation(registration_id, channel);
}

#[tauri::command]
pub fn acknowledge_agent_conversation_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
    frames: usize,
    bytes: u64,
) {
    streams.acknowledge_agent_conversation(&registration_id, frames, bytes);
}

#[tauri::command]
pub fn unregister_agent_conversation_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
) {
    streams.unregister_agent_conversation(&registration_id);
}

#[tauri::command]
pub fn register_terminal_output_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
    channel: Channel<StreamEnvelope<TerminalOutputEvent>>,
) {
    streams.register_terminal_output(registration_id, channel);
}

#[tauri::command]
pub fn acknowledge_terminal_output_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
    frames: usize,
    bytes: u64,
) {
    streams.acknowledge_terminal_output(&registration_id, frames, bytes);
}

#[tauri::command]
pub fn unregister_terminal_output_stream(
    streams: tauri::State<'_, ProjectionStreams>,
    registration_id: String,
) {
    streams.unregister_terminal_output(&registration_id);
}
