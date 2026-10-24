use super::*;
use crate::agent_conversation::agui::ResumeEntry;
use crate::agent_conversation::protocol::{AgentConfigOptionChoice, AgentConversationProvider, PlanItem};

const THREAD: &str = "session-1";

/// One open view on one session, fed native events in sequence order.
struct Script {
    views: AguiProjector,
    sequence: i64,
}

impl Script {
    fn open() -> Self {
        let mut views = AguiProjector::default();
        views.begin_open(THREAD);
        views.finish_open(THREAD, Some((&[], &HashSet::new(), 0, false)));
        Self { views, sequence: 0 }
    }

    fn event(&mut self, payload: Payload) -> AgentConversationEvent {
        self.sequence += 1;
        event(self.sequence, payload)
    }

    fn send(&mut self, payload: Payload) -> Vec<Value> {
        let event = self.event(payload);
        self.views.project(&event)
    }

    fn run(&mut self, run_id: &str, parent: Option<&str>, resume: Vec<(&str, ResumeStatus, Value)>) -> Result<Vec<Answer>, String> {
        self.views.start_run(&RunAgentInput {
            thread_id: THREAD.into(), run_id: run_id.into(), parent_run_id: parent.map(str::to_string),
            resume: resume.into_iter().map(|(id, status, payload)| ResumeEntry { interrupt_id: id.into(), status, payload }).collect(),
            message: None, message_id: None,
        })
    }
}

fn event(sequence: i64, payload: Payload) -> AgentConversationEvent {
    AgentConversationEvent {
        runtime_state: None, owned_id: THREAD.into(), provider: AgentConversationProvider::Claude, generation: 2,
        sequence, timestamp_ms: sequence as u128 * 1000, turn_id: Some("turn-1".into()), payload,
    }
}

fn types(chunks: &[Value]) -> Vec<&str> {
    chunks.iter().map(|chunk| chunk["type"].as_str().unwrap()).collect()
}

/// What the stock client needs: each run starts once and ends once (an
/// interrupt only pauses it), content comes while a run is live, and every
/// message and call is closed before the last run ends (a call may span a
/// pause: the stock client completes open calls at the interrupt).
fn assert_well_formed(chunks: &[Value]) {
    let (mut started, mut ended, mut live, mut open) = (HashSet::new(), HashSet::new(), HashSet::new(), HashSet::new());
    for chunk in chunks {
        let run = chunk["runId"].as_str().unwrap_or_default().to_string();
        let item = chunk["messageId"].as_str().or(chunk["toolCallId"].as_str()).unwrap_or_default().to_string();
        match chunk["type"].as_str().unwrap() {
            "RUN_STARTED" => assert!(started.insert(run.clone()) && live.insert(run), "a run started twice: {chunk}"),
            "RUN_FINISHED" if chunk["outcome"]["type"] == "interrupt" => {
                assert!(started.contains(&run) && !ended.contains(&run), "a pause outside its run: {chunk}");
                live.remove(&run);
            }
            "RUN_FINISHED" | "RUN_ERROR" => {
                assert!(started.contains(&run) && ended.insert(run.clone()), "a run ended twice or never started: {chunk}");
                live.remove(&run);
            }
            "TEXT_MESSAGE_END" | "TOOL_CALL_END" => {
                assert!(started.len() > ended.len() && open.remove(&item), "an end outside a run or for nothing open: {chunk}");
            }
            "CUSTOM" | "ACTIVITY_SNAPSHOT" | "SUBAGENT_STARTED" | "SUBAGENT_FINISHED" | "SUBAGENT_ERROR" => {}
            kind => {
                assert!(!live.is_empty(), "{kind} outside a live run: {chunk}");
                if kind.ends_with("_START") {
                    open.insert(item);
                }
            }
        }
    }
    assert_eq!(started, ended, "every run ends");
    assert!(open.is_empty(), "{open:?} still open after the last run ended");
}

fn turn(state: TurnState) -> Payload { Payload::Turn { turn_id: "turn-1".into(), state } }
fn delta(id: &str, text: &str) -> Payload { Payload::AssistantDelta { item_id: id.into(), delta: text.into() } }
fn reply(id: &str, text: &str) -> Payload { Payload::AssistantMessage { item_id: id.into(), text: text.into(), completed: true, blocks: None } }
fn user(id: &str, text: &str) -> Payload { Payload::UserMessage { item_id: id.into(), text: text.into(), completed: true, attachment_ids: vec!["shot-1".into()] } }
fn tool(id: &str, state: ToolState, summary: Option<&str>, output: Option<&str>) -> Payload {
    Payload::Tool { item_id: id.into(), name: "Write".into(), state, summary: summary.map(str::to_string),
        output: output.map(str::to_string), path: summary.map(|_| "notes/hello.txt".into()), diff: None, has_detail: false, added: None, removed: None }
}
fn permission(id: &str, state: ApprovalState) -> Payload {
    let options = if state == ApprovalState::Requested {
        vec![("allow-once".into(), "Yes".into(), "allow_once".into()), ("reject".into(), "No".into(), "reject_once".into())]
    } else {
        Vec::new()
    };
    Payload::Approval { request_id: id.into(), state, summary: "Write notes/hello.txt".into(), options }
}
fn question(id: &str) -> Payload {
    let choice = |value: Value, label: &str| AgentConfigOptionChoice { value, label: label.into(), description: None };
    Payload::UserInputRequested {
        request_id: id.into(), title: "How much may this spend?".into(), description: Some("Pick a budget".into()), can_decline: true,
        fields: vec![AgentUserInputField {
            id: "budget".into(), label: "Budget".into(), description: None, required: true, kind: AgentUserInputKind::Select,
            choices: Some(vec![choice(json!(0.25), "A quarter"), choice(json!(100.0), "A hundred"), choice(json!(12345678901234567890u64), "Unlimited")]),
        }],
    }
}

