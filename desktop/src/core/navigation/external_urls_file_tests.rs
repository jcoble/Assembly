//! Spec 7 of the Terminal: a `file://` URL, whatever host it names, becomes the app's own file link, through feature
//! 28's `file:` parsing.

use super::external_urls::*;

const MAIN: &str = "assembly://file?path=%2Fwork%2Fa%2Fsrc%2Fmain.rs";

#[test]
fn a_file_link_with_any_host_builds_an_app_link() {
    for host in ["", "localhost", "my-mac.local", "box", "user@box:22"] {
        let url = format!("file://{host}/work/a/src/main.rs");
        assert_eq!(file_url_address(&url, None).as_deref(), Some(MAIN), "{url}");
        assert_eq!(
            file_url_address(&url, Some("Workbox Test")).as_deref(),
            Some(&*format!("{MAIN}&profile=Workbox%20Test")),
            "{url}"
        );
    }
    assert_eq!(file_address("/work/a/src/main.rs", None), MAIN);
    assert_eq!(
        file_address("/work/a/src/main.rs", Some("Workbox Test")),
        format!("{MAIN}&profile=Workbox%20Test")
    );
}

#[test]
fn an_encoded_path_stays_one_path() {
    assert_eq!(
        file_url_address("file://box/work/a/my%20file.rs", None).as_deref(),
        Some("assembly://file?path=%2Fwork%2Fa%2Fmy%20file.rs")
    );
    assert_eq!(
        file_url_address("file://box/work/a/b+c&d=e.rs", None).as_deref(),
        Some("assembly://file?path=%2Fwork%2Fa%2Fb%2Bc%26d%3De.rs")
    );
    // An encoded slash would add a component.
    assert_eq!(file_url_address("file://box/work/a%2Fb.rs", None), None);
}

#[test]
fn a_query_or_dot_segment_gives_none() {
    for url in [
        "file://box/work/a/main.rs?line=3",
        "file://box/work/a/main.rs#top",
        "file://box/work/../etc/passwd",
        "file://box/work/./a/main.rs",
        "file://box/work/%2E%2E/etc/passwd",
        "file://box/work//a/main.rs",
        "file://box/work/a main.rs",
        "file://box/work/a\\main.rs",
        "file://box",
        "file://box/",
        "file:/work/a/main.rs",
        "https://box/work/a/main.rs",
        "assembly://file?path=%2Fwork",
    ] {
        assert_eq!(file_url_address(url, None), None, "{url}");
        assert_eq!(file_url_address(url, Some("Workbox Test")), None, "{url}");
    }
}

#[test]
fn it_round_trips_through_parse_external() {
    for (url, path) in [
        ("file://box/work/a/src/main.rs", "/work/a/src/main.rs"),
        ("file:///work/a/my%20file.rs", "/work/a/my file.rs"),
        ("file://box/work/a/b+c&d=e.rs", "/work/a/b+c&d=e.rs"),
        ("file://box/work/%C3%A9t%C3%A9.rs", "/work/été.rs"),
    ] {
        for machine in [None, Some("Workbox Test"), Some("a&b=c d")] {
            let address = file_url_address(url, machine).expect(url);
            assert_eq!(
                parse_external(&address),
                ExternalRoute::File {
                    path: path.into(),
                    machine: machine.map(String::from),
                },
                "{url} on {machine:?}"
            );
            assert_eq!(address, file_address(path, machine), "{url} on {machine:?}");
        }
    }
}
