use super::super::{protocol::CommandResult, remote::RemoteConnectionManager};
use mcb_core::session_store::SessionStore;
use rmcp::schemars::{self, JsonSchema};
use serde_json::{json, Value};
use std::sync::{Mutex, PoisonError};
use tokio::sync::broadcast::{error::RecvError, Receiver};

/// When `notify` or the 30-minute reminder sent something to the phone since the owner last answered a board question.
static NOTIFIED: Mutex<Vec<i64>> = Mutex::new(Vec::new());
const PHONE_LIMIT: &str = "phone limit reached: the owner hasn't answered; 3 in 30 minutes, 5 in an hour";
const NOT_ORCHESTRATOR: &str = "only the main orchestrator can message the owner's phone; the board has notified the desktop";

/// Takes one phone push for the 30-minute reminder, unless the limit is reached.
pub(crate) fn phone_slot(now: i64) -> bool {
    let free = !phone_limited(now);
    if free { NOTIFIED.lock().unwrap_or_else(PoisonError::into_inner).push(now); }
    free
}

/// The phone gets at most 3 pushes in any 30 minutes and 5 in any hour until the owner answers.
fn phone_limited(now: i64) -> bool {
    let mut sent = NOTIFIED.lock().unwrap_or_else(PoisonError::into_inner);
    sent.retain(|at| now - at < 3_600_000);
    sent.len() >= 5 || sent.iter().filter(|at| now - **at < 1_800_000).count() >= 3
}

pub(super) const DESCRIPTION: &str = "Create or update a card on the owner's Build board. The session the owner gave a task to keeps one card for it; the main orchestrator may update any card. Helpers cannot post, and workflow workers should not: their parent's card or their run already shows. Key: the task key if there is one, otherwise a short name no other session would pick. Post when you start, update when the state changes, and use needs_you only when the owner must act, with steps saying what to do. A needs_you card waits on the board as a small on-hold card, and the board notifies the owner's desktop; only the main orchestrator can pass notify to send a card or question to the owner's phone. Record every question you ask the owner before you ask it, in the same turn: pass question (the explanation, with options, your recommendation and why, and PR, task and proof links), title and recommended. The reply gives its number n: start your chat question with Q<n>, since the owner's answer arrives as 'Q<n>: …'. When it is answered anywhere, call again with the same key and answer. To ask it again, call with the same key and a new question. steps[0] is what the owner sees in the notification: write it as the one thing needed. Approvals and workflow pauses already show.";

/// `board_update`'s arguments; `update` reads them.
#[derive(JsonSchema)]
#[allow(dead_code)]
pub(super) struct BoardUpdate {
    /// Short id for this work (see above). One line, at most 80 characters.
    #[schemars(length(max = 80))]
    key: String,
    /// Required for a new card. One line, at most 120.
    #[schemars(length(max = 120))]
    title: Option<String>,
    /// Required for a new card.
    state: Option<CardState>,
    /// At most 600.
    #[schemars(length(max = 600))]
    summary: Option<String>,
    /// What the owner should do, plain text, at most 6 of 160 each.
    #[schemars(length(max = 6), inner(length(max = 160)))]
    steps: Option<Vec<String>>,
    /// One note for the owner, at most 600.
    #[schemars(length(max = 600))]
    note: Option<String>,
    /// The Notion task; hidden when Notion is off.
    task_key: Option<String>,
    /// The Notion task; hidden when Notion is off.
    task_url: Option<String>,
    /// The PR.
    pr_url: Option<String>,
    /// A run on this machine.
    run_id: Option<String>,
    /// A session on this machine (handle or name); default the caller.
    session: Option<String>,
    /// Marks this call as a question; required to ask or re-ask. At most 1200.
    #[schemars(length(max = 1200))]
    question: Option<String>,
    /// Your recommended answer, at most 300.
    #[schemars(length(max = 300))]
    recommended: Option<String>,
    /// The answer the question got, at most 600; marks it answered.
    #[schemars(length(max = 600))]
    answer: Option<String>,
    /// Main orchestrator only. Send this card to the owner's phone now. Use it whenever it's a reasonable reason to message the owner: a question, something ready for them to test or look at, a blocker, a finished batch, an emergency. Don't use it for routine progress.
    notify: Option<bool>,
}

