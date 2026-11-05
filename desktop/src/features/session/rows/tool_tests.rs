use super::{ToolPayload, ToolState};

fn tool(state: ToolState, kind: Option<&str>) -> ToolPayload {
    ToolPayload {
        name: "Bash".into(),
        state,
        summary: None,
        path: None,
        added: None,
        removed: None,
        has_detail: false,
        kind: kind.map(Into::into),
    }
}

#[test]
fn a_writing_tool_touches_the_folder_once_when_it_finishes() {
    let done = tool(ToolState::Completed, Some("execute"));
    assert!(done.just_touched_folder(Some(ToolState::Started)));
    assert!(done.just_touched_folder(None));
    assert!(!done.just_touched_folder(Some(ToolState::Completed)));
    assert!(!tool(ToolState::Started, Some("execute")).just_touched_folder(None));
    assert!(tool(ToolState::Failed, Some("edit")).just_touched_folder(Some(ToolState::Started)));
    assert!(
        !tool(ToolState::Completed, Some("read")).just_touched_folder(Some(ToolState::Started))
    );
}
