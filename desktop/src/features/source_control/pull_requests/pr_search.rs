//! The search behind the Pull requests tab's filters. The backend adds `is:pr` and the known repositories; everything
//! the person chooses or types is built here, as the old tab's pullRequestSearch.ts did, so the filters are plain data
//! a test can check. A plain word matches the title or body, the head branch or the author; anything else is GitHub
//! search syntax and passes through. A number, with or without `#`, names one pull request and is found in any state.

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub enum PrState {
    All,
    #[default]
    Open,
    Merged,
    Closed,
}
impl PrState {
    pub const ALL: [Self; 4] = [Self::Open, Self::Merged, Self::Closed, Self::All];
    pub fn label(self) -> &'static str {
        match self {
            Self::Open => "Open",
            Self::Merged => "Merged",
            Self::Closed => "Closed",
            Self::All => "All",
        }
    }
    fn terms(self) -> &'static str {
        match self {
            Self::Merged => "is:merged",
            Self::Closed => "is:closed is:unmerged",
            Self::Open | Self::All => "",
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub enum PrScope {
    #[default]
    Everyone,
    Mine,
    NeedsReview,
}
impl PrScope {
    pub const ALL: [Self; 3] = [Self::Everyone, Self::Mine, Self::NeedsReview];
    pub fn label(self) -> &'static str {
        match self {
            Self::Everyone => "Everyone",
            Self::Mine => "Mine",
            Self::NeedsReview => "Needs my review",
        }
    }
    fn terms(self) -> &'static str {
        match self {
            Self::Everyone => "",
            Self::Mine => "author:@me",
            Self::NeedsReview => "review-requested:@me",
        }
    }
}
#[derive(Debug, PartialEq)]
pub struct PrQuery {
    /// `open` has the backend add `is:open`; `all` adds nothing, and the search says the rest.
    pub state: &'static str,
    pub search: String,
}
const NEWEST: &str = "sort:updated-desc";

pub fn pr_query(state: PrState, scope: PrScope, text: &str) -> PrQuery {
    let text = text.trim();
    let number = text.strip_prefix('#').unwrap_or(text);
    if !number.is_empty() && number.bytes().all(|byte| byte.is_ascii_digit()) {
        return PrQuery {
            state: "all",
            search: format!("{number} {NEWEST}"),
        };
    }
    let mut quoted = false;
    let mut terms = vec![state.terms().to_string(), scope.terms().to_string()];
    for word in text.split_whitespace() {
        let plain = !quoted && word.chars().all(is_plain);
        if word.matches('"').count() % 2 == 1 {
            quoted = !quoted;
        }
        terms.push(if plain {
            format!("({word} OR head:{word} OR author:{word})")
        } else {
            word.to_string()
        });
    }
    terms.push(NEWEST.into());
    terms.retain(|term| !term.is_empty());
    PrQuery {
        state: if state == PrState::Open {
            "open"
        } else {
            "all"
        },
        search: terms.join(" "),
    }
}
fn is_plain(letter: char) -> bool {
    letter.is_ascii_alphanumeric() || matches!(letter, '_' | '.' | '/' | '-')
}

#[cfg(test)]
#[path = "pr_search_tests.rs"]
mod tests;
