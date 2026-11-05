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

/// A tab following session "s1", whose list shows o/r, with o/r and a/b each held by a project.
async fn following_s1(
    cx: &mut TestAppContext,
) -> (Fake, &mut gpui::VisualTestContext, gpui::Entity<PrPane>) {
    let other = json!({
        "id":FAR,"machine":"far","rootPath":"/far/b","title":"b","repoKey":"github.com/a/b","createdAt":1,
    });
    let mut fake = fake(cx, json!([holder(LANE, "lane"), other]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_following(Some("s1"), &mut fake, window).await;
    window.update(|_, cx| {
        let listed =
            crate::features::source_control::pull_requests::pull_request::PullRequestSummary {
                repository: "o/r".into(),
                number: 7,
                ..Default::default()
            };
        let queue = pane.read(cx).queue.clone();
        queue.update(cx, |queue, _| queue.page.items = vec![listed]);
    });
    (fake, window, pane)
}

fn follow(
    pane: &gpui::Entity<PrPane>,
    repo: &str,
    number: u64,
    window: &mut gpui::VisualTestContext,
) {
    let intent = OpenPullRequests {
        session_id: None,
        repo: Some(repo.into()),
        number: Some(number),
    };
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.apply_intent(intent, cx)));
}

// Review fix 1: with a session followed, a link to another project's repository opens through that project, not the
// session, as the old `openLink` did.
#[gpui::test]
async fn with_a_session_a_link_to_another_project_opens_through_that_project(
    cx: &mut TestAppContext,
) {
    let (mut fake, window, pane) = following_s1(cx).await;
    follow(&pane, "A/B", 3, window);
    until("the read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["projectId"], FAR);
    assert_eq!(read["session"], Value::Null);
    window.update(|_, cx| assert_eq!(pane.read(cx).session.as_deref(), Some("s1")));
}

#[gpui::test]
async fn with_a_session_a_link_to_a_repository_that_is_no_project_says_so(cx: &mut TestAppContext) {
    let (fake, window, pane) = following_s1(cx).await;
    follow(&pane, "x/y", 1, window);
    window.run_until_parked();
    window.update(|_, cx| {
        let pane = pane.read(cx);
        let (_, link) = pane.missing_link.clone().expect("the notice");
        assert_eq!(link, "https://github.com/x/y/pull/1");
        assert!(pane.opening.is_none() && pane.detail.is_none());
    });
    assert!(calls(&fake.seen, "pr").is_empty());
    assert!(calls(&fake.seen, "resolve_source").is_empty());
}

#[gpui::test]
async fn with_a_session_a_link_to_its_own_repository_opens_through_the_session(
    cx: &mut TestAppContext,
) {
    let (mut fake, window, pane) = following_s1(cx).await;
    follow(&pane, "o/r", 9, window);
    until("the read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    let read = calls(&fake.seen, "pr").remove(0);
    assert_eq!(read["session"], "s1");
    assert_eq!(read["number"], 9);
}

// Review fix 7: with the tab set to one project, a link to another project's repository moves the tab to that project
// and opens there, as the old `openLink` found the project by repository whatever the tab showed.
#[gpui::test]
async fn with_the_tab_on_a_project_a_link_to_another_project_moves_there(cx: &mut TestAppContext) {
    let other = json!({
        "id":FAR,"machine":"far","rootPath":"/far/b","title":"b","repoKey":"github.com/a/b","createdAt":1,
    });
    let mut fake = fake(cx, json!([holder(LANE, "lane"), other]), Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_project(Some(LANE.into()), cx)));
    follow(&pane, "a/b", 3, window);
    until("the read", &mut fake.changes, &fake.changed, || {
        window.run_until_parked();
        !calls(&fake.seen, "pr").is_empty()
    })
    .await;
    assert_eq!(calls(&fake.seen, "pr")[0]["projectId"], FAR);
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.project.as_deref(), Some(FAR));
        assert_eq!(pane.failure, None);
    });
}

// Review fix 7, several holders: a tab on a project that doesn't hold the link's repository moves to all local projects,
// and the tab asks which of the projects holding it should answer.
#[gpui::test]
async fn with_the_tab_on_a_project_a_link_held_by_several_asks_which(cx: &mut TestAppContext) {
    let held = |id: &str, machine: &str| {
        json!({
            "id":id,"machine":machine,"rootPath":format!("/{machine}/b"),"title":"b",
            "repoKey":"github.com/a/b","createdAt":1,
        })
    };
    let projects = json!([holder(LANE, "lane"), held(FAR, "far"), held(HERE, "local")]);
    let mut fake = fake(cx, projects, Source::Answers);
    let window = cx.add_empty_window();
    let pane = pane_with_projects(&mut fake, window).await;
    window.update(|_, cx| pane.update(cx, |pane, cx| pane.select_project(Some(LANE.into()), cx)));
    follow(&pane, "a/b", 3, window);
    window.run_until_parked();
    window.update(|_, cx| {
        let pane = pane.read(cx);
        assert_eq!(pane.project, None, "all local projects");
        assert_eq!(pane.failure, None);
        let choice = pane.choosing.as_ref().expect("the question");
        let mut ids: Vec<_> = choice
            .projects
            .iter()
            .map(|project| project.id.as_str())
            .collect();
        ids.sort();
        assert_eq!(ids, [FAR, HERE]);
    });
    assert!(calls(&fake.seen, "pr").is_empty());
}