/// The client starts a turn, the agent asks to write a file, and the client
/// answers through `resume`; the continuation names the paused run.
fn permission_scenario(choice: &str) -> (Script, Vec<Value>) {
    let mut script = Script::open();
    let mut chunks = Vec::new();
    script.run("run-client-1", None, Vec::new()).unwrap();
    chunks.extend(script.send(user("user-1", "Write hello into notes/hello.txt")));
    chunks.extend(script.send(turn(TurnState::Started)));
    chunks.extend(script.send(tool("tool-1", ToolState::Started, None, None)));
    chunks.extend(script.send(permission("perm-1", ApprovalState::Requested)));
    let answers = script.run("run-client-2", Some("run-client-1"), vec![("perm-1", ResumeStatus::Resolved, json!({ "optionId": choice }))]).unwrap();
    assert_eq!(answers, vec![Answer::Permission { request_id: "perm-1".into(), generation: 2, option_id: choice.into() }]);
    let accepted = if choice == "reject" { ApprovalState::Declined } else { ApprovalState::Accepted };
    chunks.extend(script.send(permission("perm-1", accepted)));
    let state = if choice == "reject" { ToolState::Failed } else { ToolState::Completed };
    let output = if choice == "reject" { "User refused permission to run tool" } else { "notes/hello.txt" };
    chunks.extend(script.send(tool("tool-1", state, Some("Write notes/hello.txt"), Some(output))));
    chunks.extend(script.send(delta("msg-1", "I created ")));
    chunks.extend(script.send(delta("msg-1", "notes/hello.txt.")));
    chunks.extend(script.send(reply("msg-1", "I created notes/hello.txt.")));
    chunks.extend(script.send(Payload::Usage { input_tokens: Some(1200), output_tokens: Some(80), used_tokens: None, context_window: None, total_tokens: Some(1280) }));
    chunks.extend(script.send(turn(TurnState::Completed)));
    (script, chunks)
}

#[test]
fn a_permission_pauses_the_run_and_the_client_resume_continues_it() {
    let (_, chunks) = permission_scenario("allow-once");
    assert_well_formed(&chunks);
    assert_eq!(types(&chunks), [
        "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "TOOL_CALL_START", "RUN_FINISHED",
        "RUN_FINISHED", "RUN_STARTED", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
        "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM", "RUN_FINISHED",
    ]);
    assert_eq!(chunks[0]["runId"], "run-client-1");
    assert!(chunks[0].get("parentRunId").is_none(), "a first run has no parent field: {}", chunks[0]);
    assert_eq!(chunks[1]["metadata"]["attachmentIds"], json!(["shot-1"]));
    let pause = &chunks[5];
    assert_eq!(pause["outcome"]["type"], "interrupt");
    let interrupt = &pause["outcome"]["interrupts"][0];
    assert_eq!(interrupt["responseSchema"]["properties"]["optionId"]["enum"], json!(["allow-once", "reject"]));
    let binding = &interrupt["metadata"]["tanstack:interruptBinding"];
    assert_eq!(binding["interruptedRunId"], "run-client-1");
    assert_eq!(binding["generation"], 2);
    assert_eq!(binding["responseSchemaHash"], agui::response_schema_hash(&interrupt["responseSchema"]));
    // Even the client's own answer finishes the paused run at once; its next run continues it.
    assert_eq!(chunks[6]["runId"], "run-client-1");
    assert!(chunks[6].get("outcome").is_none(), "{}", chunks[6]);
    assert_eq!(chunks[7], json!({ "type": "RUN_STARTED", "threadId": THREAD, "runId": "run-client-2", "parentRunId": "run-client-1" }));
    // The reply the provider re-sends whole at the end adds nothing.
    assert_eq!(chunks[12]["delta"], "I created ");
    assert_eq!(chunks[13]["delta"], "notes/hello.txt.");
    assert_eq!(chunks[16]["usage"], json!([{ "inputTokens": 1200, "outputTokens": 80, "totalTokens": 1280, "provider": "claude" }]));
}

#[test]
fn a_refused_call_keeps_its_error_on_the_call_and_the_run_finishes_cleanly() {
    let (_, chunks) = permission_scenario("reject");
    let result = chunks.iter().find(|chunk| chunk["type"] == "TOOL_CALL_RESULT").unwrap();
    assert_eq!(result["metadata"]["tanstack"]["state"], "output-error");
    assert!(!types(&chunks).contains(&"RUN_ERROR"));
}

/// Every surface answers through the manager (`respond_permission_selection`,
/// `respond_user_input`), which records the same resolution event: the
/// approval card and the inbox or board record it locally, the phone through
/// its route, a remote backend over the wire. Each must end the paused run.
#[test]
fn an_answer_from_any_surface_ends_the_paused_run_at_once() {
    let resolutions = [
        ("permission accepted on the card, inbox or phone", permission("perm-1", ApprovalState::Accepted)),
        ("permission declined", permission("perm-1", ApprovalState::Declined)),
        ("question answered", Payload::UserInputResolved { request_id: "perm-1".into(), cancelled: false }),
    ];
    for (surface, resolution) in resolutions {
        let mut script = Script::open();
        script.send(turn(TurnState::Started));
        let request = if matches!(resolution, Payload::UserInputResolved { .. }) { question("perm-1") } else { permission("perm-1", ApprovalState::Requested) };
        let pause = script.send(request);
        let paused_run = pause[0]["runId"].as_str().unwrap().to_string();
        let answered = script.send(resolution);
        assert_eq!(types(&answered), ["RUN_FINISHED"], "{surface}");
        assert_eq!(answered[0]["runId"], paused_run.as_str(), "{surface}");
        assert!(answered[0].get("outcome").is_none(), "{surface}: {}", answered[0]);
        let continued = script.send(delta("msg-1", "Done."));
        assert_eq!(continued[0]["type"], "RUN_STARTED", "{surface}");
        assert_eq!(continued[0]["parentRunId"], paused_run.as_str(), "{surface}");
    }
}

