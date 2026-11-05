//! Feature 28, task 7: an outside file opens read-only with a marker until Edit anyway, saving does nothing while it's
//! locked, and its tab has no History. A session file is never locked.
use super::super::outside::outside_key;
use super::super::outside::tests::{Stage, answers, hosts, hosts_document, owner, stage, target};
use super::super::{DocumentStore, FileView, SaveDocument};
use gpui::TestAppContext;
use gpui_kit::{Entity, Modifiers};
use serde_json::json;

const BANNER: &str = "Outside this session's project, so it opened read-only.";

impl Stage<'_> {
    async fn outside_tab(&mut self) -> Entity<FileView> {
        self.open("", "/etc/hosts", Some(hosts()));
        let view = self.tab(&outside_key(None, "/etc/hosts"));
        self.loaded(&view).await;
        self.show(&view);
        view
    }

    /// Types `text` into the file's editor as a person would: focus, then keys.
    fn type_in(&mut self, view: &Entity<FileView>, text: &str) {
        self.cx.update(|window, cx| {
            let state = view
                .read(cx)
                .editor
                .as_ref()
                .expect("the file's editor")
                .state
                .clone();
            state.update(cx, |state, cx| state.focus(window, cx));
        });
        self.draw();
        self.cx.simulate_input(text);
        self.draw();
    }

    fn click(&mut self, selector: &'static str) {
        self.draw();
        let bounds = self
            .cx
            .debug_bounds(selector)
            .unwrap_or_else(|| panic!("{selector} is drawn"));
        self.cx.simulate_click(bounds.center(), Modifiers::none());
        self.cx.run_until_parked();
        self.draw();
    }

    fn draft(&mut self) -> (String, bool) {
        self.cx.update(|_, cx| {
            let docs = cx.global::<DocumentStore>();
            let text = docs.snapshot(&hosts_document()).expect("the draft").text;
            (text, docs.is_dirty(&hosts_document()))
        })
    }
}

#[gpui::test]
async fn an_outside_file_is_read_only_from_its_first_frame(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    stage.type_in(&view, "typed");

    assert_eq!(
        stage.draft(),
        ("disk\n".into(), false),
        "typing changed nothing"
    );
}

#[gpui::test]
async fn save_does_nothing_while_locked(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    // A restored outside draft comes back dirty and locked.
    stage.cx.update(|_, cx| {
        let docs = cx.global_mut::<DocumentStore>();
        let version = docs.snapshot(&hosts_document()).unwrap().version;
        docs.replace(&hosts_document(), version, "restored draft\n".into())
            .unwrap();
    });
    view.update(stage.cx, |view, cx| view.save_document(cx));
    stage.cx.update(|window, cx| {
        let state = view.read(cx).editor.as_ref().unwrap().state.clone();
        state.update(cx, |state, cx| state.focus(window, cx));
        window.dispatch_action(Box::new(SaveDocument), cx);
    });
    let (answer, answered) = futures_channel::oneshot::channel();
    view.update(stage.cx, |view, cx| view.save_for_guard(answer, cx));
    stage.cx.run_until_parked();

    assert_eq!(
        answered.await,
        Ok(false),
        "the guard hears nothing was saved"
    );
    assert!(stage.sent("beginSave").is_empty(), "no save was sent");
    assert_eq!(
        stage.draft(),
        ("restored draft\n".into(), true),
        "the draft is kept"
    );
}

#[gpui::test]
async fn edit_anyway_unlocks_for_the_tab_s_life(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    stage.click("file-outside-notice-action");
    // Hidden and shown again, as switching tabs does: still unlocked.
    view.update_in(stage.cx, |view, window, cx| {
        gpui_kit::component::dock::BasePanel::set_active(view, false, window, cx);
        gpui_kit::component::dock::BasePanel::set_active(view, true, window, cx);
    });
    stage.cx.run_until_parked();
    // Drawing the tab puts its kept draft back in a new editor.
    stage.draw();
    stage.type_in(&view, "x");

    let (text, dirty) = stage.draft();
    assert!(
        dirty && text.contains('x'),
        "typing edits the draft: {text:?}"
    );
    view.update(stage.cx, |view, cx| view.save_document(cx));
    stage
        .until(&view, "the save", |view| view.saving.is_none())
        .await;
    assert_eq!(
        stage.sent("beginSave"),
        [
            json!({"target": target(), "expectedOwner": owner(), "expectedRevision": "r1", "size": text.len()})
        ]
    );
}

#[gpui::test]
async fn closing_and_reopening_is_locked_again(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    stage.click("file-outside-notice-action");
    view.update_in(stage.cx, |view, window, cx| view.request_close(window, cx));
    stage.cx.run_until_parked();
    let alias = outside_key(None, "/etc/hosts");
    assert!(
        stage
            .cx
            .update(
                |_, cx| crate::core::ui::center_panes::CenterPanes::find::<FileView>(&alias, cx)
            )
            .is_none(),
        "the tab closed"
    );

    let reopened = stage.outside_tab().await;
    assert!(reopened != view, "a new tab");
    reopened.read_with(stage.cx, |view, _| {
        assert_eq!(view.banner_words(), Some(BANNER))
    });
    stage.type_in(&reopened, "typed");
    assert_eq!(stage.draft(), ("disk\n".into(), false));
}

#[gpui::test]
async fn the_banner_and_status_say_outside_project(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    view.read_with(stage.cx, |view, _| {
        assert_eq!(view.banner_words(), Some(BANNER));
        assert_eq!(view.status_words(), Some("outside project"));
    });

    stage.click("file-outside-notice-action");
    view.read_with(stage.cx, |view, _| {
        assert_eq!(view.banner_words(), None);
        assert_eq!(view.status_words(), None);
    });
    assert!(
        stage.cx.debug_bounds("file-outside-notice").is_none(),
        "the banner is gone"
    );
}

#[gpui::test]
async fn an_outside_tab_has_no_history(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let view = stage.outside_tab().await;
    view.update(stage.cx, |view, cx| {
        assert!(!view.has_history());
        view.toggle_history(cx);
        assert!(
            !view.history_shown && view.history.is_none(),
            "History can't open"
        );
    });
}

#[gpui::test]
async fn a_session_file_is_never_locked(cx: &mut TestAppContext) {
    let session = |method: &str, args: &serde_json::Value| match method {
        "owner" => Ok(
            json!({"machine": super::super::outside::tests::ENGINE, "checkoutRoot": "/etc", "remoteSessionId": "s1"}),
        ),
        _ => answers(method, args),
    };
    let mut stage = stage(cx, Box::new(session));
    stage.open("s1", "hosts", None);
    let view = stage.tab(&FileView::session_tab_key("s1", "hosts"));
    stage.loaded(&view).await;
    stage.show(&view);
    view.read_with(stage.cx, |view, _| {
        assert_eq!(view.banner_words(), None);
        assert_eq!(view.status_words(), None);
        assert!(view.has_history());
    });
    assert!(stage.cx.debug_bounds("file-outside-notice").is_none());
    stage.type_in(&view, "x");
    let (text, dirty) = stage.draft();
    assert!(
        dirty && text.contains('x'),
        "typing edits the draft: {text:?}"
    );
}