#[derive(JsonSchema)]
#[serde(rename_all = "snake_case")]
#[schemars(inline)]
#[allow(dead_code)]
enum CardState { NeedsYou, Running, InReview, Done }

pub(super) fn update(store: &SessionStore, caller: &str, role: &str, mut args: Value) -> Result<Value, String> {
    if role == "helper" {
        return Err("helpers cannot post to the board".into());
    }
    if let Some(session) = args["session"].as_str() {
        // The directory leaves out the caller, so its own handle or name is matched here.
        let own = store.directory_name(caller).map_err(|error| error.to_string())?;
        let is_caller = session == caller || own.is_some_and(|(handle, name)|
            session == name || session.split(" · ").next() == Some(handle.as_str()));
        let owned_id = if is_caller { caller.to_string() } else {
            store.resolve_session_name_for(session, caller, "local").map_err(|error| error.to_string())?
        };
        args["session"] = json!(owned_id);
    }
    let key = args["key"].as_str().ok_or("key must be a string")?.to_string();
    let now = chrono::Utc::now().timestamp_millis();
    let refused = if args["notify"] != true { None } else if role != "orchestrator" { Some(NOT_ORCHESTRATOR) }
        else if phone_limited(now) { Some(PHONE_LIMIT) } else { None };
    if refused.is_some() { args["notify"] = json!(false); }
    let mut reply = json!({"key": key, "saved": true});
    if args.get("question").is_some() || args.get("answer").is_some() {
        reply["question"] = json!(store.board_question_update(&key, caller, role == "orchestrator", &args, now).map_err(|e| e.to_string())?);
    } else {
        store.board_update(&key, caller, role == "orchestrator", &args, now).map_err(|error| error.to_string())?;
    }
    let mut sent = NOTIFIED.lock().unwrap_or_else(PoisonError::into_inner);
    // An answer recorded here is the owner's answer from the chat; it resets the limit like a board answer.
    if args.get("answer").is_some() { sent.clear(); }
    if args["notify"] == true { sent.push(now); }
    drop(sent);
    if let Some(text) = refused { reply["phone"] = json!(text); }
    mcb_core::broker::messages_changed();
    Ok(reply)
}

/// The owner's board operations on this machine's own store; agents never reach these.
pub(super) fn answer(store: &SessionStore, op: &str, args: &Value) -> Result<Value, String> {
    let text = |key: &str| args[key].as_str().ok_or_else(|| format!("{key} is required"));
    let number = |key: &str| args[key].as_i64().ok_or_else(|| format!("{key} is required"));
    let result = match op {
        "board" => store.board(number("today_ms")?, number("now_ms")?),
        "board_count" => store.board_count(number("now_ms")?),
        "board_card" => store.board_card(text("key")?, text("action")?, number("at")?).map(Value::Bool),
        "board_note" => store.board_note(&uuid::Uuid::new_v4().to_string(), text("target")?, text("body")?, number("now_ms")?).map(Value::Bool),
        "board_question" => store.board_question_answer(number("id")?, number("asked_at_ms")?, text("answer")?, number("now_ms")?).map(Value::Bool),
        _ => return Err(format!("Unknown messaging operation {op}")),
    };
    let value = result.map_err(|error| error.to_string())?;
    if matches!(op, "board_card" | "board_note" | "board_question") && value == Value::Bool(true) {
        if op == "board_question" { NOTIFIED.lock().unwrap_or_else(PoisonError::into_inner).clear(); }
        mcb_core::broker::messages_changed();
    }
    Ok(value)
}