#[test]
fn a_session_that_dies_while_a_request_waits_ends_the_paused_run() {
    for state in [ConversationConnectionState::Failed, ConversationConnectionState::Disconnected, ConversationConnectionState::Closed] {
        let mut script = Script::open();
        script.send(turn(TurnState::Started));
        let pause = script.send(permission("perm-1", ApprovalState::Requested));
        let ended = script.send(Payload::Connection { state, native_session_id: None });
        assert_eq!(types(&ended), ["RUN_FINISHED", "CUSTOM"], "{state:?}");
        assert_eq!(ended[0]["runId"], pause[0]["runId"]);
        assert_eq!(ended[0]["outcome"]["type"], "cancelled");
        assert!(script.send(permission("perm-1", ApprovalState::Expired)).is_empty(), "nothing is left open to end");
    }
    // A run with no pause ends the same way.
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(delta("msg-1", "Half a"));
    let ended = script.send(Payload::Connection { state: ConversationConnectionState::Failed, native_session_id: None });
    assert_eq!(types(&ended), ["TEXT_MESSAGE_END", "RUN_FINISHED", "CUSTOM"], "the half reply ends before its run");
    assert_eq!(ended[1]["outcome"]["type"], "cancelled");
}

/// An expired permission or a cancelled question is withdrawn, not answered:
/// on Stop or a failure the turn's end follows, so the pause ends only when
/// something comes next, as cancelled when it is the turn's end.
#[test]
fn a_withdrawn_request_ends_its_pause_only_when_the_turn_goes_on() {
    for withdrawn in [permission("perm-1", ApprovalState::Expired), Payload::UserInputResolved { request_id: "perm-1".into(), cancelled: true }] {
        let mut script = Script::open();
        let mut chunks = script.send(turn(TurnState::Started));
        let request = if matches!(withdrawn, Payload::UserInputResolved { .. }) { question("perm-1") } else { permission("perm-1", ApprovalState::Requested) };
        chunks.extend(script.send(request));
        let paused_run = chunks[0]["runId"].clone();
        assert!(script.send(withdrawn).is_empty());
        // A question its user declined: the agent goes on.
        let continued = script.send(delta("msg-1", "Skipping that."));
        assert_eq!(types(&continued), ["RUN_FINISHED", "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT"]);
        assert_eq!((&continued[0]["runId"], continued[0].get("outcome")), (&paused_run, None));
        assert_eq!(continued[1]["parentRunId"], paused_run);
        chunks.extend(continued);
        chunks.extend(script.send(turn(TurnState::Completed)));
        assert_well_formed(&chunks);
    }
}

/// Stop with a reply streaming and a call waiting on a permission: the manager
/// expires the request, re-sends the reply whole, then ends the turn.
#[test]
fn a_stop_while_paused_ends_what_is_open_then_cancels_the_paused_run() {
    let mut script = Script::open();
    let mut chunks = script.send(turn(TurnState::Started));
    chunks.extend(script.send(delta("msg-1", "Working")));
    chunks.extend(script.send(tool("tool-1", ToolState::Started, None, None)));
    chunks.extend(script.send(permission("perm-1", ApprovalState::Requested)));
    let stopped: Vec<Value> = [permission("perm-1", ApprovalState::Expired), reply("msg-1", "Working"), turn(TurnState::Interrupted)]
        .into_iter().flat_map(|payload| script.send(payload)).collect();
    assert_eq!(types(&stopped), ["TEXT_MESSAGE_END", "TOOL_CALL_END", "RUN_FINISHED"], "no run starts after the pause");
    assert_eq!((&stopped[2]["runId"], &stopped[2]["outcome"]["type"]), (&chunks[0]["runId"], &json!("cancelled")));
    chunks.extend(stopped);
    assert_well_formed(&chunks);
}

#[test]
fn a_failure_while_paused_is_a_run_error_on_the_paused_run() {
    let mut script = Script::open();
    let mut chunks = script.send(turn(TurnState::Started));
    chunks.extend(script.send(question("ask-1")));
    let failed: Vec<Value> = [Payload::Error { code: "provider_error".into(), message: "The model is overloaded".into(), recoverable: false },
        Payload::UserInputResolved { request_id: "ask-1".into(), cancelled: true }, turn(TurnState::Failed)]
        .into_iter().flat_map(|payload| script.send(payload)).collect();
    assert_eq!(types(&failed), ["ACTIVITY_SNAPSHOT", "RUN_ERROR"]);
    assert_eq!((&failed[1]["runId"], &failed[1]["message"]), (&chunks[0]["runId"], &json!("The model is overloaded")));
    chunks.extend(failed);
    assert_well_formed(&chunks);
}

