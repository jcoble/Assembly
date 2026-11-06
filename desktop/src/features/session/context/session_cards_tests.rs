//! The Session and Context usage cards on the fake client: what they say from the live facts, when the session's total
//! is read, and the one message that replaces every card with no session picked.

use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, VisualTestContext};
use serde_json::{Value, json};

use super::super::listing::Listing;
use super::super::{ContextPane, FileTouched};
use super::{Words, not_reported, session_words, usage_words};
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::fake_client::FakeClient;
use crate::features::session::client::session_client::CallError;
use crate::features::session::context::live_facts::LiveFacts;
use crate::features::session::model::session_view::ContextReport;

fn open(cx: &mut TestAppContext) -> (Entity<ContextPane>, FakeClient, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    cx.update(LiveFacts::init);
    let client = FakeClient::default();
    let fake = client.clone();
    let (pane, cx) = cx.add_window_view(move |_, cx| ContextPane::new(Rc::new(fake), cx));
    (pane, client, cx)
}

fn pick(session: Option<&str>, cx: &mut VisualTestContext) {
    let session = session.map(String::from);
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| selected.select(session, cx))
    });
    cx.run_until_parked();
}

fn shown(pane: &Entity<ContextPane>, active: bool, cx: &mut VisualTestContext) {
    pane.update_in(cx, |pane, window, cx| pane.set_active(active, window, cx));
    cx.run_until_parked();
}