/// `board` and `board_count` read this desktop and every connected machine (a machine that cannot
/// be reached is left out); any other board operation goes to the one machine in `args.machine`.
pub(crate) async fn board_op(remote: &RemoteConnectionManager, op: &str, args: Value, request_id: u64) -> Result<Value, String> {
    if !matches!(op, "board" | "board_count") {
        let machine = args["machine"].as_str().ok_or("machine is required")?;
        return remote.message_machine(machine, op, args.clone(), Some(request_id)).await;
    }
    let mut boards = Vec::new();
    let mut count = 0;
    let mut snoozed_until_ms: Option<i64> = None;
    for machine in remote.message_machines("") {
        let Ok(value) = remote.message_machine(&machine, op, args.clone(), Some(request_id)).await else { continue };
        if op == "board" {
            boards.push(json!({"machine": machine, "value": value}));
        } else {
            count += value["count"].as_i64().unwrap_or_default();
            if let Some(at) = value["snoozed_until_ms"].as_i64() {
                snoozed_until_ms = Some(snoozed_until_ms.map_or(at, |earliest| earliest.min(at)));
            }
        }
    }
    Ok(if op == "board" { json!(boards) } else { json!({"count": count, "snoozed_until_ms": snoozed_until_ms}) })
}

#[tauri::command]
pub async fn board(remote: tauri::State<'_, RemoteConnectionManager>, op: String, args: Value, request_id: u64) -> CommandResult<Value> {
    Ok(remote.unless_cancelled(request_id, board_op(&remote, &op, args, request_id)).await.transpose()?.unwrap_or(Value::Null))
}