#[test]
fn calls_and_child_agents_still_open_are_closed_when_the_turn_ends() {
    let mut script = Script::open();
    let child = |id: &str, state: &str| Payload::ChildUpdate { child_id: id.into(), parent_tool_call_id: String::new(), parent_id: None,
        transcript_id: None, label: None, state: state.into(), latest_activity: None };
    let mut chunks = script.send(turn(TurnState::Started));
    chunks.extend(script.send(tool("tool-1", ToolState::Started, None, None)));
    chunks.extend(script.send(child("child-1", "running")));
    chunks.extend(script.send(child("child-2", "running")));
    assert_eq!(types(&script.send(child("child-2", "cancelled"))), ["SUBAGENT_FINISHED", "CUSTOM"]);
    let ended = script.send(turn(TurnState::Completed));
    assert_eq!(types(&ended), ["TOOL_CALL_END", "SUBAGENT_FINISHED", "RUN_FINISHED"]);
    assert_eq!(ended[1]["subagentRunId"], "child-1");
    chunks.extend(ended);
    assert_well_formed(&chunks);
}

#[test]
fn a_client_run_survives_a_reopen_and_does_not_outlive_its_turn() {
    let mut script = Script::open();
    script.run("run-client-1", None, Vec::new()).unwrap();
    script.views.begin_open(THREAD);
    let history = [event(1, user("user-0", "Earlier")), event(2, turn(TurnState::Completed))];
    script.views.finish_open(THREAD, Some((&history, &HashSet::new(), 2, false)));
    script.sequence = 2;
    assert_eq!(script.send(user("user-1", "Now"))[0]["runId"], "run-client-1", "the replayed history does not use it up");
    // A steer's run while one is open is not used by a later turn.
    script.run("run-client-2", None, Vec::new()).unwrap();
    script.send(turn(TurnState::Completed));
    assert_eq!(script.send(user("user-2", "From the phone"))[0]["runId"], "run-5");
}

/// A snapshot's events carry no turn boundaries, so its last turn looks open.
#[test]
fn the_turn_after_a_reopen_starts_its_own_run() {
    let mut script = Script::open();
    script.views.begin_open(THREAD);
    let history = [event(1, user("user-0", "Earlier")), event(2, reply("msg-0", "Done earlier."))];
    script.views.finish_open(THREAD, Some((&history, &HashSet::new(), 2, false)));
    script.sequence = 2;
    script.run("run-client-1", None, Vec::new()).unwrap();
    let mut chunks = script.send(user("user-1", "Now"));
    assert_eq!((&chunks[0]["type"], &chunks[0]["runId"]), (&json!("RUN_STARTED"), &json!("run-client-1")));
    chunks.extend(script.send(turn(TurnState::Completed)));
    assert_well_formed(&chunks);
}

#[test]
fn content_during_a_pause_comes_inside_a_run() {
    let mut script = Script::open();
    let mut chunks = script.send(turn(TurnState::Started));
    chunks.extend(script.send(tool("tool-1", ToolState::Started, Some("Write notes/hello.txt"), None)));
    chunks.extend(script.send(permission("perm-1", ApprovalState::Requested)));
    let parallel = script.send(tool("tool-1", ToolState::Completed, Some("Write notes/hello.txt"), Some("done")));
    assert_eq!(types(&parallel), ["RUN_STARTED", "TOOL_CALL_END", "TOOL_CALL_RESULT"]);
    assert!(parallel[0].get("parentRunId").is_none(), "it does not answer the pause: {}", parallel[0]);
    chunks.extend(parallel);
    chunks.extend(script.send(permission("perm-1", ApprovalState::Accepted)));
    chunks.extend(script.send(turn(TurnState::Completed)));
    assert_well_formed(&chunks);
}

#[test]
fn a_closed_session_drops_its_view_and_child_agents_are_kept_per_turn() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(Payload::ChildUpdate { child_id: "child-1".into(), parent_tool_call_id: String::new(), parent_id: None,
        transcript_id: None, label: None, state: "running".into(), latest_activity: None });
    script.send(turn(TurnState::Completed));
    assert!(script.views.views[THREAD].children.is_empty());
    script.send(Payload::Connection { state: ConversationConnectionState::Closed, native_session_id: None });
    assert!(!script.views.views.contains_key(THREAD));
    // A view its user leaves stops projecting; the session goes on.
    let mut script = Script::open();
    script.views.close(THREAD);
    assert!(script.send(turn(TurnState::Started)).is_empty());
}

#[test]
fn a_running_call_streams_its_output_and_ends_with_the_closed_row_only() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let call = |state: ToolState, output: Option<&str>, diff: Option<&str>| Payload::Tool { item_id: "tool-1".into(), name: "Edit".into(), state,
        summary: None, output: output.map(str::to_string), path: None, diff: diff.map(str::to_string), has_detail: false, added: None, removed: None };
    script.send(call(ToolState::Started, None, None));
    let content = |chunk: &Value| serde_json::from_str::<Value>(chunk["content"].as_str().unwrap()).unwrap();
    let running = script.send(call(ToolState::Updated, Some("Edited notes.txt"), Some("+hello")));
    assert_eq!((types(&running), content(&running[0])["output"].clone()), (vec!["TOOL_CALL_RESULT"], json!("Edited notes.txt")));
    let done = script.send(call(ToolState::Completed, None, None));
    // Output and diff are read by id when the row opens (TSK-1574); a replayed history row keeps saying it has them.
    assert_eq!((types(&done), content(&done[1])), (vec!["TOOL_CALL_END", "TOOL_CALL_RESULT"], json!({ "kind": "tool", "itemId": "tool-1",
        "name": "Edit", "state": "completed", "summary": "Edited notes.txt", "hasDetail": true, "added": 1, "removed": 0 })));
    let replayed = script.send(Payload::Tool { item_id: "tool-2".into(), name: "Edit".into(), state: ToolState::Completed,
        summary: None, output: None, path: None, diff: None, has_detail: true, added: Some(1), removed: Some(0) });
    assert_eq!(content(replayed.last().unwrap())["hasDetail"], json!(true));
    // Imported history sends the finished call's output in a later event with no name.
    let imported = script.send(Payload::Tool { item_id: "tool-2".into(), name: String::new(), state: ToolState::Completed,
        summary: None, output: Some("2 files".into()), path: None, diff: None, has_detail: false, added: None, removed: None });
    assert_eq!((types(&imported), content(&imported[0])["hasDetail"].clone()), (vec!["TOOL_CALL_RESULT"], json!(true)));
}