fn draw(cx: &mut VisualTestContext) {
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn tokens_reads(client: &FakeClient) -> usize {
    client
        .calls()
        .iter()
        .filter(|(call, _)| call == "rows/tokens")
        .count()
}

/// Shows the tab on s1 with an empty file list, no images and the given `rows/tokens` answer.
fn shown_on_s1(
    tokens: Result<Value, CallError>,
    pane: &Entity<ContextPane>,
    client: &FakeClient,
    cx: &mut VisualTestContext,
) {
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    client.queue_answer(tokens);
    pick(Some("s1"), cx);
    shown(pane, true, cx);
}

fn facts(
    pane: &Entity<ContextPane>,
    session: &str,
    key: Option<SessionKey>,
    // The newest ended turn is run "rN", none at 0.
    turns_ended: usize,
) -> LiveFacts {
    LiveFacts {
        publisher: pane.entity_id(),
        session: session.into(),
        key,
        provider: Some("claude".into()),
        context: Some(ContextReport {
            seq: 3,
            used: 51_285,
            size: 1_000_000,
            input_tokens: None,
            output_tokens: None,
        }),
        input_tokens: Some(40_000),
        output_tokens: Some(10_000),
        model: Some("Opus".into()),
        effort: Some("High".into()),
        access: Some("Ask when needed".into()),
        newest_prompt_seq: Some(2),
        newest_ended_run: (turns_ended > 0).then(|| format!("r{turns_ended}")),
        drafts: Vec::new(),
    }
}

fn publish(facts: LiveFacts, cx: &mut VisualTestContext) {
    cx.update(|_, cx| LiveFacts::publish(facts, cx));
    cx.run_until_parked();
}

fn texts(words: &[Words]) -> Vec<(&str, bool)> {
    words
        .iter()
        .map(|words| (words.text.as_str(), words.reported))
        .collect()
}

// Pins: g0-shape#16
#[gpui::test]
fn the_cards_say_the_selected_sessions_facts_and_total(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(Ok(json!({ "total": 1_234_567 })), &pane, &client, cx);
    publish(facts(&pane, "s1", None, 1), cx);
    draw(cx);

    let (live, total) = pane.read_with(cx, |pane, cx| (pane.live(cx), pane.total));
    assert_eq!(total, Some(Some(1_234_567)));
    assert_eq!(
        texts(&session_words(live.as_ref())),
        [("Opus", true), ("High", true), ("Ask when needed", true)]
    );
    let (percent, words) = usage_words(live.as_ref(), total);
    assert_eq!(percent, Some(5));
    assert_eq!(
        texts(&words),
        [
            ("Current context: 51,285", true),
            ("of 1,000,000", true),
            ("1,234,567 tokens", true),
            ("In: 40,000", true),
            ("Out: 10,000", true),
        ]
    );
    for id in [
        "context-model",
        "context-effort",
        "context-access",
        "context-used",
        "context-window",
        "context-percent",
        "context-total",
        "context-in",
        "context-out",
    ] {
        assert!(cx.debug_bounds(id).is_some(), "{id} is drawn");
    }
    let session = cx.debug_bounds("context-model").unwrap();
    let usage = cx.debug_bounds("context-used").unwrap();
    let files = cx.debug_bounds("context-note-empty").unwrap();
    let images = cx.debug_bounds("attachments-note-empty").unwrap();
    assert!(
        session.top() < usage.top() && usage.top() < files.top() && files.top() < images.top(),
        "the cards are in order"
    );
}

// Pins: g0-shape#16
#[gpui::test]
fn missing_facts_say_who_didnt_report_them(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    // An older remote has no `rows/tokens`; an answer with no total is the same.
    shown_on_s1(Ok(json!({})), &pane, &client, cx);
    let mut bare = facts(&pane, "s1", None, 0);
    (bare.context, bare.input_tokens, bare.output_tokens) = (None, None, None);
    (bare.model, bare.effort, bare.access) = (None, None, None);
    publish(bare, cx);
    draw(cx);

    let (live, total) = pane.read_with(cx, |pane, cx| (pane.live(cx), pane.total));
    assert_eq!(total, Some(None), "answered, with nothing reported");
    let missing = ("not reported by Claude", false);
    assert_eq!(
        texts(&session_words(live.as_ref())),
        [missing, missing, missing]
    );
    let (percent, words) = usage_words(live.as_ref(), total);
    assert_eq!(percent, None);
    assert_eq!(
        texts(&words),
        [
            ("Current context not reported by Claude", false),
            ("window size not reported by Claude", false),
            missing,
            ("In not reported by Claude", false),
            ("Out not reported by Claude", false),
        ]
    );
    assert!(cx.debug_bounds("context-used").is_some());
    assert!(
        cx.debug_bounds("context-percent").is_none(),
        "no bar without both numbers"
    );
}

// Pins: g0-shape#16
#[test]
fn a_not_reported_line_names_the_provider_as_people_read_it() {
    assert_eq!(not_reported(Some("claude")), "not reported by Claude");
    assert_eq!(
        not_reported(Some("Claude Code")),
        "not reported by Claude Code"
    );
    assert_eq!(not_reported(None), "not reported");
    let (_, words) = usage_words(None, None);
    assert_eq!(words[3].text, "In not reported");
}

// Pins: g0-shape#16
#[gpui::test]
fn with_no_session_one_message_replaces_every_card(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    client.queue_answer(Ok(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "lastSeq": 2 }]),
    ));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    client.queue_answer(Ok(json!({ "total": 9 })));
    pick(Some("s1"), cx);
    shown(&pane, true, cx);
    publish(facts(&pane, "s1", None, 0), cx);
    draw(cx);
    assert!(cx.debug_bounds("touched-file-src/a.rs").is_some());

    pick(None, cx);
    draw(cx);
    assert!(cx.debug_bounds("context-note-pick").is_some());
    for id in [
        "context-model",
        "context-used",
        "context-total",
        "touched-file-src/a.rs",
        "context-note-reading",
        "attachments-note-empty",
        "attachments-note-reading",
    ] {
        assert!(cx.debug_bounds(id).is_none(), "{id} isn't drawn");
    }
    assert_eq!(
        pane.read_with(cx, |pane, _| pane.files.clone()),
        Listing::Reading,
        "nor kept"
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn a_turn_ending_while_shown_reads_the_total_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(Ok(json!({ "total": 100 })), &pane, &client, cx);
    publish(facts(&pane, "s1", None, 1), cx);
    assert_eq!(tokens_reads(&client), 1, "facts arriving don't read again");
    assert_eq!(
        client
            .calls()
            .iter()
            .find(|(call, _)| call == "rows/tokens")
            .map(|(_, args)| args.clone()),
        Some(json!({ "session": "s1" }))
    );

    let mut typed = facts(&pane, "s1", None, 1);
    typed.drafts.clear();
    typed.newest_prompt_seq = Some(5);
    publish(typed, cx);
    assert_eq!(tokens_reads(&client), 1, "only a turn ending reads it");

    client.queue_answer(Ok(json!({ "total": 250 })));
    publish(facts(&pane, "s1", None, 2), cx);
    assert_eq!(tokens_reads(&client), 2);
    assert_eq!(pane.read_with(cx, |pane, _| pane.total), Some(Some(250)));
    assert_eq!(
        client
            .calls()
            .iter()
            .filter(|(call, _)| call == "rows/files")
            .count(),
        1,
        "the lists aren't read again"
    );
}

