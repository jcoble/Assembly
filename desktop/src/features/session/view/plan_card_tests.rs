use super::current_step;
use crate::features::session::rows::plan::{PlanItem, PlanStatus};

fn plan(statuses: &[PlanStatus]) -> Vec<PlanItem> {
    statuses
        .iter()
        .map(|status| PlanItem {
            text: "step".into(),
            status: *status,
        })
        .collect()
}

// Pins: g0-session#18
#[test]
fn the_plan_marks_the_step_in_progress_else_the_next_pending_else_the_last() {
    use PlanStatus::*;
    assert_eq!(current_step(&plan(&[Completed, InProgress, Pending])), 2);
    assert_eq!(current_step(&plan(&[Completed, Pending, Pending])), 2);
    assert_eq!(current_step(&plan(&[Completed, Completed])), 2);
    assert_eq!(current_step(&plan(&[])), 0);
}
