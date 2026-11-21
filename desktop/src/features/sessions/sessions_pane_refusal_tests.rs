//! The rail's words for a session that didn't open.

use super::refusal_words;
use crate::core::engine::EngineRefusal;

#[test]
fn a_refused_open_names_the_machine_it_could_not_reach_and_otherwise_gives_the_reason() {
    let refused = |code: &str| EngineRefusal {
        code: code.into(),
        message: "The reason.".into(),
    };
    assert_eq!(
        refusal_words(Some("Workbox"), &refused("source_changed")),
        "That session is on Workbox, which this app isn't connected to."
    );
    assert_eq!(
        refusal_words(Some("Workbox"), &refused("nonresumable")),
        "That session didn't open: The reason."
    );
    assert_eq!(
        refusal_words(None, &refused("source_changed")),
        "That session didn't open: The reason."
    );
}

// Follow-up, finding 2: a session the backend can't find gets words a person can act on.
#[test]
fn a_session_that_isnt_found_says_what_to_check() {
    let refused = EngineRefusal {
        code: "unknown_engine_session".into(),
        message: "The Engine session is unavailable.".into(),
    };
    let words = "That session wasn't found. It may have been removed, or the link may be wrong; \
                 check the Sessions list.";
    assert_eq!(refusal_words(None, &refused), words);
    assert_eq!(refusal_words(Some("Workbox"), &refused), words);
}

// TSK-1692: a remote session never saved here says which machine to connect, in the backend's words.
#[test]
fn a_session_not_saved_here_says_which_machine_to_connect() {
    let refused = EngineRefusal {
        code: "not_saved".into(),
        message: "Not saved on this machine. Connect workbox-test to open it.".into(),
    };
    assert_eq!(
        refusal_words(Some("workbox-test"), &refused),
        "Not saved on this machine. Connect workbox-test to open it."
    );
}