// Pins: g0-shape#13
#[gpui::test]
fn a_turn_ending_while_shown_reads_the_files_again(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    shown_on_s1(Ok(json!({ "total": 100 })), &pane, &client, cx);
    publish(facts(&pane, "s1", None, 1), cx);
    let a = || FileTouched {
        path: "src/a.rs".into(),
        added: 1,
        removed: 0,
        count: 1,
        name: None,
    };
    let files = |pane: &Entity<ContextPane>, cx: &mut VisualTestContext| {
        pane.read_with(cx, |pane, _| pane.files.clone())
    };

    client.queue_answer(Ok(json!({ "total": 250 })));
    client.queue_answer(Ok(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "count": 1, "lastSeq": 4 }]),
    ));
    publish(facts(&pane, "s1", None, 2), cx);
    let reads: Vec<Value> = (client.calls().into_iter())
        .filter(|(call, _)| call == "rows/files")
        .map(|(_, args)| args)
        .collect();
    assert_eq!(
        reads,
        [json!({ "session": "s1" }), json!({ "session": "s1" })]
    );
    assert_eq!(
        files(&pane, cx),
        Listing::Read(vec![a()]),
        "the list follows the run"
    );

    client.queue_answer(Ok(json!({ "total": 300 })));
    client.queue_answer(Err(CallError::Failed("disk is full".into())));
    publish(facts(&pane, "s1", None, 3), cx);
    assert_eq!(
        files(&pane, cx),
        Listing::Read(vec![a()]),
        "a failed re-read keeps the list"
    );

    client.queue_answer(Ok(json!({ "total": 400 })));
    let late = client.queue_pending();
    publish(facts(&pane, "s1", None, 4), cx);
    shown(&pane, false, cx);
    late.send(Ok(json!([]))).ok();
    cx.run_until_parked();
    assert_eq!(
        files(&pane, cx),
        Listing::Read(vec![a()]),
        "a read answered after a hide is dropped"
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn a_failed_total_leaves_the_lists(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    client.queue_answer(Ok(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "lastSeq": 2 }]),
    ));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    client.queue_answer(Err(CallError::Failed("disk is full".into())));
    pick(Some("s1"), cx);
    shown(&pane, true, cx);
    let (files, images, total) = pane.read_with(cx, |pane, _| {
        (pane.files.clone(), pane.attachments.clone(), pane.total)
    });
    assert_eq!(
        files,
        Listing::Read(vec![FileTouched {
            path: "src/a.rs".into(),
            added: 1,
            removed: 0,
            count: 0,
            name: None
        }])
    );
    assert_eq!(images, Listing::Read(vec![]));
    assert_eq!(total, Some(None), "read as not reported");
}

// Pins: g0-shape#17
#[gpui::test]
fn a_hidden_tab_reads_no_total(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick(Some("s1"), cx);
    publish(facts(&pane, "s1", None, 1), cx);
    publish(facts(&pane, "s1", None, 2), cx);
    assert_eq!(tokens_reads(&client), 0);

    shown_on_s1(Ok(json!({ "total": 1 })), &pane, &client, cx);
    assert_eq!(tokens_reads(&client), 1, "shown, it reads");
    shown(&pane, false, cx);
    publish(facts(&pane, "s1", None, 3), cx);
    assert_eq!(
        tokens_reads(&client),
        1,
        "hidden again, a turn ending reads nothing"
    );
}

