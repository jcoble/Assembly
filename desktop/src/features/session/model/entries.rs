//! `Entry` is one row of the virtual list: the loading line, an item, a fold's heading, or the working line. A
//! cancelled run's error, which draws nothing, is left out. Ported from session/entries.ts.

use std::collections::{HashMap, HashSet};

use super::fold::Fold;
use super::item::{Item, ItemKind};
use super::notices::Notice;

#[derive(Clone, Debug, PartialEq)]
pub enum Entry {
    /// An older page is loading.
    Loading,
    /// The item at this index.
    Item(usize),
    /// A fold's heading, at the index of the first item it hides; `ends_reply` when, closed, it ends the reply.
    FoldHeading {
        run_id: String,
        first: usize,
        ends_reply: bool,
    },
    /// A live turn's "Working for" line.
    Working,
}

impl Entry {
    /// The list's rows. `open`: the folds the reader opened, by run id.
    pub fn all(
        items: &[Item],
        folds: &HashMap<String, Fold>,
        open: &HashSet<String>,
        loading: bool,
        working: bool,
    ) -> Vec<Entry> {
        let mut list = Vec::new();
        if loading {
            list.push(Entry::Loading);
        }
        // Each fold's heading, by run id, as its index in `list`.
        let mut heads: HashMap<&str, usize> = HashMap::new();
        for (at, item) in items.iter().enumerate() {
            let fold = item.run_id.as_ref().and_then(|run| folds.get(run));
            if let Some(fold) = fold.filter(|fold| fold.work.contains(&item.key)) {
                let head = *heads.entry(&fold.run_id).or_insert_with(|| {
                    list.push(Entry::FoldHeading {
                        run_id: fold.run_id.clone(),
                        first: at,
                        ends_reply: false,
                    });
                    list.len() - 1
                });
                if !open.contains(&fold.run_id) {
                    if ends_reply_at(items, at)
                        && let Entry::FoldHeading { ends_reply, .. } = &mut list[head]
                    {
                        *ends_reply = true;
                    }
                    continue;
                }
            }
            let draws = match &item.kind {
                ItemKind::Error { message, code } => {
                    Notice::of_error(message, code.as_deref()).is_some()
                }
                _ => true,
            };
            if draws || ends_reply_at(items, at) {
                list.push(Entry::Item(at));
            }
        }
        if working {
            list.push(Entry::Working);
        }
        list
    }

    /// The row's key for the list, unique and kept across updates: an item's key, `fold:<run>`, or a line's name.
    pub fn key(&self, items: &[Item]) -> String {
        match self {
            Entry::Loading => "line:loading".to_string(),
            Entry::Item(at) => items[*at].key.clone(),
            Entry::FoldHeading { run_id, .. } => format!("fold:{run_id}"),
            Entry::Working => "line:working".to_string(),
        }
    }
}

/// A reply's pieces sit under one agent-name label; the reply ends before the next prompt or turn line.
fn in_reply(item: Option<&Item>) -> bool {
    item.is_some_and(|item| !matches!(item.kind, ItemKind::User { .. } | ItemKind::Stats { .. }))
}

fn ends_reply_at(items: &[Item], at: usize) -> bool {
    in_reply(items.get(at)) && !in_reply(items.get(at + 1))
}
