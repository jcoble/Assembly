use super::{LARGEST_PICTURE, is_picture, may_ask_first, may_follow, picture_url};
use url::Url;

fn url(text: &str) -> Url {
    Url::parse(text).unwrap()
}
const PNG: &[u8] = b"\x89PNG\r\n\x1a\nrest";

#[test]
fn a_login_becomes_the_old_screens_picture_address() {
    assert_eq!(
        picture_url("octocat").as_deref(),
        Some("https://github.com/octocat.png?size=40")
    );
    assert_eq!(
        picture_url("dependabot[bot]").as_deref(),
        Some("https://github.com/dependabot%5Bbot%5D.png?size=40")
    );
    assert_eq!(
        picture_url("a b").as_deref(),
        Some("https://github.com/a%20b.png?size=40")
    );
    assert_eq!(picture_url(""), None);
}
#[test]
fn whatever_a_login_holds_it_stays_one_picture_on_github() {
    for login in [
        "../../etc/passwd",
        "a/b",
        "x?y=1",
        "x#y",
        "@evil.example",
        "%2e%2e",
        ".",
    ] {
        let made = picture_url(login).unwrap();
        assert!(may_ask_first(&url(&made)), "{login} made {made}");
    }
}
#[test]
fn only_a_persons_github_picture_may_be_asked_first() {
    assert!(may_ask_first(&url(
        "https://github.com/octocat.png?size=40"
    )));
    for refused in [
        "http://github.com/octocat.png?size=40",
        "https://token@github.com/octocat.png?size=40",
        "https://a:b@github.com/octocat.png?size=40",
        "https://github.com:8443/octocat.png?size=40",
        "https://github.com.evil.example/octocat.png?size=40",
        "https://example.com/octocat.png?size=40",
        "https://avatars.githubusercontent.com/u/1?size=40",
        "https://github.com/a/octocat.png?size=40",
        "https://github.com/.png?size=40",
        "https://github.com/octocat.svg?size=40",
        "https://github.com/octocat.png",
        "https://github.com/octocat.png?size=40&token=x",
        "https://github.com/octocat.png?size=40#x",
        "file:///etc/passwd",
        "data:image/png;base64,AAAA",
    ] {
        assert!(!may_ask_first(&url(refused)), "{refused}");
    }
}
#[test]
fn a_redirect_may_land_only_on_github_or_its_avatar_host() {
    for allowed in [
        "https://avatars.githubusercontent.com/u/583231?size=40&v=4",
        "https://github.com/Octocat.png?size=40",
    ] {
        assert!(may_follow(&url(allowed)), "{allowed}");
    }
    for refused in [
        "http://avatars.githubusercontent.com/u/1",
        "https://user@avatars.githubusercontent.com/u/1",
        "https://avatars.githubusercontent.com:8443/u/1",
        "https://evil.example/u/1",
        "https://github.com/login",
        "file:///etc/passwd",
    ] {
        assert!(!may_follow(&url(refused)), "{refused}");
    }
}
#[test]
fn only_real_pictures_count() {
    assert!(is_picture("image/png", PNG));
    assert!(is_picture("IMAGE/PNG; charset=binary", PNG));
    assert!(is_picture("image/jpeg", &[0xff, 0xd8, 0xff, 0xe0]));
    assert!(is_picture("image/gif", b"GIF89a..."));
    assert!(is_picture("image/webp", b"RIFF\0\0\0\0WEBPVP8 "));
    assert!(!is_picture("image/svg+xml", b"<svg></svg>"));
    assert!(!is_picture("image/png", b"<svg></svg>"));
    assert!(!is_picture("text/html", PNG));
    assert!(!is_picture("", PNG));
    assert!(!is_picture("image/webp", b"RIFF"));
    let mut too_long = PNG.to_vec();
    too_long.resize(LARGEST_PICTURE as usize + 1, 0);
    assert!(!is_picture("image/png", &too_long));
}
