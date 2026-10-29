//! Tests for the quick filter pills' picks (docs/behavior/sessions-list.md, sessions-list#7).

use super::filter_pills::{FilterGroup, FilterOption, Pills};

fn option(value: &str, label: &str) -> FilterOption {
    FilterOption {
        value: value.into(),
        label: label.into(),
    }
}

/// Provider: Claude, Codex, Antigravity; Location: Local, Remote.
fn pills() -> Pills {
    let providers = [
        option("claude", "Claude"),
        option("codex", "Codex"),
        option("antigravity", "Antigravity"),
    ];
    let locations = [option("local", "Local"), option("remote", "Remote")];
    Pills {
        groups: vec![
            FilterGroup::new("Provider", providers.into()),
            FilterGroup::new("Location", locations.into()),
        ],
        open: None,
    }
}

#[test]
fn picking_two_values_joins_their_labels_in_option_order() {
    let mut pills = pills();
    pills.toggle(0, "codex");
    pills.toggle(0, "claude");
    assert_eq!(pills.groups[0].picked, ["claude", "codex"]);
    assert_eq!(pills.label(0), "Claude/Codex");
    assert_eq!(pills.label(1), "Location");
}

#[test]
fn picking_every_value_reads_all() {
    let mut pills = pills();
    pills.toggle(1, "remote");
    pills.toggle(1, "local");
    assert_eq!(pills.label(1), "All");
}

#[test]
fn unpicking_the_last_value_clears_the_group() {
    let mut pills = pills();
    pills.toggle(0, "codex");
    pills.toggle(0, "codex");
    assert!(pills.groups[0].picked.is_empty());
    assert_eq!(pills.label(0), "Provider");
}

#[test]
fn clear_empties_every_group() {
    let mut pills = pills();
    pills.toggle(0, "claude");
    pills.toggle(1, "local");
    pills.open(1);
    pills.clear();
    assert!(pills.groups.iter().all(|group| group.picked.is_empty()));
    assert_eq!(pills.open, None);
}

#[test]
fn closing_keeps_the_picks() {
    let mut pills = pills();
    pills.open(0);
    pills.toggle(0, "claude");
    assert_eq!(pills.open, Some(0));
    pills.close();
    assert_eq!(pills.open, None);
    assert_eq!(pills.label(0), "Claude");
}
