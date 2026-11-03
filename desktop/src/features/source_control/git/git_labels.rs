//! Small words shared by the Git views: how long ago something was, what a commit's ref decorations mean, and a
//! repository path split for a row.
use chrono::{DateTime, Datelike as _, Duration, Local, TimeZone, Utc};
use std::fmt::Display;

/// A distance inside the last day, then "yesterday", then a date, all in the reader's own time zone. A stamp in the
/// future means the clocks disagree, so it reads as "just now".
pub fn age_at<Tz: TimeZone>(at: DateTime<Utc>, now: DateTime<Tz>) -> String
where
    Tz::Offset: Display,
{
    let seconds = (now.clone().with_timezone(&Utc) - at).num_seconds();
    let when = at.with_timezone(&now.timezone());
    if seconds < 60 {
        return "just now".into();
    }
    if seconds < 3_600 {
        return format!("{}m ago", seconds / 60);
    }
    if seconds < 86_400 {
        return format!("{}h ago", seconds / 3_600);
    }
    if when.date_naive() == now.date_naive() - Duration::days(1) {
        return "yesterday".into();
    }
    if when.year() == now.year() {
        return when.format("%b %-d, %-I:%M %p").to_string();
    }
    when.format("%b %-d, %Y").to_string()
}
/// A commit's time as the row says it; an unreadable time is left out rather than printed back raw.
pub fn commit_age(committed_at: &str) -> String {
    commit_age_at(committed_at, Local::now())
}
pub fn commit_age_at<Tz: TimeZone>(committed_at: &str, now: DateTime<Tz>) -> String
where
    Tz::Offset: Display,
{
    match DateTime::parse_from_rfc3339(committed_at.trim()) {
        Ok(at) => age_at(at.with_timezone(&Utc), now),
        Err(_) => String::new(),
    }
}
/// A stash's age from its milliseconds.
pub fn age_of_millis(millis: i64) -> String {
    match DateTime::from_timestamp_millis(millis) {
        Some(at) => age_at(at, Local::now()),
        None => String::new(),
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum RefKind {
    Head,
    Branch,
    Tag,
}
#[derive(Clone, Debug, PartialEq)]
pub struct RefPill {
    pub label: String,
    pub kind: RefKind,
}
/// The names pointing at a commit. `HEAD -> name` is drawn as just the name, and that branch isn't repeated beside it.
pub fn ref_pills(refs: &[String]) -> Vec<RefPill> {
    let current: Vec<&str> = refs
        .iter()
        .filter_map(|decoration| decoration.strip_prefix("HEAD -> "))
        .collect();
    refs.iter()
        .filter(|decoration| !current.contains(&decoration.as_str()))
        .map(|decoration| {
            if let Some(name) = decoration.strip_prefix("HEAD -> ") {
                RefPill {
                    label: name.to_string(),
                    kind: RefKind::Head,
                }
            } else if decoration == "HEAD" {
                RefPill {
                    label: "HEAD".into(),
                    kind: RefKind::Head,
                }
            } else if let Some(name) = decoration.strip_prefix("tag: ") {
                RefPill {
                    label: name.to_string(),
                    kind: RefKind::Tag,
                }
            } else {
                RefPill {
                    label: decoration.clone(),
                    kind: RefKind::Branch,
                }
            }
        })
        .collect()
}
/// A repository path as a row draws it: the file's name, then the folder it sits in.
pub fn split_path(path: &str) -> (&str, &str) {
    match path.rfind('/') {
        Some(at) => (&path[at + 1..], &path[..at]),
        None => (path, ""),
    }
}
#[cfg(test)]
#[path = "git_labels_tests.rs"]
mod tests;
