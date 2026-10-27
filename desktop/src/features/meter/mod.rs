//! The lag meter, on only with `ASSEMBLY_METER=<csv path>`. That variable is the soak switch too: a soak is a long run
//! with the meter on, driven from outside the window (implementation-plan.md, the soak script). It has no timer and no
//! thread of its own: frame lag, draw time and stalls come from GPUI's frame trace, key-to-screen from each key's time
//! to the next presented frame, and it reads the trace and the footprint on the backend clock's `second` tick,
//! subscribed only while it's on. When it's off nothing of it runs. Its columns are the spike meter's (spike/gpui
//! src/meter.rs) with `footprint_mb` added at the end, so numbers compare with the spike baseline column for column,
//! except the stall columns, which measure something else (see meter_line.rs).

pub mod footprint;
pub mod meter;
pub mod meter_line;
