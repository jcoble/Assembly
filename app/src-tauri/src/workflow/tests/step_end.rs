//! A step's session stops, and its token is refused, when the step ends; only the build step changes code (TSK-1601).
use super::*;

/// An engine whose session ids no other test uses, since tokens live on the one shared endpoint.
fn engine(runtime: Arc<FakeRuntime>) -> WorkflowEngine {
    let first = (uuid::Uuid::new_v4().as_u128() as u64) >> 16;
    WorkflowEngine::new(runtime, Arc::new(InMemoryWorktreeLeasePort::default()),
        Arc::new(FakeClock(AtomicU64::new(1))), Arc::new(FakeIds(AtomicU64::new(first))),
        Arc::new(SessionStore::open_in_memory().unwrap()))
}
fn shared_endpoint() -> &'static MessagingEndpoint {
    crate::agent_conversation::messaging::endpoint().unwrap()
}
fn calls_tools(token: &str) -> Result<Value, String> {
    tool(shared_endpoint(), token, "list_sessions", json!({}))
}
fn cancelled(runtime: &FakeRuntime, owned_id: &str) -> usize {
    runtime.cancelled_ids.lock().unwrap().iter().filter(|id| *id == owned_id).count()
}
fn failure_code(run: &WorkflowRunRecord, id: &str) -> Option<String> {
    step_of(run, id).failure.as_ref().map(|failure| failure.code.clone())
}
fn pass() -> Value { json!({"verdict":"pass","findings":[]}) }

#[test]
fn a_session_still_calling_tools_after_finish_is_cancelled() {
    let repo = RunRepo::new();
    let runtime = Arc::new(FakeRuntime::default());
    let engine = engine(runtime.clone());
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    let build = step_of(&run, "build").owned_id.clone();
    let token = agent(&engine, shared_endpoint(), &build, &repo.0);
    assert!(calls_tools(&token).is_ok());
    let reviewing = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(step_of(&reviewing, "build").state, WorkflowNodeState::Completed);
    assert_eq!(cancelled(&runtime, &build), 1);
    assert_eq!(calls_tools(&token).unwrap_err(), "Unauthorized");
}

#[test]
fn waiting_failing_and_accepted_steps_cancel_their_session() {
    let repo = RunRepo::new();
    let runtime = Arc::new(FakeRuntime::default());
    let engine = engine(runtime.clone());
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    end_turn(&engine, &run.id, "build", &[]);
    // A reviewer that never reports waits for the owner.
    let review = step_of(&engine.require_run(&run.id).unwrap(), "review").owned_id.clone();
    let token = agent(&engine, shared_endpoint(), &review, &repo.0);
    let waiting = end_turn(&engine, &run.id, "review", &[]);
    assert_eq!(step_of(&waiting, "review").state, WorkflowNodeState::WaitingInput);
    assert_eq!(cancelled(&runtime, &review), 1);
    assert_eq!(calls_tools(&token).unwrap_err(), "Unauthorized");
    let accepted = tauri::async_runtime::block_on(engine.answer_pause(&run.id, "review", "accept", None, "owner", None, "accept", None)).unwrap();
    assert_eq!(step_of(&accepted, "review").state, WorkflowNodeState::Completed);
    assert_eq!(cancelled(&runtime, &review), 2);

    // A step that fails its expected files fails and stops.
    let repo = RunRepo::new();
    let run = loop_run(&engine, &repo.0, "  build: { agent: { provider: codex }, prompt: prompts/p.md, expect_files: [missing.md] }\n");
    let build = step_of(&run, "build").owned_id.clone();
    let failed = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(failure_code(&failed, "build").as_deref(), Some("expect-files"));
    assert_eq!(cancelled(&runtime, &build), 1);
}

#[test]
fn a_redone_build_gets_a_new_token_and_the_old_one_is_refused() {
    let repo = RunRepo::new();
    let runtime = Arc::new(FakeRuntime::default());
    let engine = engine(runtime.clone());
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    let build = step_of(&run, "build").owned_id.clone();
    let old = agent(&engine, shared_endpoint(), &build, &repo.0);
    end_turn(&engine, &run.id, "build", &[]);
    let redone = end_turn(&engine, &run.id, "review", &[findings(1)]);
    // The build keeps its session; stopping an idle session suspends it, and resuming it issues a token again.
    assert_eq!((step_of(&redone, "build").attempt, step_of(&redone, "build").owned_id.as_str()), (2, build.as_str()));
    let new = agent(&engine, shared_endpoint(), &build, &repo.0);
    assert_ne!(new, old);
    assert_eq!(calls_tools(&old).unwrap_err(), "Unauthorized");
    assert!(calls_tools(&new).is_ok());
}

#[test]
fn a_review_step_that_commits_fails_changed_code() {
    let repo = RunRepo::new();
    let engine = engine(Arc::new(FakeRuntime::default()));
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    end_turn(&engine, &run.id, "build", &[]);
    repo.git(&["-c", "user.name=Test", "-c", "user.email=test@test", "commit", "--allow-empty", "-m", "review fix"]);
    let ended = end_turn(&engine, &run.id, "review", &[pass()]);
    assert_eq!(step_of(&ended, "review").state, WorkflowNodeState::Failed);
    assert_eq!(failure_code(&ended, "review").as_deref(), Some("changed-code"));
}

