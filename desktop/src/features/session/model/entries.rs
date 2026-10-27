//! `Entry` is one row of the virtual list: the loading line, an item, a fold's heading, or the working line. A
//! cancelled run's error, which draws nothing, is left out. Ported from session/entries.ts.

use std::collections::{HashMap, HashSet};

use super::fold::Fold;
use super::item::Item;

#[derive(Clone, Debug, PartialEq)]
pub enum Entry {
    /// An older page is loading.
    Loading,
    /// The item at this index.
    Item(usize),
    /// A fold's heading, at the index of the first item it hides; `ends_reply` when, closed, it ends the reply.
    FoldHeading { run_id: String, first: usize, ends_reply: bool },
    /// A live turn's "Working for" line.
    Working,
}

impl Entry {
    /// The list's rows. `open`: the folds the reader opened, by run id.
    pub fn all(
        _items: &[Item],
        _folds: &HashMap<String, Fold>,
        _open: &HashSet<String>,
        _loading: bool,
        _working: bool,
    ) -> Vec<Entry> {
        todo!()
    }

    /// The row's key for the list, unique and kept across updates: an item's key, `fold:<run>`, or a line's name.
    pub fn key(&self, _items: &[Item]) -> String {
        todo!()
    }
}