// Pins: g0-shape#16
#[gpui::test]
fn facts_for_another_owner_of_the_same_id_read_as_not_reported(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let owner = |machine: &str| SessionKey {
        machine: machine.into(),
        id: "s1".into(),
    };
    let source = ExpectedRowSource {
        machine: "a".into(),
        generation: 0,
        route_machine: None,
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(owner("a"), source, true, cx)
        })
    });
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    client.queue_answer(Ok(json!({ "total": 7 })));
    shown(&pane, true, cx);
    publish(facts(&pane, "s1", Some(owner("b")), 1), cx);
    draw(cx);

    let live = pane.read_with(cx, |pane, cx| pane.live(cx));
    assert_eq!(live, None);
    assert_eq!(
        texts(&session_words(live.as_ref()))[0],
        ("not reported", false)
    );
    assert!(cx.debug_bounds("context-percent").is_none());

    publish(facts(&pane, "s1", Some(owner("a")), 1), cx);
    assert!(
        pane.read_with(cx, |pane, cx| pane.live(cx)).is_some(),
        "its own owner's facts show"
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn the_total_reads_as_reading_until_rows_tokens_answers(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    pick(Some("s1"), cx);
    publish(facts(&pane, "s1", None, 1), cx);
    let (live, total) = pane.read_with(cx, |pane, cx| (pane.live(cx), pane.total));
    assert_eq!(total, None, "nothing has answered");
    let (_, words) = usage_words(live.as_ref(), total);
    assert_eq!(
        texts(&words[2..3]),
        [("Reading…", false)],
        "not a figure nobody sent"
    );

    shown_on_s1(Ok(json!({})), &pane, &client, cx);
    let (live, total) = pane.read_with(cx, |pane, cx| (pane.live(cx), pane.total));
    let (_, words) = usage_words(live.as_ref(), total);
    assert_eq!(
        texts(&words[2..3]),
        [("not reported by Claude", false)],
        "answered with none"
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn a_refused_total_marks_the_source_changed_and_keeps_the_lists(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    let source = ExpectedRowSource {
        machine: "a".into(),
        generation: 0,
        route_machine: None,
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key.clone(), source, true, cx)
        })
    });
    client.queue_answer(Ok(
        json!([{ "path": "src/a.rs", "added": 1, "removed": 0, "lastSeq": 2 }]),
    ));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Owner replaced".into(),
    }));
    shown(&pane, true, cx);
    publish(facts(&pane, "s1", Some(key.clone()), 1), cx);

    let (files, total, valid) = pane.read_with(cx, |pane, _| {
        (
            pane.files.clone(),
            pane.total,
            pane.context_scope.as_ref().map(|scope| scope.valid),
        )
    });
    assert_eq!(
        files,
        Listing::Read(vec![FileTouched {
            path: "src/a.rs".into(),
            added: 1,
            removed: 0,
            count: 0,
            name: None
        }])
    );
    assert_eq!(total, Some(None));
    assert_eq!(valid, Some(false), "the refused source is no longer used");
    publish(facts(&pane, "s1", Some(key), 2), cx);
    assert_eq!(
        tokens_reads(&client),
        1,
        "a turn ending reads nothing from a refused source"
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn an_older_remotes_bad_args_reads_as_not_reported_and_keeps_the_source(cx: &mut TestAppContext) {
    let (pane, client, cx) = open(cx);
    let key = SessionKey {
        machine: "a".into(),
        id: "s1".into(),
    };
    let source = ExpectedRowSource {
        machine: "a".into(),
        generation: 0,
        route_machine: None,
    };
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            selected.commit_open(key.clone(), source, true, cx)
        })
    });
    client.queue_answer(Ok(json!([])));
    client.queue_answer(Ok(json!({ "prompts": [] })));
    // An older remote checks `expectedSource` against its own allow-list, which has no `tokens`.
    client.queue_answer(Err(CallError::BadArgs(
        "expectedSource requires a supported session call.".into(),
    )));
    shown(&pane, true, cx);
    publish(facts(&pane, "s1", Some(key), 1), cx);

    let (total, valid) = pane.read_with(cx, |pane, _| {
        (
            pane.total,
            pane.context_scope.as_ref().map(|scope| scope.valid),
        )
    });
    assert_eq!(total, Some(None), "answered with nothing");
    assert_eq!(valid, Some(true), "the source is still used");
    let facts = facts(&pane, "s1", None, 1);
    let (_, [_, _, words, _, _]) = usage_words(Some(&facts), total);
    assert_eq!(
        (words.text.as_str(), words.reported),
        ("not reported by Claude", false)
    );
}

// Pins: g0-shape#17
#[gpui::test]
fn not_reported_figures_wrap_whole_in_a_narrow_drawer(cx: &mut TestAppContext) {
    use crate::core::state::preferences::Preferences;
    use crate::core::theme::text::TextRole;
    use gpui_kit::{px, size};

    let (pane, client, cx) = open(cx);
    let width = px(260.);
    cx.simulate_resize(size(width, px(1200.)));
    shown_on_s1(Ok(json!({})), &pane, &client, cx);
    let mut bare = facts(&pane, "s1", None, 0);
    (bare.context, bare.input_tokens, bare.output_tokens) = (None, None, None);
    publish(bare, cx);
    draw(cx);

    let line =
        cx.update(|_, cx| Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Body)).1);
    let used = cx.debug_bounds("context-used").unwrap();
    let window = cx.debug_bounds("context-window").unwrap();
    assert!(
        window.right() <= width,
        "the window size stays inside the drawer"
    );
    assert!(used.right() <= window.left(), "the two don't overlap");
    for (id, bounds) in [("context-used", used), ("context-window", window)] {
        assert!(
            bounds.size.height >= line * 1.5,
            "{id} wraps onto a second line rather than cutting off: {bounds:?}"
        );
    }
}