#[test]
fn a_review_step_leaving_an_untracked_file_fails_changed_code() {
    let repo = RunRepo::new();
    let engine = engine(Arc::new(FakeRuntime::default()));
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    end_turn(&engine, &run.id, "build", &[]);
    std::fs::write(repo.0.join("stray.txt"), "left behind").unwrap();
    let ended = end_turn(&engine, &run.id, "review", &[pass()]);
    assert_eq!(failure_code(&ended, "review").as_deref(), Some("changed-code"));
}

#[test]
fn a_review_step_rewriting_a_file_the_build_left_fails_changed_code() {
    let repo = RunRepo::new();
    let engine = engine(Arc::new(FakeRuntime::default()));
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    std::fs::write(repo.0.join("draft.txt"), "the build's version").unwrap();
    end_turn(&engine, &run.id, "build", &[]);
    std::fs::write(repo.0.join("draft.txt"), "the reviewer's version").unwrap();
    let ended = end_turn(&engine, &run.id, "review", &[pass()]);
    assert_eq!(failure_code(&ended, "review").as_deref(), Some("changed-code"));
}

#[test]
fn a_build_step_may_commit() {
    let repo = RunRepo::new();
    let engine = engine(Arc::new(FakeRuntime::default()));
    let run = loop_run(&engine, &repo.0, LOOP_REVIEW);
    repo.git(&["-c", "user.name=Test", "-c", "user.email=test@test", "commit", "--allow-empty", "-m", "the work"]);
    let reviewing = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(step_of(&reviewing, "build").state, WorkflowNodeState::Completed);
    let ended = end_turn(&engine, &run.id, "review", &[pass()]);
    assert_eq!(step_of(&ended, "review").state, WorkflowNodeState::Completed);
}

#[test]
fn changed_code_is_permanent_and_not_retried_by_redo() {
    let repo = RunRepo::new();
    let engine = engine(Arc::new(FakeRuntime::default()));
    let run = loop_run(&engine, &repo.0, "  build: { agent: { provider: codex }, prompt: prompts/p.md }\n  checks: { needs: [build], run: [\"touch stray.txt && exit 1\"], on_fail: build }\n");
    end_turn(&engine, &run.id, "build", &[]);
    let run = settle(&engine, &run.id);
    assert_eq!(failure_code(&run, "checks").as_deref(), Some("changed-code"));
    assert_eq!(step_of(&run, "checks").state, WorkflowNodeState::Failed);
    assert_eq!(step_of(&run, "build").attempt, 1);
}

fn load(steps: &str) -> Result<WorkflowDefinitionV1, WorkflowError> {
    let repo = RunRepo::new();
    std::fs::create_dir_all(repo.0.join(".assembly/workflows/prompts")).unwrap();
    std::fs::write(repo.0.join(".assembly/workflows/prompts/p.md"), "Do the work").unwrap();
    std::fs::write(repo.0.join(AGENT_FLOW), format!("name: Loop\nsteps:\n{steps}")).unwrap();
    WorkflowEngine::validate_file(&repo.0, Path::new(AGENT_FLOW))
}

#[test]
fn two_unordered_agent_steps_are_refused_at_load() {
    let error = load("  build: { agent: { provider: codex }, prompt: prompts/p.md }\n  docs: { agent: { provider: claude }, prompt: prompts/p.md }\n").unwrap_err();
    assert!(error.to_string().contains("steps build and docs can run at the same time; agent steps must be ordered"), "{error}");
}

#[test]
fn parallel_reviews_of_one_step_still_load() {
    let second = "  second:\n    needs: [build]\n    review: { of: build, agent: { provider: codex }, prompt: prompts/p.md }\n";
    assert!(load(&format!("{LOOP_REVIEW}{second}")).is_ok());
}

#[test]
fn redo_stops_a_parallel_reviewer_and_its_timer() {
    let repo = RunRepo::new();
    let runtime = Arc::new(FakeRuntime::default());
    let engine = run_engine(runtime.clone());
    let steps = format!("{LOOP_REVIEW}  second:\n    needs: [build]\n    review: {{ of: build, agent: {{ provider: codex }}, prompt: prompts/p.md }}\n");
    let run = loop_run(&engine, &repo.0, &steps);
    let both = end_turn(&engine, &run.id, "build", &[]);
    let old_second = step_of(&both, "second").clone();
    assert_eq!(old_second.state, WorkflowNodeState::Running);
    assert!(engine.deadlines.lock().unwrap().contains_key(&old_second.id));
    let run = end_turn(&engine, &run.id, "review", &[findings(1)]);
    assert!(runtime.cancelled_ids.lock().unwrap().contains(&old_second.owned_id));
    assert!(!engine.deadlines.lock().unwrap().contains_key(&old_second.id));
    assert_eq!((step_of(&run, "second").attempt, step_of(&run, "second").state), (2, WorkflowNodeState::Blocked));
    assert_eq!(step_of(&run, "build").state, WorkflowNodeState::Running);
    assert_eq!(run.state, WorkflowRunState::Running);
}

#[test]
fn run_step_commands_do_not_hold_up_the_dispatch_call() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    let run = step_run(&engine, &repo.0, json!({"run":["sleep 1; touch ../done.txt"]}));
    let started = std::time::Instant::now();
    let running = tauri::async_runtime::block_on(engine.start(&run.id, "start")).unwrap();
    assert!(started.elapsed() < std::time::Duration::from_millis(800), "start waited for the command");
    assert_eq!(running.nodes[0].state, WorkflowNodeState::Running);
    let finished = settle(&engine, &run.id);
    assert!(repo.0.join("../done.txt").exists());
    assert_eq!(finished.nodes[0].state, WorkflowNodeState::Completed);
}
