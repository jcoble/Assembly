//! Pull request pane tests for choosing the project that answers: a repository on two machines, local projects, and
//! staying with the project already answering along a stack.
use super::*;

#[gpui::test]
async fn a_repository_on_two_machines_waits_for_the_person_to_say_which(cx: &mut TestAppContext) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(FAR, "far")]),
        Source::Answers,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_pr(Some("o/r".into()), 7, cx)));
    window.run_until_parked();
    window.update(|_, cx| {
        let pane = pane.read(cx);
        let choice = pane.choosing.as_ref().expect("the question");
        assert_eq!(choice.projects.len(), 2);
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });
    assert!(calls(&fake.seen, "resolve_source").is_empty());
    assert!(calls(&fake.seen, "pr").is_empty());

    window.update(|_, cx| pane.update(cx, |pane, cx| pane.choose_project(FAR, cx)));
    window.update(|_, cx| assert!(pane.read(cx).choosing.is_none()));
    until("the chosen read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], FAR);
    assert_eq!(read["expectedSource"]["routeMachine"], "far");
}

#[gpui::test]
async fn cancelling_forgets_the_question_and_a_late_answer_opens_nothing(cx: &mut TestAppContext) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(FAR, "far")]),
        Source::HoldsFirst,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_project(Some(LANE.into()), cx);
            pane.select_pr(Some("o/r".into()), 7, cx);
        })
    });
    window.update(|_, cx| assert!(pane.read(cx).opening.is_some()));
    until(
        "the first question",
        &mut fake.changes,
        &fake.changed,
        || {
            window.run_until_parked();
            !calls(&fake.seen, "resolve_source").is_empty()
        },
    )
    .await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.cancel_open(cx)));
    window.update(|_, cx| assert!(pane.read(cx).opening.is_none()));

    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_project(Some(FAR.into()), cx);
            pane.select_pr(Some("o/r".into()), 8, cx);
        })
    });
    until("the second read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let reads = calls(&fake.seen, "pr");
    assert_eq!(reads.len(), 1, "{reads:?}");
    assert_eq!(reads[0]["number"], 8);
    assert_eq!(reads[0]["projectId"], FAR);
}

#[gpui::test]
async fn a_row_under_all_local_projects_opens_this_computers_copy_without_asking(
    cx: &mut TestAppContext,
) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(HERE, "local")]),
        Source::Answers,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    let row =
        serde_json::from_value(json!({"repository":"o/r","number":7,"title":"The row"})).unwrap();
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_row(row, cx)));
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(
            pane.selected
                .as_ref()
                .and_then(|wanted| wanted.row.as_ref())
                .unwrap()
                .title,
            "The row"
        );
    });
    window.update(|_, cx| assert!(pane.read(cx).choosing.is_none()));
    until("the local read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.selected.is_none());
        let detail = pane.detail.as_ref().expect("the page").read(cx);
        assert_eq!(detail.row().unwrap().title, "The row");
    });
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], HERE);
    assert_eq!(read["expectedSource"]["machine"], "box");
    assert_eq!(read["expectedSource"]["routeMachine"], Value::Null);
}

#[gpui::test]
async fn the_all_list_searches_local_projects_and_a_picked_remote_searches_only_itself(
    cx: &mut TestAppContext,
) {
    let far = json!({
        "id":FAR,"machine":"far","rootPath":"/far/x","title":"x",
        "repoKey":"github.com/o/far","createdAt":1,
    });
    let mut fake = fake(cx, json!([holder(HERE, "local"), far]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.sync_scope(cx)));
    window.update(|_, cx| assert_eq!(pane.read(cx).scope, vec!["o/r".to_string()]));
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_project(Some(FAR.into()), cx)));
    window.update(|_, cx| assert_eq!(pane.read(cx).scope, vec!["o/far".to_string()]));
}

#[gpui::test]
async fn moving_along_a_stack_stays_with_the_project_already_answering(cx: &mut TestAppContext) {
    let mut fake = fake(
        cx,
        json!([holder(LANE, "lane"), holder(FAR, "far")]),
        Source::Answers,
    );
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.select_pr(Some("o/r".into()), 7, cx);
            pane.choose_project(LANE, cx);
        })
    });
    until("the first read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let shown: crate::features::source_control::pull_requests::pull_request::PullRequestDetail =
        serde_json::from_value(json!({"repository":"o/r","localRoot":"/lane/r","number":7,"title":"PR","body":"","bodyBlocks":[],"url":"https://github.com/o/r/pull/7","author":"author","headBranch":"feature","baseBranch":"main","headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"","createdAt":"","mergedAt":"","mergedBy":"","closedAt":"","commits":[],"comments":[],"reviews":[],"reviewers":[],"checks":[],"files":[],"moreFiles":false,"timeline":[]})).unwrap();
    window.update(|_, cx| {
        let detail = pane.read(cx).detail.clone().expect("the page");
        detail.update(cx, |detail, _| detail.detail = Some(shown));
        pane.update(cx, |pane, cx| pane.select_pr(Some("O/R".into()), 8, cx));
        assert!(pane.read(cx).choosing.is_none());
    });
    until("the next read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        calls(&fake.seen, "pr").len() == 2
    })
    .await;
    let reads = calls(&fake.seen, "pr");
    assert_eq!(reads[1]["number"], 8);
    assert_eq!(reads[1]["projectId"], LANE);
    assert_eq!(calls(&fake.seen, "resolve_source").len(), 1);
}

// Feature 28 parity item 2: a pull request link followed from anywhere, for a repository that isn't a project, says so
// exactly as the same link pasted into the search does, with the way to GitHub.
#[gpui::test]
async fn a_followed_link_to_a_repository_that_is_no_project_says_so_as_a_pasted_one_does(
    cx: &mut TestAppContext,
) {
    let mut fake = fake(cx, json!([holder(LANE, "lane")]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    let url = "https://github.com/other/r/pull/7";
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.search(url.into(), cx)));
    let pasted = window.update(|_, cx| pane.read(cx).missing_link.clone());
    assert!(pasted.is_some(), "the pasted link's notice");
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.missing_link = None;
            pane.apply_intent(
                OpenPullRequests {
                    session_id: None,
                    repo: Some("other/r".into()),
                    number: Some(7),
                },
                cx,
            )
        })
    });
    window.run_until_parked();
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.missing_link, pasted);
        assert_eq!(pane.failure, None);
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });
    assert!(calls(&fake.seen, "resolve_source").is_empty());
}

#[gpui::test]
async fn a_followed_link_to_a_project_s_repository_opens_it_whatever_its_case(
    cx: &mut TestAppContext,
) {
    let mut fake = fake(cx, json!([holder(LANE, "lane")]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| {
        pane.update(cx, |pane, cx| {
            pane.apply_intent(
                OpenPullRequests {
                    session_id: None,
                    repo: Some("O/R".into()),
                    number: Some(7),
                },
                cx,
            )
        })
    });
    until("the read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    assert_eq!(calls(&fake.seen, "pr")[0]["projectId"], LANE);
    window.update(|_, cx| assert_eq!(pane.read(cx).missing_link, None));
}