/// Calls `changed` for every run change, so the board recounts; ends when the engine goes away.
pub(super) async fn forward_run_changes(mut runs: Receiver<(String, usize)>, changed: impl Fn()) {
    loop {
        match runs.recv().await {
            Ok(_) | Err(RecvError::Lagged(_)) => changed(),
            Err(RecvError::Closed) => break,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_conversation::{manager::AgentRuntimeManager, providers::ProviderRegistry};
    use std::sync::{atomic::{AtomicUsize, Ordering}, Arc};

    fn store_with_a_waiting_card() -> SessionStore {
        let store = SessionStore::open_in_memory().unwrap();
        store.board_update("one", "lead", false, &json!({"title": "Title", "state": "needs_you", "steps": ["Merge it"]}), 1).unwrap();
        store
    }

    #[test]
    fn successful_agent_and_owner_writes_signal_the_board_but_refused_writes_do_not() {
        // The broker callback is process-wide and set once; isolate it from server tests.
        if std::env::var_os("ASSEMBLY_BOARD_SIGNAL_TEST").is_none() {
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact", concat!(module_path!(), "::successful_agent_and_owner_writes_signal_the_board_but_refused_writes_do_not"), "--nocapture"])
                .env("ASSEMBLY_BOARD_SIGNAL_TEST", "1").output().unwrap();
            assert!(output.status.success(), "{}\n{}", String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr));
            return;
        }
        let fired = Arc::new(AtomicUsize::new(0));
        let counter = fired.clone();
        mcb_core::broker::on_messages_changed(move || { counter.fetch_add(1, Ordering::SeqCst); });
        let store = SessionStore::open_in_memory().unwrap();
        update(&store, "lead", "top", json!({"key": "one", "title": "Title", "state": "needs_you"})).unwrap();
        assert_eq!(fired.load(Ordering::SeqCst), 1);
        assert!(update(&store, "helper", "helper", json!({"key": "one", "state": "done"})).is_err());
        assert!(update(&store, "other", "top", json!({"key": "one", "state": "done"})).is_err());
        assert_eq!(fired.load(Ordering::SeqCst), 1);
        answer(&store, "board_note", &json!({"target": "card:one", "body": "Ready", "now_ms": 3})).unwrap();
        assert_eq!(fired.load(Ordering::SeqCst), 2);
        answer(&store, "board_card", &json!({"key": "one", "action": "done", "at": 4})).unwrap();
        assert_eq!(fired.load(Ordering::SeqCst), 3);
        answer(&store, "board_card", &json!({"key": "missing", "action": "done", "at": 4})).unwrap();
        assert_eq!(fired.load(Ordering::SeqCst), 3);
        let id = update(&store, "lead", "top", json!({"key": "q", "title": "Merge?", "question": "Why"})).unwrap()["question"].as_i64().unwrap();
        assert_eq!(fired.load(Ordering::SeqCst), 4);
        let asked = store.board(0, 5).unwrap()["questions"][0]["asked_at_ms"].as_i64().unwrap();
        assert_eq!(answer(&store, "board_question", &json!({"id": id, "asked_at_ms": asked, "answer": "Q1: yes", "now_ms": 5})).unwrap(), json!(true));
        assert_eq!(fired.load(Ordering::SeqCst), 5);
        assert_eq!(answer(&store, "board_question", &json!({"id": id, "asked_at_ms": asked, "answer": "Q1: no", "now_ms": 6})).unwrap(), json!(false));
        assert_eq!(fired.load(Ordering::SeqCst), 5);
    }

    #[test]
    fn board_tool_schema_has_notify() {
        let schema = serde_json::to_value(schemars::schema_for!(BoardUpdate)).unwrap();
        assert!(schema["properties"]["notify"]["description"].as_str().unwrap().starts_with("Main orchestrator only."));
        assert!(DESCRIPTION.contains("only the main orchestrator can pass notify"));
    }

    #[test]
    fn notify_is_capped_until_the_owner_answers() {
        let store = SessionStore::open_in_memory().unwrap();
        let id = update(&store, "lead", "top", json!({"key": "q", "title": "Merge?", "question": "Why"})).unwrap()["question"].as_i64().unwrap();
        let notify = |key: &str| update(&store, "lead", "orchestrator", json!({"key": key, "title": "Ready", "state": "needs_you", "notify": true})).unwrap();
        let replies: Vec<Value> = (0..6).map(|n| notify(&format!("card-{n}"))).collect();
        assert_eq!(store.board_notified_since(0).unwrap().len(), 3, "six notify updates send three");
        assert_eq!(replies[3], json!({"key": "card-3", "saved": true,
            "phone": "phone limit reached: the owner hasn't answered; 3 in 30 minutes, 5 in an hour"}));
        let asked = store.board(0, i64::MAX).unwrap()["questions"][0]["asked_at_ms"].as_i64().unwrap();
        answer(&store, "board_question", &json!({"id": id, "asked_at_ms": asked, "answer": "yes", "now_ms": 1})).unwrap();
        assert_eq!(notify("after"), json!({"key": "after", "saved": true}));
        assert_eq!(store.board_notified_since(0).unwrap().len(), 4, "an answer resets the count");
        for n in 0..2 { notify(&format!("more-{n}")); }
        assert!(notify("full").get("phone").is_some());
        update(&store, "lead", "orchestrator", json!({"key": "q", "answer": "yes, from the chat"})).unwrap();
        assert_eq!(notify("after-chat"), json!({"key": "after-chat", "saved": true}), "an answer recorded from the chat resets it too");
    }

    #[test]
    fn notify_from_a_non_orchestrator_session_does_not_push() {
        let store = SessionStore::open_in_memory().unwrap();
        let reply = update(&store, "lead", "top", json!({"key": "one", "title": "Ready", "state": "needs_you", "notify": true})).unwrap();
        assert_eq!(reply, json!({"key": "one", "saved": true,
            "phone": "only the main orchestrator can message the owner's phone; the board has notified the desktop"}));
        assert_eq!(store.board(0, i64::MAX).unwrap()["cards"][0]["key"], "one", "the card is saved");
        assert_eq!(store.board_notified_since(0).unwrap(), Vec::<Value>::new(), "nothing goes to the phone");
    }

    #[test]
    fn a_recorded_question_replies_with_its_number() {
        let store = SessionStore::open_in_memory().unwrap();
        let reply = update(&store, "lead", "top", json!({"key": "q", "title": "Merge?", "question": "Why", "recommended": "yes"})).unwrap();
        let board = answer(&store, "board", &json!({"today_ms": 0, "now_ms": 5})).unwrap();
        assert_eq!(reply, json!({"key": "q", "saved": true, "question": board["questions"][0]["id"]}));
        assert_eq!(update(&store, "helper", "helper", json!({"key": "q", "answer": "yes"})).unwrap_err(), "helpers cannot post to the board");
    }

    #[test]
    fn a_title_only_call_on_a_question_key_does_not_touch_the_question() {
        let store = SessionStore::open_in_memory().unwrap();
        update(&store, "lead", "top", json!({"key": "q", "title": "Merge?", "question": "Why"})).unwrap();
        update(&store, "lead", "top", json!({"key": "q", "answer": "yes"})).unwrap();
        assert_eq!(update(&store, "lead", "top", json!({"key": "q", "title": "Other"})).unwrap_err(), "title and state are required for a new card");
        let board = answer(&store, "board", &json!({"today_ms": 0, "now_ms": 5})).unwrap();
        assert_eq!((&board["questions"][0]["title"], &board["questions"][0]["answer"]), (&json!("Merge?"), &json!("yes")));
        assert_eq!(board["cards"], json!([]));
    }

    #[tokio::test]
    async fn an_owner_answer_needs_its_id_and_its_machine() {
        let store = Arc::new(SessionStore::open_in_memory().unwrap());
        assert_eq!(answer(&store, "board_question", &json!({"answer": "yes", "now_ms": 1})).unwrap_err(), "id is required");
        let connections = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store).unwrap();
        assert_eq!(board_op(&connections, "board_question", json!({"id": 1, "answer": "yes", "now_ms": 1}), 1).await.unwrap_err(), "machine is required");
    }

    #[test]
    fn board_and_board_count_answer_with_the_machines_cards() {
        let store = store_with_a_waiting_card();
        let board = answer(&store, "board", &json!({"today_ms": 0, "now_ms": 5})).unwrap();
        assert_eq!(board["cards"][0]["key"], "one");
        assert_eq!(answer(&store, "board_count", &json!({"now_ms": 5})).unwrap()["count"], 1);
    }

    #[test]
    fn a_card_action_and_an_owner_note_change_the_board() {
        let store = store_with_a_waiting_card();
        let note = json!({"target": "card:one", "body": "Looks right", "now_ms": 3});
        assert_eq!(answer(&store, "board_note", &note).unwrap(), json!(true));
        let board = answer(&store, "board", &json!({"today_ms": 0, "now_ms": 5})).unwrap();
        assert_eq!(board["cards"][0]["notes"][0]["body"], "Looks right");
        assert_eq!(answer(&store, "board_card", &json!({"key": "one", "action": "done", "at": 4})).unwrap(), json!(true));
        assert_eq!(answer(&store, "board_count", &json!({"now_ms": 5})).unwrap()["count"], 0);
        assert_eq!(answer(&store, "board_card", &json!({"key": "", "action": "clear_done", "at": 6})).unwrap(), json!(true));
        assert_eq!(answer(&store, "board", &json!({"today_ms": 0, "now_ms": 7})).unwrap()["cards"], json!([]));
    }

    #[test]
    fn a_board_operation_without_its_arguments_or_with_a_wrong_name_is_refused() {
        let store = store_with_a_waiting_card();
        assert_eq!(answer(&store, "board_count", &json!({})).unwrap_err(), "now_ms is required");
        assert_eq!(answer(&store, "board_card", &json!({"key": "one", "action": "explode", "at": 1})).unwrap_err(), "unknown board card action");
        assert_eq!(answer(&store, "board_nope", &json!({})).unwrap_err(), "Unknown messaging operation board_nope");
    }

    #[test]
    fn snoozing_dropping_and_undoing_change_the_count_without_losing_the_card() {
        let store = store_with_a_waiting_card();
        for (action, at, count) in [("snooze", 99, 0), ("drop", 6, 0), ("undo", 7, 0), ("snooze", 0, 1)] {
            assert_eq!(answer(&store, "board_card", &json!({"key": "one", "action": action, "at": at})).unwrap(), json!(true));
            assert_eq!(answer(&store, "board_count", &json!({"now_ms": 5})).unwrap()["count"], count);
        }
        assert_eq!(answer(&store, "board", &json!({"today_ms": 0, "now_ms": 5})).unwrap()["cards"][0]["key"], "one");
    }

    #[test]
    fn the_messaging_router_sends_board_operations_here() {
        let store = store_with_a_waiting_card();
        let count = super::super::answer(&store, None, "board_count", &json!({"now_ms": 5})).unwrap();
        assert_eq!(count["count"], 1);
    }

    #[tokio::test]
    async fn the_desktop_reads_its_board_and_routes_actions_to_the_named_machine() {
        let directory = std::env::temp_dir().join(format!("assembly-board-ops-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let desktop = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("desktop.db")).unwrap();
        desktop.store().board_update("one", "lead", false, &json!({"title": "Title", "state": "needs_you"}), 1).unwrap();
        let connections = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), desktop.store_handle()).unwrap();
        connections.set_message_host(desktop.clone());

        let counts = board_op(&connections, "board_count", json!({"now_ms": 5}), 1).await.unwrap();
        assert_eq!(counts, json!({"count": 1, "snoozed_until_ms": null}));
        let boards = board_op(&connections, "board", json!({"today_ms": 0, "now_ms": 5}), 1).await.unwrap();
        assert_eq!(boards[0]["machine"], "mac");
        assert_eq!(boards[0]["value"]["cards"][0]["key"], "one");
        board_op(&connections, "board_card", json!({"machine": "mac", "key": "one", "action": "snooze", "at": 99}), 2).await.unwrap();
        assert_eq!(board_op(&connections, "board_count", json!({"now_ms": 5}), 3).await.unwrap(), json!({"count": 0, "snoozed_until_ms": 99}));
        let done = board_op(&connections, "board_card", json!({"machine": "mac", "key": "one", "action": "done", "at": 4}), 2).await.unwrap();
        assert_eq!(done, json!(true));
        assert_eq!(board_op(&connections, "board_count", json!({"now_ms": 5}), 3).await.unwrap()["count"], 0);
        let error = board_op(&connections, "board_card", json!({"machine": "away", "key": "one", "action": "done", "at": 4}), 4).await.unwrap_err();
        assert!(error.contains("not reachable"), "{error}");
        std::fs::remove_dir_all(&directory).unwrap();
    }

    #[tokio::test]
    async fn an_unavailable_machine_is_omitted_from_reads_but_an_action_needs_a_machine() {
        let store = Arc::new(SessionStore::open_in_memory().unwrap());
        let connections = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store).unwrap();
        assert_eq!(board_op(&connections, "board", json!({"today_ms": 0, "now_ms": 5}), 1).await.unwrap(), json!([]));
        assert_eq!(board_op(&connections, "board_count", json!({"now_ms": 5}), 2).await.unwrap(), json!({"count": 0, "snoozed_until_ms": null}));
        assert_eq!(board_op(&connections, "board_card", json!({"key": "one", "action": "done", "at": 4}), 3).await.unwrap_err(), "machine is required");
    }

    #[tokio::test]
    async fn run_changes_fire_the_signal_even_after_missed_ones_and_forwarding_ends_when_the_engine_goes() {
        let (changes, runs) = tokio::sync::broadcast::channel(2);
        for run in 0..5 { changes.send((format!("run-{run}"), 0)).unwrap(); }
        let fired = Arc::new(AtomicUsize::new(0));
        let counter = fired.clone();
        drop(changes);
        forward_run_changes(runs, move || { counter.fetch_add(1, Ordering::SeqCst); }).await;
        // One call for the three changes the receiver missed, then one for each of the two it kept.
        assert_eq!(fired.load(Ordering::SeqCst), 3);
    }
}
