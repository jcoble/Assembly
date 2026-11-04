//! Lane allocation ported from the existing gitGraphLanes.ts; pixels belong to the timeline.
use super::git_history::GitCommit;
use std::collections::HashSet;
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum EdgeKind {
    Passing,
    Child,
    Parent,
}
#[derive(Clone, Debug, PartialEq)]
pub struct LaneRow {
    pub lane: usize,
    pub edges: Vec<(EdgeKind, usize, usize)>,
    pub merge: bool,
    pub root: bool,
}
fn claim(columns: &mut Vec<Option<String>>, sha: &str) -> usize {
    if let Some(index) = columns.iter().position(Option::is_none) {
        columns[index] = Some(sha.into());
        index
    } else {
        columns.push(Some(sha.into()));
        columns.len() - 1
    }
}
pub fn lanes<'a>(commits: impl IntoIterator<Item = &'a GitCommit>) -> Vec<LaneRow> {
    let mut columns: Vec<Option<String>> = Vec::new();
    let mut seen = HashSet::new();
    let mut rows = Vec::new();
    for commit in commits {
        let sha = commit.sha.trim();
        if sha.is_empty() || !seen.insert(sha) {
            continue;
        }
        let mut parents = Vec::new();
        for parent in &commit.parents {
            let parent = parent.trim();
            if !parent.is_empty() && !parents.contains(&parent) {
                parents.push(parent);
            }
        }
        let waiting = columns
            .iter()
            .position(|value| value.as_deref() == Some(sha));
        let lane = waiting.unwrap_or_else(|| claim(&mut columns, sha));
        let mut edges = Vec::new();
        for (index, value) in columns.iter().enumerate() {
            if index != lane && value.is_some() {
                edges.push((EdgeKind::Passing, index, index));
            }
        }
        if waiting.is_some() {
            edges.push((EdgeKind::Child, lane, lane));
        }
        if let Some(first) = parents.first() {
            if let Some(existing) = columns.iter().enumerate().find_map(|(index, value)| {
                (index != lane && value.as_deref() == Some(*first)).then_some(index)
            }) {
                columns[lane] = None;
                edges.push((EdgeKind::Parent, lane, existing));
            } else {
                columns[lane] = Some((*first).into());
                edges.push((EdgeKind::Parent, lane, lane));
            }
            for parent in parents.iter().skip(1) {
                let target = columns
                    .iter()
                    .position(|value| value.as_deref() == Some(*parent))
                    .unwrap_or_else(|| claim(&mut columns, parent));
                edges.push((EdgeKind::Parent, lane, target));
            }
        } else {
            columns[lane] = None;
        }
        edges.sort_by_key(|(_, from, _)| *from);
        rows.push(LaneRow {
            lane,
            edges,
            merge: parents.len() > 1,
            root: parents.is_empty(),
        });
    }
    rows
}
#[cfg(test)]
#[path = "graph_lanes_tests.rs"]
mod tests;