#[test]
fn arguments_that_change_before_a_call_ends_reach_the_client() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let task = |state: ToolState, summary: &str| Payload::Tool { item_id: "background-task:1".into(), name: "Background command".into(), state,
        summary: Some(summary.into()), output: None, path: None, diff: None, has_detail: false, added: None, removed: None };
    assert_eq!(types(&script.send(task(ToolState::Started, "npm test"))), ["TOOL_CALL_START", "TOOL_CALL_ARGS"]);
    assert!(script.send(task(ToolState::Updated, "npm test: 40 of 90")).is_empty());
    let done = script.send(task(ToolState::Completed, "Completed"));
    assert_eq!(types(&done), ["TOOL_CALL_END", "TOOL_CALL_RESULT"]);
    assert_eq!(done[0]["input"], json!({ "summary": "Completed" }));
}

#[test]
fn withdrawing_one_of_two_requests_shows_the_other_again() {
    let mut script = Script::open();
    [turn(TurnState::Started), permission("perm-1", ApprovalState::Requested), question("ask-1")].map(|payload| script.send(payload));
    let rest = &script.send(permission("perm-1", ApprovalState::Expired))[0]["outcome"]["interrupts"];
    assert_eq!((rest.as_array().unwrap().len(), &rest[0]["id"]), (1, &json!("ask-1")));
}

#[test]
fn a_second_request_joins_the_pause_and_the_first_answer_leaves_the_other_open() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(permission("perm-1", ApprovalState::Requested));
    let both = script.send(question("ask-1"));
    assert_eq!(both[0]["outcome"]["interrupts"].as_array().unwrap().len(), 2);
    let rest = script.send(permission("perm-1", ApprovalState::Accepted));
    assert_eq!(rest[0]["outcome"]["interrupts"][0]["id"], "ask-1");
    assert_eq!(rest[0]["runId"], both[0]["runId"]);
}

#[test]
fn a_question_takes_its_answer_as_an_object_and_a_cancel_as_cancel() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let pause = script.send(question("ask-1"));
    let schema = &pause[0]["outcome"]["interrupts"][0]["responseSchema"];
    assert_eq!(schema["properties"]["budget"]["enum"], json!([0.25, 100.0, 12345678901234567890u64]));
    assert_eq!(schema["required"], json!(["budget"]));
    let answer = script.run("run-2", Some("run-1"), vec![("ask-1", ResumeStatus::Resolved, json!({ "budget": 0.25 }))]).unwrap();
    assert_eq!(answer, vec![Answer::Input { request_id: "ask-1".into(), generation: 2, action: AgentUserInputAction::Accept,
        content: BTreeMap::from([("budget".to_string(), json!(0.25))]) }]);
    let cancelled = answer_of(&script, "ask-1", ResumeStatus::Cancelled, Value::Null);
    assert_eq!(cancelled, Answer::Input { request_id: "ask-1".into(), generation: 2, action: AgentUserInputAction::Cancel, content: BTreeMap::new() });
}

fn answer_of(script: &Script, id: &str, status: ResumeStatus, payload: Value) -> Answer {
    let view = &script.views.views[THREAD];
    answer(view.requests.iter().find(|request| request.id == id).unwrap(), status, &payload).unwrap()
}

#[test]
fn resume_refuses_an_unknown_interrupt_or_an_option_that_was_not_offered() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(permission("perm-1", ApprovalState::Requested));
    assert!(script.run("run-2", None, vec![("perm-9", ResumeStatus::Resolved, json!({ "optionId": "allow-once" }))]).is_err());
    assert!(script.run("run-2", None, vec![("perm-1", ResumeStatus::Resolved, json!({ "optionId": "allow-forever" }))]).is_err());
    assert_eq!(answer_of(&script, "perm-1", ResumeStatus::Cancelled, Value::Null),
        Answer::Approval { request_id: "perm-1".into(), generation: 2, accept: false });
}

#[test]
fn a_legacy_approval_offers_allow_and_deny_and_is_answered_with_a_decision() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let pause = script.send(Payload::Approval { request_id: "approval-1".into(), state: ApprovalState::Requested, summary: "Run ls".into(), options: Vec::new() });
    let interrupt = &pause[0]["outcome"]["interrupts"][0];
    assert_eq!(interrupt["responseSchema"]["properties"]["optionId"]["enum"], json!(["accept", "decline"]));
    assert_eq!(answer_of(&script, "approval-1", ResumeStatus::Resolved, json!({ "optionId": "accept" })),
        Answer::Approval { request_id: "approval-1".into(), generation: 2, accept: true });
}

