use gpui_kit::component::diff::{DiffLinePosition, DiffSide};
use serde::{Deserialize, Serialize};
use uuid::Uuid;
#[derive(Clone, Debug, PartialEq)]
pub struct ReviewIdentity {
    pub repository: String,
    pub number: u64,
    pub head: String,
    pub base: String,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct LineComment {
    pub path: String,
    pub line: usize,
    pub side: String,
    pub body: String,
}
#[derive(Clone, Debug, PartialEq)]
pub struct DraftComment {
    pub id: String,
    pub target: LineComment,
}
pub struct ReviewDraft {
    pub identity: ReviewIdentity,
    pub body: String,
    pub comments: Vec<DraftComment>,
}
impl ReviewDraft {
    pub fn new(identity: ReviewIdentity) -> Self {
        Self {
            identity,
            body: String::new(),
            comments: Vec::new(),
        }
    }
    pub fn start_comment(&mut self, position: &DiffLinePosition) -> Result<String, String> {
        if self.comments.len() >= 20 {
            return Err("A review can contain at most 20 line comments.".into());
        }
        let id = Uuid::new_v4().to_string();
        self.comments.push(DraftComment {
            id: id.clone(),
            target: LineComment {
                path: position.path().to_string(),
                line: position.line(),
                side: match position.side() {
                    DiffSide::Original => "LEFT",
                    DiffSide::Modified => "RIGHT",
                }
                .into(),
                body: String::new(),
            },
        });
        Ok(id)
    }
    pub fn update_comment(&mut self, id: &str, body: String) {
        if let Some(comment) = self.comments.iter_mut().find(|comment| comment.id == id) {
            comment.target.body = body;
        }
    }
    pub fn remove_comment(&mut self, id: &str) {
        self.comments.retain(|comment| comment.id != id);
    }
    pub fn can_post(&self, current: &ReviewIdentity) -> bool {
        !self.body.trim().is_empty() && self.body.len() <= 20_000 && self.can_post_lines(current)
    }
    /// The line comments are fit to post on this head, whatever the overall comment says.
    pub fn can_post_lines(&self, current: &ReviewIdentity) -> bool {
        self.identity == *current
            && self.comments.len() <= 20
            && self.comments.iter().all(|comment| {
                !comment.target.body.trim().is_empty() && comment.target.body.len() <= 10_000
            })
    }
}
#[cfg(test)]
#[path = "review_draft_tests.rs"]
mod tests;
