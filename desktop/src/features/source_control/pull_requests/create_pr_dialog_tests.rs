use super::{CreatePrForm, CreatePrInput, create_failure};
use crate::core::intents::{open_pull_requests::OpenPullRequests, open_url::OpenInBrowser};
use crate::features::source_control::git_action::GitWrites;
use crate::features::source_control::git_operation::OperationStatus;
use crate::features::source_control::git_target::GitTarget;
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Modifiers};
use serde_json::json;
use std::{cell::RefCell, rc::Rc};
#[test]
fn no_create_without_an_actual_base_and_title() {
    let input = CreatePrInput {
        base: String::new(),
        title: "Title".into(),
        description: "Exact\nbody".into(),
        draft: false,
    };
    assert!(input.args().is_err());
    assert!(
        CreatePrInput {
            base: "actual-default".into(),
            title: "  ".into(),
            ..input.clone()
        }
        .args()
        .is_err()
    );
    let args = CreatePrInput {
        base: " actual-default ".into(),
        title: " Title ".into(),
        draft: true,
        ..input
    }
    .args()
    .unwrap();
    assert_eq!(args["base"], "actual-default");
    assert_eq!(args["title"], "Title");
    assert_eq!(args["description"], "Exact\nbody");
    assert_eq!(args["draft"], true);
}
#[test]
fn a_push_failure_says_nothing_was_created() {
    assert_eq!(
        create_failure(Some("The push was refused.".into())),
        "The push was refused. Nothing was created."
    );
    assert_eq!(
        create_failure(None),
        "The pull request couldn't be created. Nothing was created."
    );
}
// Pins: g9#13
#[gpui::test]
fn a_created_pr_offers_to_open_it(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let slot = Rc::new(RefCell::new(None));
    let made = slot.clone();
    let (_, visual) = cx.add_window_view(move |window, cx| {
        let writes = GitWrites::new(cx);
        let form =
            cx.new(|cx| CreatePrForm::new(GitTarget::new("s1"), writes, "lane".into(), window, cx));
        made.replace(Some(form.clone()));
        gpui_kit::base::Root::new(form, window, cx)
    });
    let form: gpui_kit::Entity<CreatePrForm> = slot.take().expect("the form");
    let urls = Rc::new(RefCell::new(Vec::new()));
    let tabs = Rc::new(RefCell::new(Vec::new()));
    let (url_seen, tab_seen) = (urls.clone(), tabs.clone());
    visual.update(|_, cx| {
        cx.on_action(move |intent: &OpenInBrowser, _| {
            url_seen.borrow_mut().push(intent.url.clone())
        });
        cx.on_action(move |intent: &OpenPullRequests, _| {
            tab_seen
                .borrow_mut()
                .push((intent.session_id.clone(), intent.number))
        });
    });
    let status: OperationStatus = serde_json::from_value(json!({
        "operationId":"o","instanceId":"i","machine":"m","version":1,"state":"succeeded",
        "result":{"number":7,"url":"https://github.com/o/r/pull/7","head":"lane"},"error":null
    }))
    .unwrap();
    form.update(visual, |form, cx| form.finish("createPr", status, None, cx));
    let settle = |visual: &mut gpui_kit::VisualTestContext| {
        visual.run_until_parked();
        visual.update(|window, cx| window.draw(cx).clear(cx));
    };
    settle(visual);
    let browser = visual
        .debug_bounds("created-pr-browser")
        .expect("Open on GitHub");
    visual.simulate_click(browser.center(), Modifiers::none());
    settle(visual);
    assert_eq!(
        *urls.borrow(),
        vec!["https://github.com/o/r/pull/7".to_string()]
    );
    let tab = visual
        .debug_bounds("created-pr-tab")
        .expect("Open in Pull requests");
    visual.simulate_click(tab.center(), Modifiers::none());
    settle(visual);
    assert_eq!(*tabs.borrow(), vec![(Some("s1".to_string()), Some(7))]);
}