/// Starts carry what a row is drawn from; a call is its own message; the chat's ids are kept.
#[test]
fn every_start_carries_the_row_facts_and_a_call_is_its_own_message() {
    let mut script = Script::open();
    script.views.start_run(&RunAgentInput { thread_id: THREAD.into(), run_id: "run-client-1".into(), parent_run_id: None,
        resume: Vec::new(), message: None, message_id: Some("chat-message-1".into()) }).unwrap();
    script.send(turn(TurnState::Started));
    let facts = |kind: &str, sequence: i64| json!({ "itemType": kind, "turnId": "turn-1", "startedAtMs": sequence * 1000, "firstSequence": sequence });
    let asked = script.send(user("user-1", "Write hello"));
    assert_eq!(asked[0]["messageId"], "chat-message-1", "the chat already shows the message it sent under that id");
    let mut expected = facts("user-message", 2);
    expected["attachmentIds"] = json!(["shot-1"]);
    assert_eq!((&asked[0]["metadata"], &asked[2]["metadata"]), (&expected, &json!({ "completed": true })));
    let mut expected = facts("assistant-message", 3);
    expected["completed"] = json!(false);
    assert_eq!(script.send(delta("msg-1", "On it"))[0]["metadata"], expected);
    let call = script.send(tool("tool-1", ToolState::Started, Some("Write notes/hello.txt"), None));
    assert_eq!(call[0]["parentMessageId"], "tool-1", "a call is not folded into the reply before it");
    assert_eq!(call[0]["metadata"], facts("mcp-tool", 4));
    let done = script.send(tool("tool-1", ToolState::Completed, Some("Write notes/hello.txt"), Some("notes/hello.txt")));
    let native: Value = serde_json::from_str(done.last().unwrap()["content"].as_str().unwrap()).unwrap();
    assert_eq!(native, json!({ "kind": "tool", "itemId": "tool-1", "name": "Write", "state": "completed",
        "summary": "Write notes/hello.txt", "path": "notes/hello.txt", "hasDetail": true }));
    let blocks = serde_json::from_value(json!([{ "kind": "code", "language": "sh", "value": "ls", "complete": true }])).unwrap();
    let ended = script.send(Payload::AssistantMessage { item_id: "msg-1".into(), text: "On it".into(), completed: true, blocks: Some(blocks) });
    assert_eq!((&ended[0]["metadata"]["completed"], &ended[0]["metadata"]["blocks"][0]["kind"]), (&json!(true), &json!("code")));
    script.send(turn(TurnState::Completed));
    assert_eq!(script.send(user("user-2", "From the phone"))[1]["messageId"], "user-2");
}

/// Rows AG-UI has no event for, a compaction the used tokens give away among them,
/// are activity messages carrying the native event, and none opens a run.
#[test]
fn rows_without_an_agui_event_are_activity_messages_outside_runs() {
    let mut script = Script::open();
    let terminal: Payload = serde_json::from_value(json!({ "kind": "terminalProjection", "eventType": "item.completed",
        "providerInstanceId": "claude", "nativeSessionId": "native-1", "itemId": "item-7", "payload": {}, "providerMetadata": {},
        "rawFrameReference": { "id": "frame-1", "redacted": false } })).unwrap();
    let rows: Vec<Value> = [
        Payload::Plan { items: vec![PlanItem { text: "Write".into(), status: "pending".into() }] },
        Payload::ContextCompaction { trigger: Some("auto".into()), pre_tokens: Some(90_000), post_tokens: Some(20_000) },
        Payload::CheckoutChanged { from_cwd: "/a".into(), to_cwd: "/b".into() },
        Payload::Error { code: "provider_error".into(), message: "Overloaded".into(), recoverable: true },
        terminal,
    ].into_iter().flat_map(|payload| script.send(payload)).collect();
    assert_eq!(types(&rows), ["ACTIVITY_SNAPSHOT"; 5]);
    assert_eq!(rows.iter().map(|row| row["messageId"].as_str().unwrap()).collect::<Vec<_>>(), ["plan:1", "compaction:2", "checkout:3", "error:2:4", "item-7"]);
    assert_eq!((&rows[0]["activityType"], &rows[4]["activityType"]), (&json!("assembly.plan"), &json!("assembly.terminalProjection")));
    assert_eq!(rows[0]["content"]["event"]["payload"]["items"][0]["text"], "Write");
    assert_well_formed(&rows);
    let usage = |used| Payload::Usage { input_tokens: None, output_tokens: None, used_tokens: Some(used), context_window: None, total_tokens: None };
    assert_eq!(types(&script.send(usage(100_000))), ["CUSTOM"]);
    assert_eq!(types(&script.send(usage(90_000))), ["CUSTOM"], "a small drop is not one");
    let dropped = script.send(usage(20_000));
    assert_eq!((types(&dropped), &dropped[0]["messageId"]), (vec!["ACTIVITY_SNAPSHOT", "CUSTOM"], &json!("compaction:8")));
    assert_eq!(dropped[0]["content"]["event"]["payload"], json!({ "kind": "contextCompaction", "preTokens": 90_000, "postTokens": 20_000 }));
    assert_eq!(types(&script.send(usage(10_000))), ["CUSTOM"], "below the floor a drop is not one");
}

#[test]
fn replays_and_repeated_starts_are_dropped() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let first = script.event(delta("msg-1", "Hi"));
    assert_eq!(types(&script.views.project(&first)), ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT"]);
    assert!(script.views.project(&first).is_empty(), "the same event again is a replay");
    assert_eq!(types(&script.send(delta("msg-1", " there"))), ["TEXT_MESSAGE_CONTENT"]);
    assert_eq!(types(&script.send(reply("msg-1", "Hi there"))), ["TEXT_MESSAGE_END"]);
    assert!(script.send(reply("msg-1", "Hi there")).is_empty(), "a reply already ended is not shown again");
}

