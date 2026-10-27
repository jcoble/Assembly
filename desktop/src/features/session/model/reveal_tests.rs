//! The smooth reveal of a streaming reply, ported from app/test/heights.test.ts.

use super::reveal::{MAX_LAG_MS, MIN_CPS, Reveal};
use crate::features::session::rows::markdown::{Block, Inline, ListItem};

fn text(value: &str) -> Inline {
    Inline::Text {
        value: value.into(),
    }
}

// Pins: g0-session#4
#[test]
fn the_reveal_keeps_a_steady_pace_speeds_up_when_behind_and_stops_at_the_text() {
    let frame = 1000.0 / 60.0;
    let small = Reveal::step(0, 5, frame);
    assert!(
        small >= 1 && small <= (MIN_CPS * frame / 1000.0).ceil() as usize + 1,
        "{small}"
    );
    // Bigger buffers move faster, and never take more than MAX_LAG_MS to drain at the current pace.
    let mut previous = 0;
    for backlog in [10, 100, 1_000, 50_000] {
        let step = Reveal::step(0, backlog, frame);
        assert!(step >= previous, "{backlog}: {step} < {previous}");
        assert!(
            backlog as f64 / step as f64 * frame <= MAX_LAG_MS + frame,
            "{backlog} falls behind"
        );
        previous = step;
    }
    // Never past the text, a long gap catches up at once, and a caught-up reveal holds.
    assert_eq!(Reveal::step(98, 100, 1_000.0), 100);
    assert_eq!(Reveal::step(0, 400, MAX_LAG_MS), 400);
    assert_eq!(Reveal::step(100, 100, frame), 100);
    assert_eq!(Reveal::step(120, 100, frame), 100);
    let (mut shown, mut frames) = (0, 0);
    while shown < 2_000 && frames < 1_000 {
        shown = Reveal::step(shown, 2_000, frame);
        frames += 1;
    }
    assert_eq!(shown, 2_000);
}

// Pins: g0-session#4
#[test]
fn clipping_shows_the_first_characters_of_the_blocks_keeping_whole_blocks_as_they_are() {
    let item = |n: usize| ListItem {
        task: false,
        checked: false,
        parts: vec![text(&format!("item {n}"))],
        blocks: vec![],
    };
    let blocks = vec![
        Block::Paragraph {
            parts: vec![
                text("Hello "),
                Inline::Strong {
                    parts: vec![text("wörld")],
                },
            ],
        },
        Block::List {
            ordered: false,
            items: (1..=3).map(item).collect(),
        },
        Block::Code {
            language: "ts".into(),
            value: "const x = 1;".into(),
            complete: true,
        },
        Block::Table {
            headers: vec![vec![text("a")], vec![text("b")]],
            rows: vec![vec![vec![text("cc")], vec![text("dd")]]],
        },
    ];
    let total = Reveal::chars(&blocks);
    assert_eq!(total, 11 + 18 + 12 + 6);
    for n in 0..=total {
        assert_eq!(Reveal::chars(&Reveal::clip(&blocks, n)), n, "at {n}");
    }
    assert_eq!(Reveal::clip(&blocks, total), blocks);
    assert!(Reveal::clip(&blocks, 0).is_empty());
    let code = Reveal::clip(&blocks, 11 + 18 + 10).pop().unwrap();
    assert!(matches!(code, Block::Code { value, .. } if value == "const x = "));
}
