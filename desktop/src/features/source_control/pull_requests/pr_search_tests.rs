use super::*;

// Pins: old pullRequestSearch.ts filters on the Pull requests tab.
#[test]
fn state_and_scope_become_search_terms_newest_first() {
    let query = pr_query(PrState::Merged, PrScope::Mine, "");
    assert_eq!(query.state, "all");
    assert_eq!(query.search, "is:merged author:@me sort:updated-desc");
    let closed = pr_query(PrState::Closed, PrScope::NeedsReview, "");
    assert_eq!(
        closed.search,
        "is:closed is:unmerged review-requested:@me sort:updated-desc"
    );
    let all = pr_query(PrState::All, PrScope::Everyone, "");
    assert_eq!(
        (all.state, all.search.as_str()),
        ("all", "sort:updated-desc")
    );
    assert_eq!(pr_query(PrState::Open, PrScope::Everyone, "").state, "open");
}
#[test]
fn a_plain_word_also_finds_its_branch_and_author() {
    assert_eq!(
        pr_query(PrState::All, PrScope::Everyone, "fix/login").search,
        "(fix/login OR head:fix/login OR author:fix/login) sort:updated-desc"
    );
}
#[test]
fn github_syntax_and_quoted_phrases_pass_through() {
    assert_eq!(
        pr_query(PrState::All, PrScope::Everyone, "label:bug \"two words\"").search,
        "label:bug \"two words\" sort:updated-desc"
    );
}
#[test]
fn a_number_names_one_pull_request_in_any_state() {
    for text in ["#131", "131"] {
        let query = pr_query(PrState::Merged, PrScope::Mine, text);
        assert_eq!(query.state, "all");
        assert_eq!(query.search, "131 sort:updated-desc");
    }
}
