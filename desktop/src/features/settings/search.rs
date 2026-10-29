//! "Search settings": which sections and rows a search leaves. A row is left when its title, description, keywords,
//! card or section name holds the search, ignoring case; an empty search leaves everything. Replaces the `matches`,
//! `shownSections`, `shownSection` and `shownCards` of SettingsDialog.svelte:363-424.

use super::row::{ROWS, Row};
use super::section::{SECTIONS, Section};

/// The rows `query` leaves, in the catalog's order.
pub fn rows(query: &str) -> Vec<&'static Row> {
    let needle = query.trim().to_lowercase();
    ROWS.iter()
        .filter(|row| {
            let section = SECTIONS
                .iter()
                .find(|section| section.id == row.section)
                .map_or("", |section| section.label);
            let haystack = format!(
                "{} {} {} {} {section}",
                row.title, row.description, row.keywords, row.card
            );
            haystack.to_lowercase().contains(&needle)
        })
        .collect()
}

/// The sections the left column lists: every one for an empty search, else those with a row left.
pub fn sections(query: &str) -> Vec<&'static Section> {
    if query.trim().is_empty() {
        return SECTIONS.iter().collect();
    }
    let left = rows(query);
    SECTIONS
        .iter()
        .filter(|section| left.iter().any(|row| row.section == section.id))
        .collect()
}

/// The section the page shows: the chosen one while it's listed, else the first one listed, so the page is never
/// blank while the search finds something elsewhere.
pub fn shown(query: &str, chosen: &str) -> Option<&'static Section> {
    let listed = sections(query);
    listed
        .iter()
        .find(|section| section.id == chosen)
        .or(listed.first())
        .copied()
}

/// The cards of `section` with the rows `query` leaves in each, in the order their rows come.
pub fn cards(query: &str, section: &str) -> Vec<(&'static str, Vec<&'static Row>)> {
    let mut cards: Vec<(&'static str, Vec<&'static Row>)> = Vec::new();
    for row in rows(query).into_iter().filter(|row| row.section == section) {
        match cards.iter_mut().find(|(card, _)| *card == row.card) {
            Some((_, rows)) => rows.push(row),
            None => cards.push((row.card, vec![row])),
        }
    }
    cards
}

#[cfg(test)]
#[path = "search_tests.rs"]
mod tests;
