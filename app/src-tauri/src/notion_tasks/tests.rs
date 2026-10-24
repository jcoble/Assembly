// Regression pins for the Tasks list and task viewer (TSK-1167).

fn listed(page: Value) -> NotionTaskProjection {
    finish_task_projection(
        pending_task_projection(&page, 7).expect("a listed task"),
        &HashMap::from([
            ("project-a".to_string(), "Assembly".to_string()),
            ("project-b".to_string(), "Rental Command".to_string()),
        ]),
    )
}

/// Pins cb1fe40ce: the list reads Status whether Notion stores it as a
/// select field (the Command Center) or as a status field.
#[test]
fn list_reads_status_from_a_select_or_a_status_field() {
    for status in [
        json!({ "select": { "name": "Doing" } }),
        json!({ "status": { "name": "Doing" } }),
    ] {
        let task = listed(json!({ "id": "task-1", "properties": { "Status": status } }));
        assert_eq!(task.status, "Doing");
    }
}

/// Pins cb1fe40ce and 815328b7f: a task with no status is listed as
/// Unspecified (the pill the store filters on), and a page whose title
/// property is not called Name still shows its title.
#[test]
fn list_names_missing_status_and_reads_any_title_property() {
    let task = listed(json!({
        "id": "task-1",
        "properties": { "Task name": { "title": [{ "plain_text": "Fix login" }] } }
    }));
    assert_eq!(task.status, "Unspecified");
    assert_eq!(task.title, "Fix login");

    let untitled = listed(json!({ "id": "task-2", "properties": {} }));
    assert_eq!(untitled.title, "Untitled task");
}

/// Pins cb1fe40ce: a task related to several projects lists every project
/// whose name was loaded, and skips relations that are not loaded.
#[test]
fn list_joins_every_known_project_and_skips_unknown_ones() {
    let task = listed(json!({
        "id": "task-1",
        "properties": { "Project": { "relation": [
            { "id": "project-a" }, { "id": "project-missing" }, { "id": "project-b" }
        ] } }
    }));
    assert_eq!(task.project, "Assembly, Rental Command");
}

/// Pins cb1fe40ce: several assignees are listed together, and nobody
/// assigned means no assignee rather than an empty name.
#[test]
fn list_joins_assignees_and_leaves_none_empty() {
    let task = listed(json!({
        "id": "task-1",
        "properties": { "Assignee": { "people": [{ "name": "Sam" }, { "id": "no-name" }, { "name": "Ana" }] } }
    }));
    assert_eq!(task.assignee.as_deref(), Some("Sam, Ana"));

    let task = listed(json!({ "id": "task-2", "properties": { "Assignee": { "people": [] } } }));
    assert_eq!(task.assignee, None);
}

/// Pins cb1fe40ce: a result without an id or properties is not a task, so
/// the refresh drops it instead of caching a blank row.
#[test]
fn list_drops_results_without_an_id_or_properties() {
    assert!(pending_task_projection(&json!({ "properties": {} }), 1).is_none());
    assert!(pending_task_projection(&json!({ "id": "task-1" }), 1).is_none());
}

/// Pins e00bfbb52: the task id goes straight into the Notion API path, so
/// only a Notion page id (hex digits and dashes) is accepted.
#[test]
fn task_id_check_accepts_only_notion_page_ids() {
    assert!(validate_task_id("3f4394b0689d81b19819c696dad2fd48").is_ok());
    assert!(validate_task_id("3f4394b0-689d-81b1-9819-c696dad2fd48").is_ok());
    for id in ["../users/me", "3f43/children", "3f43?filter=x", "task-1", &"a".repeat(65)] {
        assert_eq!(validate_task_id(id).unwrap_err(), "The Notion task ID is invalid", "{id}");
    }
}

/// Pins 73cc44ef1: reading a task page asks for the next page of blocks or
/// comments only while Notion says there is more, so it cannot loop forever.
#[test]
fn detail_paging_continues_only_while_notion_has_more() {
    assert_eq!(next_cursor(&json!({ "has_more": true, "next_cursor": "c2" })), Some("c2"));
    assert_eq!(next_cursor(&json!({ "has_more": false, "next_cursor": "c2" })), None);
    assert_eq!(next_cursor(&json!({ "has_more": true, "next_cursor": null })), None);
    assert_eq!(next_cursor(&json!({})), None);
    assert_eq!(paged_path("blocks/b/children?page_size=100", None), "blocks/b/children?page_size=100");
    assert_eq!(
        paged_path("blocks/b/children?page_size=100", Some("c2")),
        "blocks/b/children?page_size=100&start_cursor=c2"
    );
}

/// Pins a9db2db83: the viewer shows the status drop-down for a select-typed
/// Status, so the select field must reach it by name with its value.
#[test]
fn viewer_receives_a_select_status_with_its_kind_and_value() {
    let rows = task_properties(
        &json!({ "properties": {
            "Status": { "type": "select", "select": { "name": "Blocked" } },
            "Priority": { "type": "select", "select": null }
        } }),
        &HashMap::new(),
    );
    let status = rows.iter().find(|row| row.name == "Status").expect("Status row");
    assert_eq!(status.kind, "select");
    assert_eq!(status.values, ["Blocked"]);
    let priority = rows.iter().find(|row| row.name == "Priority").expect("Priority row");
    assert!(priority.values.is_empty());
}

/// Pins 73cc44ef1: a related page whose title was not loaded still shows as
/// a link to that page instead of disappearing.
#[test]
fn viewer_relation_without_a_loaded_title_still_links_to_the_page() {
    let rows = task_properties(
        &json!({ "properties": { "Blocked by": { "type": "relation", "relation": [
            { "id": "3f4394b0-689d-81b1-9819-c696dad2fd48" }
        ] } } }),
        &HashMap::new(),
    );
    assert_eq!(rows[0].values, ["Notion page"]);
    assert_eq!(
        rows[0].links,
        [Some("https://www.notion.so/3f4394b0689d81b19819c696dad2fd48".to_string())]
    );
}

/// Pins cfd5c3a65 (simpler Notion setup): a lone database with a title and
/// a Status field is chosen even when it is not named Tasks; databases
/// without both are never offered.
#[test]
fn setup_picks_the_only_database_with_a_title_and_status() {
    let source = |id: &str, properties: Value| {
        json!({ "object": "data_source", "id": id, "title": [{ "plain_text": "Board" }], "properties": properties })
    };
    let payload = json!({ "results": [
        source("no-status", json!({ "Name": { "type": "title" } })),
        source("no-title", json!({ "Status": { "type": "select" } })),
        source("board", json!({ "Name": { "type": "title" }, "Status": { "type": "select" } })),
        { "object": "page", "id": "a-page", "properties": { "Name": { "type": "title" }, "Status": {} } }
    ] });
    assert_eq!(task_data_source_id(&payload).unwrap(), "board");

    let none = json!({ "results": [source("no-status", json!({ "Name": { "type": "title" } }))] });
    assert!(task_data_source_id(&none).unwrap_err().starts_with("No Notion task database was found"));
}
