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
