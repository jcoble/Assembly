//! Proof fixes brief item 2: a pull request link followed before the projects are known waits for the pushed projects,
//! then routes; a connection that ends first says the projects couldn't be read.
use super::*;

/// A backend holding project LANE for o/r. Its projects list answers only when the test's `release` call arrives, or,
/// with `drops`, the connection drops at the list.
fn late_projects(cx: &mut TestAppContext, drops: bool) -> Fake {
    let seen = Arc::new(Mutex::new(Vec::<Value>::new()));
    let observed = seen.clone();
    let (changed, changes) = unbounded();
    let heard = changed.clone();
    let mut held: Option<Value> = None;
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let _ = heard.unbounded_send(());
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        observed.lock().unwrap().push(frame.clone());
        let answer = |result: Value| json!({"t":"result","id":frame["id"],"result":result});
        match frame["method"].as_str() {
            Some("list") if drops => vec![Reply::Drop],
            Some("list") => {
                held = Some(answer(
                    json!({"projects":[holder(LANE, "lane")],"next":null}),
                ));
                vec![]
            }
            Some("release") => held
                .take()
                .into_iter()
                .chain([answer(json!({}))])
                .map(Reply::Frame)
                .collect(),
            Some("resolve_source") => vec![Reply::Frame(answer(
                json!({"machine":"engine","generation":3,"routeMachine":"lane"}),
            ))],
            Some("prs") => vec![Reply::Frame(answer(
                json!({"items":[],"cursor":null,"total":0}),
            ))],
            _ => vec![],
        }
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        let endpoint = || Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint(), cx);
        SocketClient::start_bulk(endpoint(), cx);
    });
    Fake {
        _backend: backend,
        seen,
        changes,
        changed,
    }
}

fn click_link(pane: &gpui::Entity<PrPane>, window: &mut gpui::VisualTestContext) {
    let intent = OpenPullRequests {
        session_id: None,
        repo: Some("o/r".into()),
        number: Some(7),
    };
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.apply_intent(intent, cx)));
}

#[gpui::test]
async fn a_link_followed_before_the_projects_are_known_opens_once_they_arrive(
    cx: &mut TestAppContext,
) {
    let mut fake = late_projects(cx, false);
    let window = cx.add_empty_window();
    let pane = window.update(|window, cx| cx.new(|cx| PrPane::new(None, window, cx)));
    click_link(&pane, window);
    until(
        "the projects read",
        &mut fake.changes,
        &fake.changed,
        || {
            window.run_until_parked();
            !calls(&fake.seen, "list").is_empty()
        },
    )
    .await;
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(
            pane.missing_link, None,
            "no notice while the projects are unknown"
        );
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });

    let socket = window.update(|_, cx| SocketClient::global(cx));
    let release = window.update(|_, cx| {
        socket
            .read(cx)
            .call::<Value>("test", "release", json!({}), None)
    });
    until("the link's read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    drop(release);
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], LANE);
    assert_eq!(read["number"], 7);
    window.update(|_, cx| assert_eq!(pane.read(cx).missing_link, None));
}

#[gpui::test]
async fn a_link_waiting_on_the_projects_says_they_could_not_be_read_when_the_connection_ends(
    cx: &mut TestAppContext,
) {
    let mut fake = late_projects(cx, true);
    let window = cx.add_empty_window();
    let pane = window.update(|window, cx| cx.new(|cx| PrPane::new(None, window, cx)));
    click_link(&pane, window);
    until("the notice", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        window.update(|_, cx| pane.read(cx).missing_link.is_some())
    })
    .await;
    window.update(|_, cx| {
        let pane = pane.read(cx);
        let (words, page) = pane.missing_link.clone().expect("the notice");
        assert_eq!(
            words,
            "The projects couldn't be read, so the link didn't open."
        );
        assert_eq!(page, "https://github.com/o/r/pull/7");
    });
    assert!(calls(&fake.seen, "pr").is_empty());
}