#[test]
fn only_a_failed_turn_is_a_run_error_and_stop_cancels_the_run() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(delta("msg-1", "Working"));
    let failed_error = script.send(Payload::Error { code: "provider_error".into(), message: "The model is overloaded".into(), recoverable: false });
    assert_eq!(types(&failed_error), ["ACTIVITY_SNAPSHOT"]);
    let failed = script.send(turn(TurnState::Failed));
    assert_eq!(types(&failed), ["TEXT_MESSAGE_END", "RUN_ERROR"], "the reply ends before its run");
    assert_eq!(failed[1]["message"], "The model is overloaded");
    script.send(turn(TurnState::Started));
    script.send(permission("perm-2", ApprovalState::Requested));
    script.send(permission("perm-2", ApprovalState::Expired));
    let stopped = script.send(turn(TurnState::Interrupted));
    assert_eq!(stopped.len(), 1);
    assert_eq!(stopped[0]["outcome"]["type"], "cancelled");
}

/// A failed prompt records its turn's end before its error.
#[test]
fn an_error_after_a_failed_turn_is_not_carried_into_the_next() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    assert_eq!(types(&script.send(turn(TurnState::Failed))), ["RUN_ERROR"]);
    script.send(Payload::Error { code: "provider_error".into(), message: "Overloaded".into(), recoverable: true });
    script.send(turn(TurnState::Started));
    assert_eq!(script.send(turn(TurnState::Failed))[0]["message"], "The turn failed");
}

/// A child agent's request outlives its parent's turn (`drain_root_permissions`).
#[test]
fn a_request_still_waiting_when_the_turn_ends_keeps_its_pause() {
    let mut script = Script::open();
    let mut chunks = script.send(turn(TurnState::Started));
    chunks.extend(script.send(permission("child-perm", ApprovalState::Requested)));
    assert!(script.send(turn(TurnState::Completed)).is_empty());
    script.run("run-2", Some(chunks[0]["runId"].as_str().unwrap()), vec![("child-perm", ResumeStatus::Resolved, json!({ "optionId": "allow-once" }))]).unwrap();
    let answered = script.send(permission("child-perm", ApprovalState::Accepted));
    assert_eq!((&answered[0]["runId"], answered[0].get("outcome")), (&chunks[0]["runId"], None));
    chunks.extend(answered);
    assert_well_formed(&chunks);
}

#[test]
fn a_turn_that_ends_mid_reply_ends_the_reply_before_the_run() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    script.send(delta("msg-1", "Working"));
    assert_eq!(types(&script.send(turn(TurnState::Completed))), ["TEXT_MESSAGE_END", "RUN_FINISHED"]);
}

#[test]
fn child_agents_start_and_finish_once() {
    let mut script = Script::open();
    script.send(turn(TurnState::Started));
    let child = |state: &str| Payload::ChildUpdate { child_id: "child-1".into(), parent_tool_call_id: "tool-9".into(), parent_id: None,
        transcript_id: None, label: Some("Explore".into()), state: state.into(), latest_activity: None };
    assert_eq!(types(&script.send(child("running"))), ["SUBAGENT_STARTED", "CUSTOM"]);
    assert_eq!(types(&script.send(child("finished"))), ["SUBAGENT_FINISHED", "CUSTOM"]);
    assert_eq!(types(&script.send(child("finished"))), ["CUSTOM"]);
}

#[test]
fn reopening_replays_the_snapshot_then_the_open_request_then_live_events() {
    let snapshot = [
        event(10, turn(TurnState::Started)),
        event(11, delta("msg-1", "Let me ")),
        event(12, delta("msg-1", "check.")),
        event(13, permission("stale-1", ApprovalState::Requested)),
        event(14, permission("perm-1", ApprovalState::Requested)),
    ];
    let mut views = AguiProjector::default();
    views.begin_open(THREAD);
    // A delta that lands while the snapshot is being read waits for it.
    assert!(views.project(&event(15, delta("msg-1", " Done")).clone()).is_empty());
    let pending = HashSet::from(["perm-1".to_string()]);
    let opened = views.finish_open(THREAD, Some((&snapshot, &pending, 14, true)));
    assert!(!types(&opened.events).iter().any(|kind| kind.starts_with("RUN_")), "history leaves runs out");
    assert_eq!(opened.events[1]["delta"], "Let me check.", "history joins a reply's pieces");
    let reopened = &opened.runs[0];
    let interrupts = reopened["outcome"]["interrupts"].as_array().unwrap();
    assert_eq!(interrupts.len(), 1, "a request the session no longer waits on is not shown again");
    assert_eq!(interrupts[0]["id"], "perm-1");
    assert_eq!(opened.live.len(), 1);
    assert_eq!(opened.live[0].1, 15);
    assert!(views.project(&event(12, delta("msg-1", "check."))).is_empty(), "covered by the snapshot");
    // The whole reply re-sent at the end adds nothing to what the view shows.
    let tail = views.project(&event(16, reply("msg-1", "Let me check. Done")));
    assert_eq!(types(&tail), ["TEXT_MESSAGE_END"]);
}

