//! A file opened from a viewed project is that project's, so it can't block saving the session's workspace.
use super::*;

#[gpui::test]
async fn a_file_from_a_viewed_project_does_not_stop_the_session_saving(cx: &mut TestAppContext) {
    let (_backend, _heard, coordinator) = start_answering(Value::Null, Some("/checkout"), cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let _dock = cx.update(add_dock);
    let _refused = cx.update(|_, cx| fail_on_refusal(&coordinator, cx));
    let (sent, mut activated) = unbounded();
    let _heard = cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    open_workspace(owner(), &coordinator, cx);
    activated.recv().await.unwrap();
    cx.update(|window, cx| {
        FileView::open_at(
            "view:70:2f6f74686572".into(),
            "a.txt".into(),
            (None, None),
            true,
            None,
            window,
            cx,
        );
    });
    assert_eq!(cx.update(|_, cx| coordinator.read(cx).status.clone()), None);
}
