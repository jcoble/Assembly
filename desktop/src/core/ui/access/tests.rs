//! What a named control writes into the accessibility tree: the node GPUI builds from each element (its role, id,
//! label and state), and the ids' shape. The whole window's tree needs an assistive client attached, which a test
//! window never has, so `scripts/agent-desktop-ids.ts` reads that one from the running app.

use gpui_kit::accesskit::Node;
use gpui_kit::{Element, InteractiveElement as _, Role, Toggled, div};

use super::control_ids::{self, ALL};
use super::{ExposedElement as _, say};

/// The node GPUI would build for `element`: its role, with what the element writes into it.
fn node(element: &impl Element) -> Node {
    let mut node = Node::new(element.a11y_role().expect("the element has a role"));
    element.write_a11y_info(&mut node);
    node
}

#[test]
fn a_line_of_words_is_a_label_the_tree_can_read() {
    let node = node(&say("sessions.empty", "No sessions here."));
    assert_eq!(node.role(), Role::Label);
    assert_eq!(node.author_id(), Some("sessions.empty"));
    assert_eq!(node.label(), Some("No sessions here."));
}

#[test]
fn a_tab_is_named_found_by_its_id_and_says_whether_it_is_picked() {
    let tab = |picked| {
        node(
            &div()
                .id("tab")
                .selected_in_tree(Role::Tab, "side-tabs.files", "Files", picked),
        )
    };
    let (picked, other) = (tab(true), tab(false));
    assert_eq!(picked.role(), Role::Tab);
    assert_eq!(picked.author_id(), Some("side-tabs.files"));
    assert_eq!(picked.label(), Some("Files"));
    assert_eq!(picked.is_selected(), Some(true));
    assert_eq!(other.is_selected(), Some(false));
}

#[test]
fn a_filter_value_is_a_button_that_says_whether_it_is_on() {
    let pill = |on| {
        node(
            &div()
                .id("pill")
                .toggle_in_tree("sessions.filter.status.done", "Done", on),
        )
    };
    assert_eq!(pill(true).role(), Role::Button);
    assert_eq!(pill(true).toggled(), Some(Toggled::True));
    assert_eq!(pill(false).toggled(), Some(Toggled::False));
    assert_eq!(pill(true).author_id(), Some("sessions.filter.status.done"));
}

#[test]
fn every_listed_id_is_area_dot_control_and_none_repeats() {
    let well_formed = |id: &str| {
        let (area, control) = id.split_once('.').unwrap_or_default();
        let word = |text: &str| {
            !text.is_empty()
                && text
                    .chars()
                    .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
        };
        word(area) && word(control)
    };
    for id in ALL {
        assert!(well_formed(id), "{id} is not <area>.<control>");
    }
    let mut seen = std::collections::HashSet::new();
    for id in ALL {
        assert!(seen.insert(id), "{id} is listed twice");
    }
}

#[test]
fn ids_for_a_control_that_repeats_add_what_tells_its_copies_apart() {
    use control_ids::*;
    assert_eq!(content_tab("Build Board"), "tabs.build-board");
    assert_eq!(side_tab("Sessions"), "side-tabs.sessions");
    assert_eq!(drawer_pane("Source Control"), "drawer.pane.source-control");
    assert_eq!(
        filter_group("sessions", "Provider"),
        "sessions.filter.provider"
    );
    assert_eq!(
        filter_value("sessions", "Provider", "Claude"),
        "sessions.filter.provider.claude"
    );
    assert_eq!(row("session:ab12"), "rows.session.ab12");
    assert_eq!(
        machine_button("Workbox Test", "connect"),
        "settings.machine.workbox-test.connect"
    );
    assert_eq!(board_tab("needs-you"), "board.tab.needs-you");
}