/// A reopened view goes on in the client's run, or ends it once the snapshot shows its turn over.
#[test]
fn a_reopen_keeps_the_clients_run_while_its_turn_goes_on_and_ends_it_after() {
    // The live view saw the first `seen` events; the snapshot holds them all.
    let reopen = |seen: usize, history: Vec<Payload>, pending: &[&str], running: bool| {
        let mut script = Script::open();
        script.run("run-client-1", None, Vec::new()).unwrap();
        history.iter().take(seen).for_each(|payload| { script.send(payload.clone()); });
        script.sequence = history.len() as i64;
        script.views.begin_open(THREAD);
        let events: Vec<_> = history.into_iter().enumerate().map(|(index, payload)| event(index as i64 + 1, payload)).collect();
        let pending = pending.iter().map(|id| id.to_string()).collect();
        let runs = script.views.finish_open(THREAD, Some((&events, &pending, events.len() as i64, running))).runs;
        (script, runs)
    };
    let (mut script, runs) = reopen(1, vec![turn(TurnState::Started)], &[], true);
    assert!(runs.is_empty());
    assert_eq!(types(&script.send(delta("msg-1", "Hi"))), ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT"], "the run goes on");
    assert_eq!(script.send(turn(TurnState::Completed)).last().unwrap()["runId"], "run-client-1");
    let (_, runs) = reopen(1, vec![turn(TurnState::Started), turn(TurnState::Completed)], &[], false);
    assert_eq!((types(&runs), &runs[0]["runId"], runs[0].get("outcome")), (vec!["RUN_FINISHED"], &json!("run-client-1"), None));
    let (_, runs) = reopen(2, vec![turn(TurnState::Started), permission("perm-1", ApprovalState::Requested)], &["perm-1"], true);
    assert_eq!((runs.len(), &runs[0]["runId"]), (1, &json!("run-client-1")), "the request is shown again on the client's run");
}

#[test]
fn a_session_without_an_open_view_projects_nothing() {
    let mut views = AguiProjector::default();
    assert!(views.project(&event(1, turn(TurnState::Started))).is_empty());
}

/// The scenarios the frontend's contract test plays are well formed; with
/// `AGUI_FIXTURE_OUT` naming a folder they are also written there for it.
/// `rust-chunks.jsonl` holds client runs only; `rust-chunks-extra.jsonl` holds
/// the answers from other surfaces, a Stop during a pause, and a reopened view.
#[test]
fn contract_scenarios_are_well_formed() {
    let line = |scenario: &str, chunk: &Value| json!({ "scenario": scenario, "ownedId": THREAD, "chunk": chunk }).to_string();
    let mut runs = Vec::new();
    for (scenario, choice) in [("permission-allow", "allow-once"), ("permission-reject", "reject")] {
        let chunks = permission_scenario(choice).1;
        assert_well_formed(&chunks);
        runs.extend(chunks.iter().map(|chunk| line(scenario, chunk)));
    }
    let mut script = Script::open();
    script.run("run-client-3", None, Vec::new()).unwrap();
    let mut question_run = script.send(turn(TurnState::Started));
    question_run.extend(script.send(question("ask-1")));
    script.run("run-client-4", Some("run-client-3"), vec![("ask-1", ResumeStatus::Resolved, json!({ "budget": 0.25 }))]).unwrap();
    question_run.extend(script.send(Payload::UserInputResolved { request_id: "ask-1".into(), cancelled: false }));
    question_run.extend(script.send(delta("msg-2", "A quarter it is.")));
    question_run.extend(script.send(Payload::ChildUpdate { child_id: "child-1".into(), parent_tool_call_id: String::new(), parent_id: None,
        transcript_id: None, label: Some("Explore".into()), state: "running".into(), latest_activity: Some("Reading".into()) }));
    question_run.extend(script.send(Payload::Plan { items: vec![PlanItem { text: "Spend".into(), status: "completed".into() }] }));
    question_run.extend(script.send(turn(TurnState::Completed)));
    question_run.extend(script.send(turn(TurnState::Started)));
    question_run.extend(script.send(Payload::Error { code: "provider_error".into(), message: "Overloaded".into(), recoverable: false }));
    question_run.extend(script.send(turn(TurnState::Failed)));
    assert_well_formed(&question_run);
    runs.extend(question_run.iter().map(|chunk| line("question-then-failed-turn", chunk)));

    let mut other = Script::open();
    let other_run: Vec<Value> = [turn(TurnState::Started), permission("perm-1", ApprovalState::Requested), permission("perm-1", ApprovalState::Accepted),
        delta("msg-3", "Done."), reply("msg-3", "Done."), turn(TurnState::Completed), turn(TurnState::Started),
        permission("perm-2", ApprovalState::Requested), permission("perm-2", ApprovalState::Expired), turn(TurnState::Interrupted)].into_iter().flat_map(|payload| other.send(payload)).collect();
    assert_well_formed(&other_run);
    let mut extra: Vec<String> = other_run.iter().map(|chunk| line("other-surface-and-stop", chunk)).collect();
    let snapshot = [event(10, turn(TurnState::Started)), event(11, delta("msg-4", "Let me check.")), event(12, permission("perm-3", ApprovalState::Requested))];
    let mut views = AguiProjector::default();
    views.begin_open(THREAD);
    let opened = views.finish_open(THREAD, Some((&snapshot, &HashSet::from(["perm-3".to_string()]), 12, true)));
    extra.extend(opened.events.iter().chain(&opened.runs).map(|chunk| line("reopened-view", chunk)));
    let Some(folder) = std::env::var_os("AGUI_FIXTURE_OUT") else { return };
    let folder = std::path::PathBuf::from(folder);
    std::fs::write(folder.join("rust-chunks.jsonl"), runs.join("\n") + "\n").unwrap();
    std::fs::write(folder.join("rust-chunks-extra.jsonl"), extra.join("\n") + "\n").unwrap();
}
