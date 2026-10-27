//! A session's rows as the backend sends them (the `rows` topic and `rows/page`; RowWire.cs). Plain serde types:
//! the backend already parsed the markdown and worked out each tool's summary, so nothing here reads text.

pub mod ask;
#[cfg(test)]
mod ask_tests;
pub mod markdown;
pub mod plan;
pub mod row;
pub mod row_page;
pub mod subagent;
pub mod task;
pub mod tool;
pub mod turn_stats;
