//! Rail words derived from held canonical needs, independent of chat ask summaries.
use super::rail_page::RailEntry;
use crate::core::engine::engine_items::NeedItem;
use gpui_kit::SharedString;

pub fn chip(needs: &[NeedItem]) -> Option<SharedString> {
    let mut questions = 0;
    let mut approvals = 0;
    let mut total = 0;
    for need in needs.iter().filter(|need| need.state == "open") {
        total += 1;
        match need.kind.as_str() {
            "question" | "choice" => questions += 1,
            "permission" | "confirmation" => approvals += 1,
            _ => {}
        }
    }
    if total == 0 {
        return None;
    }
    Some(
        if questions == total {
            format!("{total} question{}", if total == 1 { "" } else { "s" })
        } else if approvals == total {
            format!("{total} approval{}", if total == 1 { "" } else { "s" })
        } else {
            format!("{total} need you")
        }
        .into(),
    )
}

pub fn oldest_open_preview(needs: &[NeedItem]) -> Option<&str> {
    needs
        .iter()
        .filter(|need| need.state == "open" && need.display_number.is_some())
        .min_by_key(|need| need.display_number)
        .and_then(|need| need.text.as_deref())
}

pub fn preview(entry: &RailEntry, needs: &[NeedItem]) -> SharedString {
    oldest_open_preview(needs)
        .or(entry.preview.as_deref())
        .unwrap_or_default()
        .to_string()
        .into()
}
