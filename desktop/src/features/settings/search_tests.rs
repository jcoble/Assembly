use super::{cards, rows, sections, shown};

fn ids<T>(items: &[&T], id: impl Fn(&T) -> &'static str) -> Vec<&'static str> {
    items.iter().map(|item| id(item)).collect()
}

// The left column lists every section, under its group, in the old app's order.
#[test]
fn an_empty_search_lists_every_section_and_row() {
    let listed: Vec<(&str, &str)> = sections("  ")
        .iter()
        .map(|section| (section.group, section.label))
        .collect();
    assert_eq!(
        listed,
        [
            ("Interface", "Appearance"),
            ("Workspace", "Editor"),
            ("Workspace", "Terminal"),
            ("Application", "General"),
            ("Application", "Connections"),
            ("Application", "Updates"),
            ("Application", "Agents"),
            ("Application", "Plugins and Providers"),
            ("Application", "Utility model"),
        ]
    );
    assert_eq!(rows("").len(), super::super::row::ROWS.len());
}

// A search keeps the sections with a row left, and in each only the rows it matches.
#[test]
fn a_search_keeps_only_the_sections_and_rows_it_matches() {
    assert_eq!(
        ids(&sections("Line HEIGHT"), |section| section.id),
        ["editor", "terminal"]
    );
    assert_eq!(
        ids(&rows("line height"), |row| row.id),
        ["editor-line-height", "terminal-line-height"]
    );
}

// The words someone is likely to type match too: the row's description, keywords, card and section.
#[test]
fn a_search_reads_the_description_keywords_card_and_section() {
    assert_eq!(
        ids(&rows("monospace"), |row| row.id),
        ["code-font", "editor-font", "terminal-font"]
    );
    assert_eq!(ids(&rows("ssh"), |row| row.id), ["machines"]);
    assert_eq!(ids(&rows("colors"), |row| row.id), ["theme"]);
    assert_eq!(
        ids(&rows("terminal"), |row| row.id),
        [
            "terminal-font",
            "terminal-font-size",
            "terminal-line-height"
        ]
    );
}

// A search that empties the chosen section shows the first one left, so the page is never blank while results
// exist elsewhere; one that matches nothing shows nothing.
#[test]
fn the_shown_section_moves_to_the_first_one_left() {
    assert_eq!(
        shown("", "editor").map(|section| section.id),
        Some("editor")
    );
    assert_eq!(
        shown("ssh", "appearance").map(|section| section.id),
        Some("connections")
    );
    assert_eq!(
        shown("font size", "terminal").map(|section| section.id),
        Some("terminal")
    );
    assert!(shown("no such setting", "appearance").is_none());
    assert!(sections("no such setting").is_empty());
}

// A section's rows are grouped into its cards, in the order the rows come.
#[test]
fn a_sections_rows_are_grouped_into_cards() {
    let appearance: Vec<(&str, Vec<&str>)> = cards("", "appearance")
        .into_iter()
        .map(|(card, rows)| (card, rows.iter().map(|row| row.id).collect()))
        .collect();
    assert_eq!(
        appearance,
        [
            ("Colors", vec!["theme"]),
            ("Text", vec!["ui-font", "code-font", "ui-font-size"])
        ]
    );
    let searched: Vec<&str> = cards("size", "appearance")
        .into_iter()
        .map(|(card, _)| card)
        .collect();
    assert_eq!(searched, ["Text"]);
}
