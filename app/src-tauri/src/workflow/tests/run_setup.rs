//! A run whose worktree cannot be set up ends failed with the error, and Retry sets it up again (TSK-1618).
use super::*;

#[test]
fn a_run_whose_worktree_setup_fails_ends_failed_with_the_error() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    repo.git(&["remote", "set-url", "origin", "/var/tmp/no-such-origin.git"]);
    std::fs::create_dir_all(repo.0.join(".assembly/workflows/prompts")).unwrap();
    std::fs::write(repo.0.join(".assembly/workflows/prompts/p.md"), "Do the work").unwrap();
    std::fs::write(repo.0.join(".assembly/workflows/loop.yml"), format!("name: Loop\nsteps:\n{LOOP_REVIEW}")).unwrap();
    let task = TaskSnapshot { key: "TSK-1416".into(), title: "Task title".into(), body: "Task body".into() };
    let run = engine.create_run_from_file(&repo.0, Path::new(".assembly/workflows/loop.yml"), task, json!({}), "create".into()).unwrap();
    let error = tauri::async_runtime::block_on(engine.start(&run.id, "start")).unwrap_err().to_string();
    assert!(error.contains("no-such-origin"), "{error}");

    let run = engine.get_run(&run.id).unwrap();
    assert_eq!(run.state, WorkflowRunState::Failed);
    let failure = step_of(&run, "build").failure.clone().unwrap();
    assert_eq!((step_of(&run, "build").state, failure.code.as_str()), (WorkflowNodeState::Failed, "setup"));
    assert!(failure.message.contains("no-such-origin"), "{}", failure.message);
    assert!(run.nodes.iter().all(|node| !matches!(node.state,
        WorkflowNodeState::Ready | WorkflowNodeState::Starting | WorkflowNodeState::Running)));
    assert!(engine.recover_after_restart().unwrap().is_empty());

    // Once the repository is fixed, Retry on the failed step sets the worktree up and starts the step.
    repo.git(&["remote", "set-url", "origin", repo.0.parent().unwrap().join("origin.git").to_str().unwrap()]);
    let retried = tauri::async_runtime::block_on(engine.retry_node(&run.id, "build", "retry")).unwrap();
    assert_eq!(retried.state, WorkflowRunState::Running);
    assert_eq!(step_of(&retried, "build").state, WorkflowNodeState::Running);
    assert!(retried.input_snapshot["worktree"]["path"].is_string());
}

#[test]
fn start_workflow_after_a_setup_failure_starts_a_new_run() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    let endpoint = agent_endpoint(&engine, &repo.0);
    let token = agent(&engine, &endpoint, "setup-starter", &repo.0);
    repo.git(&["remote", "set-url", "origin", "/var/tmp/no-such-origin.git"]);
    let error = tool(&endpoint, &token, "start_workflow", start_args("TSK-1")).unwrap_err();
    assert!(error.contains("no-such-origin"), "{error}");
    let failed = engine.list_runs().unwrap();
    assert_eq!(failed.iter().map(|run| run.state).collect::<Vec<_>>(), vec![WorkflowRunState::Failed]);

    // Calling again once the repository is fixed starts a new run instead of returning the stuck one.
    repo.git(&["remote", "set-url", "origin", repo.0.parent().unwrap().join("origin.git").to_str().unwrap()]);
    let started = tool(&endpoint, &token, "start_workflow", start_args("TSK-1")).unwrap();
    assert_eq!(started["state"], "running");
    assert_ne!(started["run_id"], json!(failed[0].id));
}
