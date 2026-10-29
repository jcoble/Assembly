//! `MarkdownView` draws a reply's blocks as styled text runs, with links and file links clickable (the open URL and
//! open file intents, lane g0-host) and code blocks in the theme's mono font. It only lays out what the backend
//! parsed. Replaces chat/Text.svelte, chat/markdown/CodeBlock.svelte and the spike's session/markdown.rs.

use std::ops::Range;

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::intents::open_file::OpenFile;
use crate::core::intents::open_url::OpenUrl;
use crate::features::session::rows::markdown::{Block, Inline};

#[derive(IntoElement)]
pub struct MarkdownView {
    /// The item's key: its links' element ids start with it.
    pub id: SharedString,
    pub blocks: Vec<Block>,
    /// The session a file link's path is in.
    pub session: SharedString,
}

impl RenderOnce for MarkdownView {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let mut draw = Draw {
            id: &self.id,
            session: &self.session,
            next: 0,
            cx,
        };
        draw.blocks(&self.blocks)
    }
}

/// How a run of text is marked; nested parts add to their parent's marks.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Marks {
    pub bold: bool,
    pub italic: bool,
    pub strike: bool,
    pub code: bool,
    pub link: bool,
}

/// Where a clicked link goes.
#[derive(Clone, Debug, PartialEq)]
pub enum LinkTarget {
    Url(String),
    File(String),
}

/// One paragraph's parts as one string, the marked ranges in it, and each link's range and target.
#[derive(Debug, Default, PartialEq)]
pub struct Runs {
    pub text: String,
    pub marked: Vec<(Range<usize>, Marks)>,
    pub links: Vec<(Range<usize>, LinkTarget)>,
}

impl Runs {
    pub fn of(parts: &[Inline]) -> Runs {
        let mut runs = Runs::default();
        runs.add(parts, Marks::default());
        runs
    }

    fn add(&mut self, parts: &[Inline], marks: Marks) {
        for part in parts {
            match part {
                Inline::Text { value } => self.push(value, marks),
                Inline::Code { value } => self.push(
                    value,
                    Marks {
                        code: true,
                        ..marks
                    },
                ),
                Inline::Strong { parts } => self.add(
                    parts,
                    Marks {
                        bold: true,
                        ..marks
                    },
                ),
                Inline::Emphasis { parts } => self.add(
                    parts,
                    Marks {
                        italic: true,
                        ..marks
                    },
                ),
                Inline::Strike { parts } => self.add(
                    parts,
                    Marks {
                        strike: true,
                        ..marks
                    },
                ),
                Inline::Link { href, parts } => {
                    self.link(parts, marks, LinkTarget::Url(href.clone()))
                }
                Inline::FileLink { path, parts } => {
                    self.link(parts, marks, LinkTarget::File(path.clone()))
                }
            }
        }
    }

    fn link(&mut self, parts: &[Inline], marks: Marks, target: LinkTarget) {
        let start = self.text.len();
        self.add(
            parts,
            Marks {
                link: true,
                ..marks
            },
        );
        if self.text.len() > start {
            self.links.push((start..self.text.len(), target));
        }
    }

    fn push(&mut self, value: &str, marks: Marks) {
        let start = self.text.len();
        self.text.push_str(value);
        if marks != Marks::default() && !value.is_empty() {
            self.marked.push((start..self.text.len(), marks));
        }
    }
}

/// One reply's drawing: `next` numbers its clickable paragraphs, so each has its own element id.
struct Draw<'a> {
    id: &'a str,
    session: &'a str,
    next: usize,
    cx: &'a App,
}

