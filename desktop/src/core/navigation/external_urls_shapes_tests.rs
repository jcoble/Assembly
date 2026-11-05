//! Spec sections 1, 5 and 7: exactly which links are accepted, and the link a session row copies.

use super::external_urls::*;

fn session(id: &str, machine: Option<&str>) -> ExternalRoute {
    ExternalRoute::Session {
        id: id.into(),
        machine: machine.map(String::from),
    }
}

fn file(path: &str, machine: Option<&str>) -> ExternalRoute {
    ExternalRoute::File {
        path: path.into(),
        machine: machine.map(String::from),
    }
}

#[test]
fn a_session_link_names_its_session_and_maybe_its_machine() {
    assert_eq!(
        parse_external("assembly://session/owned-123"),
        session("owned-123", None)
    );
    assert_eq!(
        parse_external("assembly://session/a%20b"),
        session("a b", None)
    );
    assert_eq!(
        parse_external("assembly://session/owned-123?profile=Workbox%20Test"),
        session("owned-123", Some("Workbox Test"))
    );
}

#[test]
fn an_open_link_carries_a_web_page() {
    let open = ExternalRoute::Open {
        url: "https://github.com/o/r/pull/3".into(),
    };
    let encoded = "https%3A%2F%2Fgithub.com%2Fo%2Fr%2Fpull%2F3";
    assert_eq!(
        parse_external(&format!("assembly://open?url={encoded}")),
        open
    );
    assert_eq!(
        parse_external(&format!("assembly://open/?url={encoded}")),
        open
    );
    assert_eq!(
        parse_external("assembly://open?url=http%3A%2F%2Fa.test%2F"),
        ExternalRoute::Open {
            url: "http://a.test/".into()
        }
    );
}

#[test]
fn a_file_link_carries_an_absolute_path_and_maybe_its_machine() {
    assert_eq!(
        parse_external("assembly://file?path=%2Fwork%2Fa%20b.cs"),
        file("/work/a b.cs", None)
    );
    assert_eq!(
        parse_external("assembly://file/?path=%2Fwork%2Fa.cs"),
        file("/work/a.cs", None)
    );
    assert_eq!(
        parse_external("assembly://file?path=%2Fwork%2Fa.cs&profile=box"),
        file("/work/a.cs", Some("box"))
    );
    assert_eq!(
        parse_external("assembly://file?profile=box&path=%2Fwork%2Fa.cs"),
        file("/work/a.cs", Some("box"))
    );
}

#[test]
fn every_other_shape_is_unsupported() {
    for url in [
        // The old app's refusals.
        "assembly://session/",
        "assembly://session/a/b",
        "assembly://open?url=javascript%3Aalert(1)",
        "assembly://file?path=relative.cs",
        "assembly://settings",
        "https://session/owned-123",
        // Session.
        "assembly://session",
        "assembly://session/a%2Fb",
        "assembly://session/a/",
        "assembly://session/a?x=1",
        "assembly://session/a?profile=",
        "assembly://session/a?profile=b&profile=c",
        "assembly://session/a?profile=b&x=1",
        "assembly://session/a#f",
        "assembly://user:pw@session/a",
        "assembly://user@session/a",
        "assembly://session/a b",
        "assembly://session/a\tb",
        "assembly://session/a%0Ab",
        "assembly://session/a?profile=b%00",
        // Open.
        "assembly://open",
        "assembly://open?url=",
        "assembly://open?url=https%3A%2F%2Fa.test&url=https%3A%2F%2Fb.test",
        "assembly://open?url=https%3A%2F%2Fa.test&x=1",
        "assembly://open/x?url=https%3A%2F%2Fa.test",
        "assembly://open?url=https%3A%2F%2Fa.test%2Fb%20c",
        "assembly://open?url=https%3A%2F%2Fa.test#f",
        "assembly://open?url=assembly%3A%2F%2Fopen",
        "assembly://open?url=file%3A%2F%2F%2Fetc%2Fpasswd",
        "assembly://user:pw@open?url=https%3A%2F%2Fa.test",
        // File.
        "assembly://file",
        "assembly://file?path=",
        "assembly://file?path=%2Fa&path=%2Fb",
        "assembly://file?path=%2Fa%2F..%2Fb",
        "assembly://file?path=%2Fa%2F.%2Fb",
        "assembly://file?path=%2Fa&x=1",
        "assembly://file?path=%2Fa&profile=",
        "assembly://file?path=%2Fa&profile=b&profile=c",
        "assembly://file/x?path=%2Fa",
        "assembly://file?path=%2Fa%00b",
        "assembly://file?path=%2Fa#f",
        "assembly://user:pw@file?path=%2Fa",
        "assembly://file?path=/a b",
    ] {
        assert_eq!(parse_external(url), ExternalRoute::Unsupported, "{url}");
    }
}

#[test]
fn the_system_can_hand_over_a_file_url_and_a_link_can_not_be_one() {
    assert_eq!(
        parse_incoming("file:///work/a%20b.cs"),
        file("/work/a b.cs", None)
    );
    assert_eq!(
        parse_external("file:///work/a.cs"),
        ExternalRoute::Unsupported
    );
    for url in [
        "file://host/work/a.cs",
        "file:///work/a.cs?x=1",
        "file:///work/a.cs#f",
        "file:relative.cs",
        "https://a.test",
        "",
    ] {
        assert_eq!(parse_incoming(url), ExternalRoute::Unsupported, "{url}");
    }
}

#[test]
fn the_system_hands_over_assembly_links_as_they_are() {
    assert_eq!(
        parse_incoming("assembly://session/a?profile=box"),
        session("a", Some("box"))
    );
    assert_eq!(
        parse_incoming("assembly://task/3f4394b0689d81b19819c696dad2fd48"),
        parse_external("assembly://task/3f4394b0689d81b19819c696dad2fd48")
    );
}

#[test]
fn a_session_row_links_to_its_session_and_a_remote_one_names_its_machine() {
    assert_eq!(session_link("s1", None), "assembly://session/s1");
    assert_eq!(
        session_link("s1", Some("box")),
        "assembly://session/s1?profile=box"
    );
}

#[test]
fn a_copied_link_parses_back_to_the_same_session_and_machine() {
    for (id, machine) in [
        ("s1", None),
        ("a b?c#d%e", None),
        ("s1", Some("Workbox Test")),
        ("a b?c#d%e", Some("Box & Co/1+2=3?#%")),
    ] {
        let link = session_link(id, machine);
        assert!(!link.contains(' '), "{link}");
        assert_eq!(parse_external(&link), session(id, machine), "{link}");
    }
}
