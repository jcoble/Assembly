//! The lag meter, on only with `ASSEMBLY_METER=<csv path>`, like the spike's. That variable is the soak switch too: a
//! soak is a long run with the meter on, driven from outside the window (implementation-plan.md, the soak script).
//! When it's off nothing of it runs. Its columns are the spike meter's (spike/gpui src/meter.rs) with `footprint_mb`
//! added at the end, so numbers compare with the spike's baseline column for column.

pub mod footprint;
pub mod lag_meter;
pub mod meter_line;

use gpui_kit::App;

/// Starts the meter when `ASSEMBLY_METER` names a CSV file.
pub fn start_if_on(cx: &mut App) {
    if let Some(csv) = std::env::var_os("ASSEMBLY_METER").filter(|csv| !csv.is_empty()) {
        lag_meter::Meter::start(csv.into(), cx);
    }
}
