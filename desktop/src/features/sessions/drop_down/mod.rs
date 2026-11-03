//! Rail drop-down consumers of the shared source-qualified Engine feed.
pub mod started_sessions;

pub mod need_row;
#[cfg(test)]
mod need_row_tests;

pub(super) mod rail_details;

/// Display ownership uses the recorded candidate tuple only; all operations still validate their actual source.
pub(super) fn rail_details_key(row:&super::rail_page::RailEntry)->gpui_kit::SharedString {
    serde_json::to_string(&(row.source.as_ref().map(|source|source.machine.as_str()),&row.machine,&row.session_id)).expect("string tuple").into()
}

pub(super) mod rail_sources;
#[cfg(test)]
mod rail_sources_tests;

pub(super) mod native_details;
