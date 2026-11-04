use super::{Hop, NoPicture, load};
use std::cell::{Cell, RefCell};
use std::time::{Duration, Instant};

const FIRST: &str = "https://github.com/octocat.png?size=40";
const PNG: &[u8] = b"\x89PNG\r\n\x1a\nrest";

fn picture() -> Hop {
    Hop::Answer {
        content_type: "image/png".to_owned(),
        bytes: PNG.to_vec(),
    }
}
fn redirect(to: &str) -> Hop {
    Hop::Redirect(to.to_owned())
}
type Loaded = (Result<Vec<u8>, NoPicture>, Vec<(String, Duration)>);

/// Runs a load over the given answers, each taking `each` on a fake clock: what came back, and each address asked with
/// the time it was given.
fn run(each: Duration, answers: Vec<Hop>) -> Loaded {
    let start = Instant::now();
    let elapsed = Cell::new(Duration::ZERO);
    let asked = RefCell::new(Vec::new());
    let mut answers = answers.into_iter();
    let result = load(
        FIRST,
        || start + elapsed.get(),
        || true,
        |url, left| {
            asked.borrow_mut().push((url.to_string(), left));
            elapsed.set(elapsed.get() + each);
            Ok(answers.next().expect("asked more often than answered"))
        },
    );
    (result, asked.into_inner())
}
#[test]
fn a_persons_page_sends_the_load_on_to_their_picture() {
    let (result, asked) = run(
        Duration::from_secs(1),
        vec![
            redirect("https://avatars.githubusercontent.com/u/583231?size=40&v=4"),
            picture(),
        ],
    );
    assert_eq!(result, Ok(PNG.to_vec()));
    assert_eq!(
        asked,
        vec![
            (FIRST.to_owned(), Duration::from_secs(5)),
            (
                "https://avatars.githubusercontent.com/u/583231?size=40&v=4".to_owned(),
                Duration::from_secs(4)
            ),
        ]
    );
}
#[test]
fn the_time_limit_covers_every_redirect_together() {
    let (result, asked) = run(
        Duration::from_secs(3),
        vec![
            redirect("https://avatars.githubusercontent.com/u/1"),
            picture(),
        ],
    );
    assert_eq!(result, Err(NoPicture::OutOfTime));
    assert_eq!(asked[1].1, Duration::from_secs(2));
}
#[test]
fn a_redirect_elsewhere_is_refused_without_being_asked() {
    for off_the_list in [
        "https://evil.example/x.png",
        "http://avatars.githubusercontent.com/u/1",
        "//evil.example/x.png",
        "file:///etc/passwd",
        "/login",
    ] {
        let (result, asked) = run(Duration::ZERO, vec![redirect(off_the_list)]);
        assert_eq!(result, Err(NoPicture::NotAllowed), "{off_the_list}");
        assert_eq!(asked.len(), 1, "{off_the_list}");
    }
}
#[test]
fn a_relative_redirect_is_checked_where_it_lands() {
    let (result, asked) = run(
        Duration::ZERO,
        vec![redirect("/Octocat.png?size=40"), picture()],
    );
    assert_eq!(result, Ok(PNG.to_vec()));
    assert_eq!(asked[1].0, "https://github.com/Octocat.png?size=40");
}
#[test]
fn a_load_stops_after_three_asks() {
    let around = "https://github.com/octocat.png?size=40";
    let (result, asked) = run(
        Duration::ZERO,
        vec![redirect(around), redirect(around), redirect(around)],
    );
    assert_eq!(result, Err(NoPicture::TooManyRedirects));
    assert_eq!(asked.len(), 3);
}
#[test]
fn an_answer_that_is_no_picture_fails_the_load() {
    let svg = Hop::Answer {
        content_type: "image/svg+xml".to_owned(),
        bytes: b"<svg></svg>".to_vec(),
    };
    assert_eq!(
        run(Duration::ZERO, vec![svg]).0,
        Err(NoPicture::NotAPicture)
    );
}
#[test]
fn anything_but_a_person_picture_never_reaches_the_network() {
    let asked = Cell::new(0);
    let result = load(
        "https://example.com/tracker.png",
        Instant::now,
        || true,
        |_, _| {
            asked.set(asked.get() + 1);
            Ok(picture())
        },
    );
    assert_eq!(result, Err(NoPicture::NotAllowed));
    assert_eq!(asked.get(), 0);
}
#[test]
fn a_screen_gone_between_redirects_stops_the_asking() {
    let wanted = Cell::new(true);
    let asked = Cell::new(0);
    let result = load(
        FIRST,
        Instant::now,
        || wanted.get(),
        |_, _| {
            asked.set(asked.get() + 1);
            wanted.set(false);
            Ok(redirect("https://avatars.githubusercontent.com/u/1"))
        },
    );
    assert_eq!(result, Err(NoPicture::NoLongerWanted));
    assert_eq!(asked.get(), 1);
}
