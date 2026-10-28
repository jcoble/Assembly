//! `Listing` is one of the Context tab's lists as its read goes: reading, read, or failed.

#[derive(Clone, Debug, PartialEq)]
pub enum Listing<T> {
    Reading,
    Read(Vec<T>),
    /// Why the read failed.
    Failed(String),
}

impl<T> Listing<T> {
    /// The same read, with its list changed.
    pub fn map<U>(self, change: impl FnOnce(Vec<T>) -> Vec<U>) -> Listing<U> {
        match self {
            Listing::Reading => Listing::Reading,
            Listing::Read(list) => Listing::Read(change(list)),
            Listing::Failed(why) => Listing::Failed(why),
        }
    }
}
