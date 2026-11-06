use serde_json::json;

use super::{BoardTab, BoundedSet, CAP, SessionTab, remember, saved_tabs};

#[test]
fn prefs_cap_at_500_dropping_oldest() {
    let mut set = BoundedSet::default();
    for index in 0..=CAP {
        set.insert(index.to_string());
    }
    assert_eq!(CAP, 500);
    assert!(!set.contains("0"), "the oldest went");
    assert!(set.contains("1"));
    assert!(set.contains("500"));
    set.insert("500".into());
    assert_eq!(
        set.to_json().as_array().unwrap().len(),
        CAP,
        "a repeat takes no room"
    );
    let mut tabs = Vec::new();
    for index in 0..=CAP {
        remember(&mut tabs, index.to_string(), SessionTab::Prs);
    }
    assert_eq!(tabs.len(), CAP);
    assert_eq!(tabs[0].0, "1", "the oldest tab went");
}

#[test]
fn saved_values_read_back_and_a_retired_name_falls_back() {
    let mut set = BoundedSet::of(Some(json!(["a", "b", 3])));
    assert!(set.contains("a"));
    set.remove("a");
    assert!(!set.contains("a"));
    assert_eq!(BoardTab::named("workflows"), Some(BoardTab::Workflows));
    assert_eq!(BoardTab::named("running"), None);
    assert_eq!(
        SessionTab::named(SessionTab::SubAgents.name()),
        Some(SessionTab::SubAgents)
    );
}

#[test]
fn saved_session_tabs_skip_what_they_cant_read() {
    let tabs = saved_tabs(&json!([["a", "workflow"], ["b", "retired"], "junk"]));
    assert_eq!(tabs, [("a".to_owned(), SessionTab::Workflow)]);
}
