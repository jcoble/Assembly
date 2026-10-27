//! `Reveal` shows a streaming reply's newest text smoothly, like typing: one step per frame, from the characters
//! received but not yet shown. Display only: it cuts the backend's blocks short and never parses text. The session
//! pane steps it on each frame while it's behind and stops asking for frames once it has caught up. Ported from
//! rows/reveal.ts.

use crate::features::session::rows::markdown::{Block, Inline, ListItem};

/// The steady pace while little is waiting, in characters a second.
pub const MIN_CPS: f64 = 90.0;
/// The most the reveal is behind: what's waiting always shows within this, so the pace grows with what's waiting.
pub const MAX_LAG_MS: f64 = 250.0;

/// The streaming item being revealed, and how many of its characters show.
#[derive(Clone, Debug, PartialEq)]
pub struct Reveal {
    pub key: String,
    pub shown: usize,
}

impl Reveal {
    /// How many characters show after a frame `dt_ms` long, out of `target`.
    pub fn step(shown: usize, target: usize, dt_ms: f64) -> usize {
        if shown >= target {
            return target;
        }
        let waiting = (target - shown) as f64;
        let step = (MIN_CPS * dt_ms / 1000.0).max(waiting * dt_ms / MAX_LAG_MS);
        target.min(shown + (step.ceil() as usize).max(1))
    }

    /// The first `chars` characters of the blocks, keeping whole blocks as they are.
    pub fn clip(blocks: &[Block], chars: usize) -> Vec<Block> {
        let mut left = chars;
        let mut out = Vec::new();
        for block in blocks {
            let size = block_chars(block);
            if size <= left {
                out.push(block.clone());
                left -= size;
                continue;
            }
            if left > 0 {
                out.push(clip_block(block, left));
            }
            break;
        }
        out
    }

    /// The characters the blocks show.
    pub fn chars(blocks: &[Block]) -> usize {
        blocks.iter().map(block_chars).sum()
    }
}

fn part_chars(part: &Inline) -> usize {
    match part {
        Inline::Text { value } | Inline::Code { value } => value.chars().count(),
        Inline::Strong { parts }
        | Inline::Emphasis { parts }
        | Inline::Strike { parts }
        | Inline::Link { parts, .. }
        | Inline::FileLink { parts, .. } => parts_chars(parts),
    }
}

fn parts_chars(parts: &[Inline]) -> usize {
    parts.iter().map(part_chars).sum()
}

fn block_chars(block: &Block) -> usize {
    match block {
        Block::Paragraph { parts } | Block::Heading { parts, .. } => parts_chars(parts),
        Block::Code { value, .. } => value.chars().count(),
        Block::Quote { blocks } => Reveal::chars(blocks),
        Block::List { items, .. } => items
            .iter()
            .map(|item| parts_chars(&item.parts) + Reveal::chars(&item.blocks))
            .sum(),
        Block::Table { headers, rows } => headers
            .iter()
            .chain(rows.iter().flatten())
            .map(|cell| parts_chars(cell))
            .sum(),
        Block::Rule => 0,
    }
}

fn clip_parts(parts: &[Inline], chars: usize) -> Vec<Inline> {
    let mut left = chars;
    let mut out = Vec::new();
    for part in parts {
        let size = part_chars(part);
        if size <= left {
            out.push(part.clone());
            left -= size;
            continue;
        }
        if left > 0 {
            let mut part = part.clone();
            match &mut part {
                Inline::Text { value } | Inline::Code { value } => {
                    *value = value.chars().take(left).collect()
                }
                Inline::Strong { parts }
                | Inline::Emphasis { parts }
                | Inline::Strike { parts }
                | Inline::Link { parts, .. }
                | Inline::FileLink { parts, .. } => *parts = clip_parts(parts, left),
            }
            out.push(part);
        }
        break;
    }
    out
}

fn clip_block(block: &Block, chars: usize) -> Block {
    match block {
        Block::Paragraph { parts } => Block::Paragraph {
            parts: clip_parts(parts, chars),
        },
        Block::Heading { level, parts } => Block::Heading {
            level: *level,
            parts: clip_parts(parts, chars),
        },
        Block::Code {
            language,
            value,
            complete,
        } => Block::Code {
            language: language.clone(),
            value: value.chars().take(chars).collect(),
            complete: *complete,
        },
        Block::Quote { blocks } => Block::Quote {
            blocks: Reveal::clip(blocks, chars),
        },
        Block::List { ordered, items } => {
            let mut left = chars;
            let mut out = Vec::new();
            for item in items {
                let head = left.min(parts_chars(&item.parts));
                let blocks = Reveal::clip(&item.blocks, left - head);
                left -= head + Reveal::chars(&blocks);
                out.push(ListItem {
                    parts: clip_parts(&item.parts, head),
                    blocks,
                    ..item.clone()
                });
                if left == 0 {
                    break;
                }
            }
            Block::List {
                ordered: *ordered,
                items: out,
            }
        }
        Block::Table { headers, rows } => {
            // Cells in reading order, each cut to what's left.
            let mut left = chars;
            let cut = |cells: &[Vec<Inline>], left: &mut usize| -> Vec<Vec<Inline>> {
                cells
                    .iter()
                    .map(|cell| {
                        let parts = clip_parts(cell, *left);
                        *left -= parts_chars(&parts);
                        parts
                    })
                    .collect()
            };
            let headers = cut(headers, &mut left);
            let mut out = Vec::new();
            for row in rows {
                if left == 0 {
                    break;
                }
                out.push(cut(row, &mut left));
            }
            Block::Table { headers, rows: out }
        }
        Block::Rule => Block::Rule,
    }
}
