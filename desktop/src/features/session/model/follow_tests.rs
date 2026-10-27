//! A sent prompt goes to the top once its item arrives, unless the reader scrolled first.

use super::follow::{Follow, FollowEvent, FollowStep};

// Pins: g0-session#5
#[test]
fn a_sent_prompt_goes_to_the_top_once_its_item_arrives() {
    let mut follow = Follow::default();
    assert_eq!(
        follow.on(FollowEvent::UserItem("u0".into())),
        FollowStep::None,
        "a prompt sent elsewhere stays put"
    );
    assert_eq!(follow.on(FollowEvent::Send), FollowStep::None);
    assert_eq!(
        follow.on(FollowEvent::UserItem("u1".into())),
        FollowStep::ShowAtTop("u1".into())
    );
    assert_eq!(
        follow.on(FollowEvent::UserItem("u2".into())),
        FollowStep::None,
        "once per send"
    );
}

// Pins: g0-session#5, g0-session#8
#[test]
fn scrolling_before_the_prompt_arrives_keeps_the_view_where_the_reader_put_it() {
    let mut follow = Follow::default();
    follow.on(FollowEvent::Send);
    assert_eq!(follow.on(FollowEvent::ReaderInput), FollowStep::None);
    assert_eq!(
        follow.on(FollowEvent::UserItem("u1".into())),
        FollowStep::None
    );
}
