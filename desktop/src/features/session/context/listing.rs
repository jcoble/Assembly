//! `Listing` is one of the Context tab's lists as its read goes: reading, read, or failed.

#[derive(Clone, Debug, PartialEq)]
pub enum Listing<T> {
    Reading,
    Read(Vec<T>),
    /// Why the read failed.
    Failed(String),
}
