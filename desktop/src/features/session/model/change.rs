//! `Change` is what applying a row did to the items, so the timeline measures only what changed: an unchanged item
//! keeps its place and its measured height.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Change {
    Appended,
    /// The item at this index changed.
    Updated(usize),
    /// Only the view's turn, plan, context or stats changed, or nothing did.
    Nothing,
}
