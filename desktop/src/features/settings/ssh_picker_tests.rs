use super::{HostChoice, SshTarget, filled_fields};
use serde_json::json;

fn target() -> SshTarget {
    serde_json::from_value(json!({"host":"box","user":"me","hostName":"box.example","port":2222}))
        .unwrap()
}

#[test]
fn a_host_shows_its_real_name_beside_it_only_when_that_says_something() {
    let choice = |host: &str, name: Option<&str>| HostChoice {
        host: host.into(),
        host_name: name.map(Into::into),
    };
    assert_eq!(
        choice("box", Some("box.example")).words(),
        "box · box.example"
    );
    assert_eq!(choice("box", Some("box")).words(), "box");
    assert_eq!(choice("seen.example", None).words(), "seen.example");
}

#[test]
fn a_pick_fills_the_destination_and_takes_the_alias_as_a_name_only_when_none_was_typed() {
    assert_eq!(filled_fields(&target(), ""), ("box".into(), "box".into()));
    assert_eq!(filled_fields(&target(), "  "), ("box".into(), "box".into()));
    assert_eq!(
        filled_fields(&target(), "mine"),
        ("mine".into(), "box".into())
    );
}

#[test]
fn what_ssh_said_reads_plainly() {
    assert_eq!(
        target().words(),
        "SSH connects as me@box.example, SSH port 2222"
    );
}
