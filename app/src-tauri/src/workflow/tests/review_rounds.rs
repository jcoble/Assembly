//! Redo rounds with several reviewers of one step (TSK-1580): a reviewer still waiting for an agent
//! slot gets the new version with the others and never starts before the step it reviews has a result.
use super::*;

#[test]
fn redo_sends_the_new_version_to_a_reviewer_still_waiting_for_a_slot() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    let steps = format!("{LOOP_REVIEW}  second:\n    needs: [build]\n    review: {{ of: build, agent: {{ provider: codex }}, prompt: prompts/p.md }}\nlimits: {{ max_sessions: 1 }}\n");
    let run = loop_run(&engine, &repo.0, &steps);
    let reviewing = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(step_of(&reviewing, "review").state, WorkflowNodeState::Running);
    assert_eq!(step_of(&reviewing, "second").state, WorkflowNodeState::Ready);
    let redone = end_turn(&engine, &run.id, "review", &[findings(1)]);
    assert_eq!((step_of(&redone, "build").attempt, step_of(&redone, "build").state), (2, WorkflowNodeState::Running));
    assert_eq!((step_of(&redone, "second").attempt, step_of(&redone, "second").state), (2, WorkflowNodeState::Blocked));
    let reviewing = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(step_of(&reviewing, "review").attempt, step_of(&reviewing, "second").attempt);
}

#[test]
fn another_round_does_not_start_a_reviewer_before_the_step_it_reviews_has_a_result() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    std::fs::create_dir_all(repo.0.join(".assembly/workflows/prompts")).unwrap();
    std::fs::write(repo.0.join(".assembly/workflows/prompts/r.md"), "Review ${{ steps.build.summary }}").unwrap();
    let reviewer = |id: &str| format!("  {id}:\n    needs: [build]\n    review: {{ of: build, agent: {{ provider: codex }}, prompt: prompts/r.md }}\n");
    let steps = format!("  build: {{ agent: {{ provider: codex }}, prompt: prompts/p.md }}\n{}{}{}limits: {{ max_sessions: 2 }}\n",
        reviewer("review"), reviewer("second"), reviewer("third"));
    let run = loop_run(&engine, &repo.0, &steps);
    let reviewing = end_turn(&engine, &run.id, "build", &[]);
    assert_eq!(step_of(&reviewing, "review").state, WorkflowNodeState::Running);
    assert_eq!(step_of(&reviewing, "second").state, WorkflowNodeState::Running);
    assert_eq!(step_of(&reviewing, "third").state, WorkflowNodeState::Ready);
    let stopped = end_turn(&engine, &run.id, "review", &[json!({"verdict":"fail","findings":["REPEAT: same problem"]})]);
    assert_eq!(stopped.state, WorkflowRunState::WaitingInput);
    let waiting = end_turn(&engine, &run.id, "second", &[json!({"verdict":"pass","findings":[]})]);
    assert_eq!(step_of(&waiting, "third").state, WorkflowNodeState::Ready);
    let answered = tauri::async_runtime::block_on(engine.answer_pause(&run.id, "review", "another-round", None, "owner", None, "answer", None)).unwrap();
    assert_eq!(step_of(&answered, "build").state, WorkflowNodeState::Running);
    let third = step_of(&answered, "third");
    assert!(third.failure.is_none(), "{:?}", third.failure.as_ref().map(|failure| &failure.message));
    assert_eq!(third.state, WorkflowNodeState::Blocked);
    assert_eq!(third.attempt, step_of(&answered, "review").attempt);
}