impl Draw<'_> {
    fn blocks(&mut self, list: &[Block]) -> Div {
        let mut column = v_flex().gap_2();
        for block in list {
            column = column.child(self.block(block));
        }
        column
    }

    fn block(&mut self, block: &Block) -> AnyElement {
        let theme = self.cx.theme();
        match block {
            Block::Paragraph { parts } => self.text(parts),
            Block::Heading { level, parts } => div()
                .font_weight(FontWeight::SEMIBOLD)
                .text_size(match level {
                    1 => rems(1.25),
                    2 => rems(1.1),
                    _ => rems(1.0),
                })
                .child(self.text(parts))
                .into_any_element(),
            Block::Code { value, .. } => div()
                .p_2()
                .rounded(theme.radius)
                .bg(theme.muted)
                .font_family(theme.mono_font_family.clone())
                .text_xs()
                .overflow_x_hidden()
                .child(value.strip_suffix('\n').unwrap_or(value).to_string())
                .into_any_element(),
            Block::Quote { blocks } => {
                let (border, muted) = (theme.border, theme.muted_foreground);
                div()
                    .pl_3()
                    .border_l_2()
                    .border_color(border)
                    .text_color(muted)
                    .child(self.blocks(blocks))
                    .into_any_element()
            }
            Block::List { ordered, items } => {
                let muted = theme.muted_foreground;
                let mut list = v_flex().gap_1();
                for (at, item) in items.iter().enumerate() {
                    let marker = match (item.task, item.checked, ordered) {
                        (true, true, _) => "☑".to_string(),
                        (true, false, _) => "☐".to_string(),
                        (false, _, true) => format!("{}.", at + 1),
                        (false, _, false) => "•".to_string(),
                    };
                    let body = v_flex()
                        .flex_1()
                        .gap_1()
                        .child(self.text(&item.parts))
                        .child(self.blocks(&item.blocks));
                    list = list.child(
                        h_flex()
                            .items_start()
                            .gap_2()
                            .child(div().text_color(muted).child(marker))
                            .child(body),
                    );
                }
                list.into_any_element()
            }
            Block::Table { headers, rows } => {
                // One grid for the whole table, so every row shares the same columns and long cells wrap.
                let (border, radius) = (theme.border, theme.radius);
                let columns = rows
                    .iter()
                    .map(Vec::len)
                    .chain([headers.len()])
                    .max()
                    .unwrap_or(0);
                let mut table = div()
                    .grid()
                    .grid_cols(columns as u16)
                    .border_1()
                    .border_color(border)
                    .rounded(radius);
                let lines = (!headers.is_empty())
                    .then_some((headers, true))
                    .into_iter()
                    .chain(rows.iter().map(|row| (row, false)));
                for (cells, header) in lines {
                    for at in 0..columns {
                        let cell = cells.get(at).map(Vec::as_slice).unwrap_or_default();
                        table = table.child(self.table_cell(cell, header));
                    }
                }
                table.into_any_element()
            }
            Block::Rule => div().h(px(1.)).bg(theme.border).into_any_element(),
        }
    }

    /// A short row's missing cells come in empty, so the cells after them stay in their columns.
    fn table_cell(&mut self, cell: &[Inline], header: bool) -> Div {
        let muted = self.cx.theme().muted;
        div()
            .min_w_0()
            .px_2()
            .py_1()
            .when(header, |cell| {
                cell.font_weight(FontWeight::SEMIBOLD).bg(muted)
            })
            .child(self.text(cell))
    }

    /// One paragraph's text with its marks; its links open their URL or file when clicked.
    fn text(&mut self, parts: &[Inline]) -> AnyElement {
        let Runs {
            text,
            marked,
            links,
        } = Runs::of(parts);
        let theme = self.cx.theme();
        let highlights = marked
            .into_iter()
            .map(|(range, marks)| {
                let style = HighlightStyle {
                    font_weight: marks.bold.then_some(FontWeight::BOLD),
                    font_style: marks.italic.then_some(FontStyle::Italic),
                    strikethrough: marks.strike.then_some(StrikethroughStyle {
                        thickness: px(1.),
                        color: None,
                    }),
                    background_color: marks.code.then_some(theme.muted),
                    color: marks.link.then_some(theme.link),
                    ..HighlightStyle::default()
                };
                (range, style)
            })
            .collect::<Vec<_>>();
        let styled = StyledText::new(text).with_highlights(highlights);
        if links.is_empty() {
            return styled.into_any_element();
        }
        self.next += 1;
        let (ranges, targets): (Vec<_>, Vec<_>) = links.into_iter().unzip();
        let session = self.session.to_string();
        let id = ElementId::Name(format!("{}:{}", self.id, self.next).into());
        InteractiveText::new(id, styled)
            .on_click(ranges, move |at, window, cx| {
                let action: Box<dyn Action> = match &targets[at] {
                    LinkTarget::Url(url) => Box::new(OpenUrl { url: url.clone() }),
                    LinkTarget::File(path) => Box::new(OpenFile {
                        session_id: session.clone(),
                        path: path.clone(),
                        line: None,
                    }),
                };
                window.dispatch_action(action, cx);
            })
            .into_any_element()
    }
}

#[cfg(test)]
#[path = "markdown_view_tests.rs"]
mod tests;
