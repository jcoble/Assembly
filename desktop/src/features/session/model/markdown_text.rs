//! A reply's blocks back as markdown, for its copy button: the rows carry only blocks. Ported from session/fold.ts's
//! markdownOf.

use crate::features::session::rows::markdown::{Block, Inline};

pub fn markdown_of(blocks: &[Block]) -> String {
    blocks
        .iter()
        .map(|block| block_text(block, ""))
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn block_text(block: &Block, indent: &str) -> String {
    match block {
        Block::Paragraph { parts } => format!("{indent}{}", inline(parts)),
        Block::Heading { level, parts } => {
            format!("{indent}{} {}", "#".repeat(*level as usize), inline(parts))
        }
        Block::Code {
            language, value, ..
        } => {
            let value = value.strip_suffix('\n').unwrap_or(value);
            format!("{indent}```{language}\n{value}\n{indent}```")
        }
        Block::Quote { blocks } => {
            let quoted = markdown_of(blocks);
            quoted
                .split('\n')
                .map(|line| format!("{indent}> {line}"))
                .collect::<Vec<_>>()
                .join("\n")
        }
        Block::List { ordered, items } => items
            .iter()
            .enumerate()
            .map(|(i, item)| {
                let mark = if *ordered {
                    format!("{}.", i + 1)
                } else {
                    "-".to_string()
                };
                let task = match (item.task, item.checked) {
                    (false, _) => "",
                    (true, true) => "[x] ",
                    (true, false) => "[ ] ",
                };
                let nested = format!("{indent}  ");
                let rest: String = item
                    .blocks
                    .iter()
                    .map(|block| format!("\n{}", block_text(block, &nested)))
                    .collect();
                format!("{indent}{mark} {task}{}{rest}", inline(&item.parts))
            })
            .collect::<Vec<_>>()
            .join("\n"),
        Block::Table { headers, rows } => {
            let row = |cells: &[Vec<Inline>]| {
                format!(
                    "| {} |",
                    cells
                        .iter()
                        .map(|cell| inline(cell))
                        .collect::<Vec<_>>()
                        .join(" | ")
                )
            };
            let rule = format!("|{}", " --- |".repeat(headers.len()));
            std::iter::once(row(headers))
                .chain(std::iter::once(rule))
                .chain(rows.iter().map(|cells| row(cells)))
                .map(|line| format!("{indent}{line}"))
                .collect::<Vec<_>>()
                .join("\n")
        }
        Block::Rule => format!("{indent}---"),
    }
}

fn inline(parts: &[Inline]) -> String {
    parts
        .iter()
        .map(|part| match part {
            Inline::Text { value } => value.clone(),
            Inline::Code { value } => format!("`{value}`"),
            Inline::Strong { parts } => format!("**{}**", inline(parts)),
            Inline::Emphasis { parts } => format!("*{}*", inline(parts)),
            Inline::Strike { parts } => format!("~~{}~~", inline(parts)),
            Inline::Link { href, parts } => format!("[{}]({href})", inline(parts)),
            Inline::FileLink { path, parts } => format!("[{}]({path})", inline(parts)),
        })
        .collect()
}
