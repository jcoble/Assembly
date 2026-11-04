//! One open row per list. The row grows with the kit's own motion, the spring and reveal its Accordion uses: the body
//! measures itself, the rows below slide down, and a closing body stays until the spring settles.
//! The body is at least one row tall and, unless its caller says otherwise, at most `DEFAULT_MAX_ROWS` rows, scrolling
//! inside past that, so an open row never takes over the list. The rail and the Build Board share this.
use std::cell::Cell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::base::motion::{MotionReveal, spring};
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The most rows an open body takes before it scrolls inside, unless its caller sets `max_rows`.
const DEFAULT_MAX_ROWS: f32 = 3.;

#[derive(Clone, Debug, PartialEq)]
pub enum RowDropDownEvent {
    Opened(SharedString),
    Shut(SharedString),
    /// The row's height changed, by its motion or by its open body changing, for a list that measures its rows.
    Moved(SharedString),
}

type BuildBody = Rc<dyn Fn(&mut Window, &mut App) -> AnyElement>;

struct HeldBody {
    build: BuildBody,
    /// Whether it has been drawn, so a close that nothing could see drops it at once.
    drawn: Rc<Cell<bool>>,
    /// The body's height when last drawn, so an open body that changes tells its list.
    height: Rc<Cell<Pixels>>,
    /// The motion's value when last drawn, so the list hears each step, the last included, and nothing at rest.
    progress: Rc<Cell<f32>>,
}

#[derive(Default)]
pub struct RowDropDowns {
    open: Option<SharedString>,
    bodies: HashMap<SharedString, HeldBody>,
}

impl EventEmitter<RowDropDownEvent> for RowDropDowns {}

impl RowDropDowns {
    pub fn open_key(&self) -> Option<&SharedString> {
        self.open.as_ref()
    }

    pub fn is_open(&self, key: &SharedString) -> bool {
        self.open.as_ref() == Some(key)
    }

    /// Open, or still closing.
    pub fn is_showing(&self, key: &SharedString) -> bool {
        self.bodies.contains_key(key)
    }

    pub fn toggle(
        &mut self,
        key: SharedString,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
        cx: &mut Context<Self>,
    ) {
        if self.is_open(&key) {
            self.shut(cx);
            return;
        }
        self.shut(cx);
        // Reopening during a close keeps its place: the spring turns around from where it is.
        let (drawn, progress) = self
            .bodies
            .get(&key)
            .map(|held| (held.drawn.clone(), held.progress.clone()))
            .unwrap_or_default();
        self.bodies.insert(
            key.clone(),
            HeldBody {
                build: Rc::new(body),
                drawn,
                height: Rc::new(Cell::new(px(0.))),
                progress,
            },
        );
        self.open = Some(key.clone());
        cx.emit(RowDropDownEvent::Opened(key));
        cx.notify();
    }

    /// Virtual lists must release bodies removed by filtering or paging, since those rows
    /// no longer render the closing motion that would normally release their resources.
    pub fn retain_keys(
        &mut self,
        mut keep: impl FnMut(&SharedString) -> bool,
        cx: &mut Context<Self>,
    ) {
        let removed = self.open.as_ref().filter(|key| !keep(key)).cloned();
        let before = self.bodies.len();
        self.bodies.retain(|key, _| keep(key));
        let changed = removed.is_some() || before != self.bodies.len();
        if let Some(key) = removed {
            self.open = None;
            cx.emit(RowDropDownEvent::Shut(key));
        }
        if changed {
            cx.notify();
        }
    }

    pub fn shut(&mut self, cx: &mut Context<Self>) {
        let Some(key) = self.open.take() else {
            return;
        };
        if self.bodies.get(&key).is_some_and(|held| !held.drawn.get()) {
            self.bodies.remove(&key);
        }
        cx.emit(RowDropDownEvent::Shut(key));
        cx.notify();
    }

    /// The owner supplies the focused row and its actual body; unrelated keys stay theirs.
    pub fn handle_key(
        &mut self,
        key: SharedString,
        code: &str,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
        cx: &mut Context<Self>,
    ) -> bool {
        match code {
            "right" => {
                if !self.is_open(&key) {
                    self.toggle(key, body, cx);
                }
                true
            }
            "left" => {
                if self.is_open(&key) {
                    self.shut(cx);
                }
                true
            }
            "escape" => {
                self.shut(cx);
                true
            }
            _ => false,
        }
    }
}

/// The part of a row that grows, drawn under it for every row: a shut row draws nothing, but its spring has to exist
/// already, because a spring's first value is taken at once and the row wouldn't animate open.
#[derive(IntoElement)]
pub struct GrowingBody {
    key: SharedString,
    drops: Entity<RowDropDowns>,
    row_height: Pixels,
    max_rows: Option<f32>,
}

impl GrowingBody {
    pub fn new(key: SharedString, drops: Entity<RowDropDowns>, row_height: Pixels) -> Self {
        GrowingBody {
            key,
            drops,
            row_height,
            max_rows: Some(DEFAULT_MAX_ROWS),
        }
    }

    /// The most rows the body takes before it scrolls inside; `None` lets it be as tall as it needs.
    pub fn max_rows(mut self, rows: Option<f32>) -> Self {
        self.max_rows = rows;
        self
    }
}

impl RenderOnce for GrowingBody {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let drops = self.drops.read(cx);
        let open = drops.is_open(&self.key);
        let held = drops.bodies.get(&self.key).map(|held| {
            (held.build.clone(), held.drawn.clone(), held.height.clone(), held.progress.clone())
        });
        let target = if open { 1. } else { 0. };
        let progress = spring(
            ElementId::Name(self.key.clone()),
            target,
            cx.theme().motion_tokens().spring_control,
            window,
            cx,
        );
        let settled_shut = progress == 0. && !open;
        if let Some((_, _, _, last)) = &held {
            let moved = last.replace(progress) != progress;
            if settled_shut || moved {
                let (handle, key) = (self.drops.clone(), self.key.clone());
                cx.defer(move |cx| {
                    handle.update(cx, |drops, cx| {
                        if settled_shut {
                            drops.bodies.remove(&key);
                            cx.notify();
                        }
                        cx.emit(RowDropDownEvent::Moved(key));
                    });
                });
            }
        }
        let Some((build, drawn, height, _)) = held.filter(|_| !settled_shut) else {
            return Empty.into_any_element();
        };
        drawn.set(true);
        let (handle, key) = (self.drops.clone(), self.key.clone());
        let row_height = self.row_height;
        let body = div()
            // Open and at rest nothing else would say so: a list keeps a row's old height until it's told.
            .on_children_prepainted(move |children, _, cx| {
                let now = children.first().map_or(px(0.), |child| child.size.height);
                if height.replace(now) != now {
                    let (handle, key) = (handle.clone(), key.clone());
                    cx.defer(move |cx| {
                        handle.update(cx, |_, cx| cx.emit(RowDropDownEvent::Moved(key)));
                    });
                }
            })
            .id(ElementId::NamedChild(
                Arc::new(ElementId::Name(self.key.clone())),
                "row-body".into(),
            ))
            .w_full()
            .min_h(row_height)
            .when_some(self.max_rows, |body, rows| body.max_h(row_height * rows))
            .overflow_y_scroll()
            .child(build(window, cx))
            .into_any_element();
        MotionReveal::new(
            ElementId::NamedChild(
                Arc::new(ElementId::Name(self.key.clone())),
                "row-reveal".into(),
            ),
            progress,
            body,
        )
        .into_any_element()
    }
}
