use super::*;

#[test]
fn agent_start_workflow_passes_inputs_and_keeps_defaults() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    let endpoint = agent_endpoint(&engine, &repo.0);
    std::fs::write(repo.0.join(AGENT_FLOW), format!("name: Loop\ninputs: {{ i_merge: {{ default: false }} }}\nsteps:\n{LOOP_REVIEW}")).unwrap();
    let token = agent(&engine, &endpoint, "starter", &repo.0);
    // Inputs the agent names override the workflow's defaults.
    let mut args = start_args("TSK-1");
    args["inputs"] = json!({"i_merge": true});
    let started = tool(&endpoint, &token, "start_workflow", args).unwrap();
    let run = engine.get_run(started["run_id"].as_str().unwrap()).unwrap();
    assert_eq!(run.input_snapshot["inputs"]["i_merge"], true);
    // Without inputs the defaults apply.
    let started = tool(&endpoint, &token, "start_workflow", start_args("TSK-2")).unwrap();
    let run = engine.get_run(started["run_id"].as_str().unwrap()).unwrap();
    assert_eq!(run.input_snapshot["inputs"]["i_merge"], false);
}

#[test]
fn a_slice_run_records_its_parent_task_and_returns_it_in_summaries() {
    let repo = RunRepo::new();
    let engine = run_engine(Arc::new(FakeRuntime::default()));
    let endpoint = agent_endpoint(&engine, &repo.0);
    let token = agent(&engine, &endpoint, "starter", &repo.0);
    // An agent starts a slice through start_workflow's inputs.
    let mut args = start_args("TSK-1603");
    args["inputs"] = json!({"parent_task": "TSK-1582"});
    let slice = tool(&endpoint, &token, "start_workflow", args).unwrap()["run_id"].as_str().unwrap().to_string();
    assert_eq!(engine.get_run(&slice).unwrap().input_snapshot["inputs"]["parent_task"], "TSK-1582");
    assert_eq!(tool(&endpoint, &token, "get_workflow_run", json!({"run_id": slice})).unwrap()["parent_task"], "TSK-1582");
    // The desktop start path takes the same inputs.
    let desktop = engine.create_run_from_file(&repo.0, Path::new(AGENT_FLOW), TaskSnapshot { key: "TSK-1604".into(),
        title: "Slice".into(), body: "Body".into() }, json!({"inputs": {"parent_task": "TSK-1582"}}), "desktop".into()).unwrap();
    assert_eq!(engine.get_run(&desktop.id).unwrap().input_snapshot["inputs"]["parent_task"], "TSK-1582");
    // A run started without it has no parent task, and its snapshot is as before.
    let plain = tool(&endpoint, &token, "start_workflow", start_args("TSK-1")).unwrap()["run_id"].as_str().unwrap().to_string();
    let snapshot = engine.get_run(&plain).unwrap().input_snapshot;
    assert!(snapshot["inputs"].get("parent_task").is_none() && snapshot.get("parent_task").is_none());
    assert!(tool(&endpoint, &token, "get_workflow_run", json!({"run_id": plain})).unwrap()["parent_task"].is_null());
    let listed = tool(&endpoint, &token, "list_workflow_runs", json!({})).unwrap();
    let parent = |id: &str| listed.as_array().unwrap().iter().find(|run| run["run_id"] == id).unwrap()["parent_task"].clone();
    assert_eq!((parent(&slice), parent(&plain)), (json!("TSK-1582"), Value::Null));
}
