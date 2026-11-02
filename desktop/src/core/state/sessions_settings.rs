//! Session depth and child-limit preference keys and integer interpretation.

use std::num::IntErrorKind;

use serde_json::Value;

pub const DEPTH_KEY: &str = "sessions.depthLimit";

pub const CHILD_LIMIT: super::number_setting::NumberSetting =
    super::number_setting::NumberSetting {
        key: "sessions.childLimit",
        min: 1.,
        max: 10.,
        step: 1.,
        default: 5.,
        unit: "",
    };

pub fn depth(saved: Option<&Value>) -> i32 {
    read(saved, 1).max(1)
}

pub fn children(saved: Option<&Value>) -> i32 {
    read(saved, 5).clamp(1, 10)
}

pub fn parse_depth(text: &str) -> Result<i32, &'static str> {
    let value = text
        .trim()
        .parse::<i32>()
        .map_err(|error| match error.kind() {
            IntErrorKind::PosOverflow | IntErrorKind::NegOverflow => {
                "That number is too large to store."
            }
            _ => "Enter a whole number of at least 1.",
        })?;
    (value >= 1)
        .then_some(value)
        .ok_or("Enter a whole number of at least 1.")
}

fn read(saved: Option<&Value>, fallback: i32) -> i32 {
    let Some(value) = saved.and_then(Value::as_f64) else {
        return fallback;
    };
    if !value.is_finite()
        || value.fract() != 0.
        || value < f64::from(i32::MIN)
        || value > f64::from(i32::MAX)
    {
        return fallback;
    }
    value as i32
}

#[cfg(test)]
#[path = "sessions_settings_tests.rs"]
mod tests;
