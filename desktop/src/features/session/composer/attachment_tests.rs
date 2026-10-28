//! The draft's image references and dropped paths, ported from app/test/attachments.test.ts. G0 writes no thumbnail.

use super::attachment::{Attachment, AttachmentState};

const SHOT: &str = "/home/u/spike-sandboxes/attachments/t1/a.png";
const PHOTO: &str = "/home/u/spike-sandboxes/attachments/t1/b.jpg";

fn saved(path: &str, name: &str) -> Attachment {
    Attachment {
        id: 0,
        name: name.to_string().into(),
        size: 1,
        state: AttachmentState::Saved { path: path.into() },
    }
}

fn named(path: &str, name: &str, thumbnail: Option<&str>) -> (String, String, Option<String>) {
    (path.into(), name.into(), thumbnail.map(Into::into))
}

// Pins: g0-session#24
#[test]
fn the_sent_message_carries_one_reference_line_per_attachment_after_the_text() {
    assert_eq!(
        Attachment::with_lines(
            "look at these",
            &[
                saved(SHOT, "Screen Shot (2).png"),
                saved(PHOTO, "photo.jpg")
            ]
        ),
        format!(
            "look at these\n\nAttached image: {SHOT} (Screen Shot (2).png)\nAttached image: {PHOTO} (photo.jpg)"
        )
    );
}

// Pins: g0-session#24
#[test]
fn an_image_alone_still_sends_text_and_a_name_cannot_break_the_line() {
    assert_eq!(
        Attachment::with_lines("", &[saved(SHOT, "two\nlines.png")]),
        format!("Attached image: {SHOT} (two lines.png)")
    );
    assert_eq!(Attachment::with_lines("just text", &[]), "just text");
    assert_eq!(
        Attachment::with_lines("", &[saved(SHOT, "shot\r\n")]),
        format!("Attached image: {SHOT} (shot )")
    );
}

// Pins: g0-session#25
#[test]
fn the_history_parse_recovers_the_attachments_and_the_text_the_user_typed() {
    let sent = Attachment::with_lines(
        "look at these\nplease",
        &[
            saved(SHOT, "Screen Shot (2).png"),
            saved(PHOTO, "photo.jpg"),
        ],
    );
    assert_eq!(
        Attachment::split_lines(&sent),
        (
            "look at these\nplease".into(),
            vec![
                named(SHOT, "Screen Shot (2).png", None),
                named(PHOTO, "photo.jpg", None)
            ]
        )
    );
    let alone = Attachment::with_lines("", &[saved(PHOTO, "photo.jpg")]);
    assert_eq!(
        Attachment::split_lines(&alone),
        (String::new(), vec![named(PHOTO, "photo.jpg", None)])
    );
}

// Pins: g0-session#25
#[test]
fn text_without_trailing_references_is_left_alone() {
    let plain = "Attached image: /a.png (x.png)\nis how the composer writes it";
    assert_eq!(Attachment::split_lines(plain), (plain.into(), vec![]));
    assert_eq!(Attachment::split_lines("hello"), ("hello".into(), vec![]));
}

// Pins: g0-session#25
#[test]
fn a_thumbnail_path_the_old_app_wrote_comes_back_from_the_history_parse() {
    let thumb = "/home/u/spike-sandboxes/attachments/t1/c.webp";
    let content = format!(
        "see\n\nAttached image: {SHOT} (Screen Shot (2).png) thumbnail: {thumb}\nAttached image: {PHOTO} (photo.jpg)"
    );
    assert_eq!(
        Attachment::split_lines(&content),
        (
            "see".into(),
            vec![
                named(SHOT, "Screen Shot (2).png", Some(thumb)),
                named(PHOTO, "photo.jpg", None)
            ]
        )
    );
}

// Pins: g0-session#24
#[test]
fn sizes_read_like_the_composer_chip() {
    assert_eq!(Attachment::size_text(512), "512 B");
    assert_eq!(Attachment::size_text(2048), "2.0 KB");
    assert_eq!(
        Attachment::size_text(3 * 1024 * 1024 + 512 * 1024),
        "3.5 MB"
    );
}

// Pins: g0-session#24
#[test]
fn only_png_jpeg_gif_and_webp_up_to_20_mb_are_accepted() {
    assert_eq!(Attachment::rejection("image/png", 20 * 1024 * 1024), None);
    assert_eq!(Attachment::rejection("image/webp", 1), None);
    assert_eq!(
        Attachment::rejection("image/svg+xml", 1),
        Some("not a supported image")
    );
    assert_eq!(
        Attachment::rejection("application/pdf", 1),
        Some("not a supported image")
    );
    assert_eq!(
        Attachment::rejection("image/jpeg", 20 * 1024 * 1024 + 1),
        Some("larger than 20 MB")
    );
}

// Pins: g0-session#24
#[test]
fn dropped_paths_are_separated_by_spaces_a_path_with_a_space_in_double_quotes() {
    assert_eq!(
        Attachment::paths_text(&["/Users/a/notes.txt".into()]),
        "/Users/a/notes.txt"
    );
    assert_eq!(
        Attachment::paths_text(&["/Users/a/My Folder".into(), "/tmp/x.md".into()]),
        "\"/Users/a/My Folder\" /tmp/x.md"
    );
}

// Pins: g0-session#24
#[test]
fn paths_go_in_at_the_caret_with_a_space_either_side_only_where_needed() {
    assert_eq!(Attachment::insert_at("", 0, "/a"), ("/a".into(), 2));
    assert_eq!(
        Attachment::insert_at("look at", 7, "/a"),
        ("look at /a".into(), 10)
    );
    assert_eq!(
        Attachment::insert_at("look at  please", 8, "/a"),
        ("look at /a please".into(), 10)
    );
    assert_eq!(
        Attachment::insert_at("seeplease", 3, "/a"),
        ("see /a please".into(), 7)
    );
    // A caret past the end or inside a character goes to the nearest boundary before it.
    assert_eq!(Attachment::insert_at("ab", 5, "/a"), ("ab /a".into(), 5));
    assert_eq!(Attachment::insert_at("é", 1, "/a"), ("/a é".into(), 3));
}

// Pins: g0-session#24
#[test]
fn an_image_type_comes_from_its_extension() {
    assert_eq!(Attachment::mime_of("Shot.PNG"), Some("image/png"));
    assert_eq!(Attachment::mime_of("a.b.jpeg"), Some("image/jpeg"));
    assert_eq!(Attachment::mime_of("a.jpg"), Some("image/jpeg"));
    assert_eq!(Attachment::mime_of("notes.txt"), None);
    assert_eq!(Attachment::mime_of("png"), None);
}
