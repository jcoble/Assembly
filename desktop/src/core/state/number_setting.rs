//! `NumberSetting` is one number set with a slider in Settings: its preference key, the slider's range and step, its
//! default, and the unit its value is shown with.

use serde_json::Value;

#[derive(Clone, Copy, Debug)]
pub struct NumberSetting {
    pub key: &'static str,
    pub min: f32,
    pub max: f32,
    pub step: f32,
    pub default: f32,
    pub unit: &'static str,
}

impl NumberSetting {
    /// The value a saved one means: a number is held in the range and moved to the nearest step; anything else
    /// (nothing saved, a reset's null, a string) gives the default.
    pub fn read(&self, saved: Option<&Value>) -> f32 {
        let Some(value) = saved.and_then(Value::as_f64).map(|value| value as f32) else {
            return self.default;
        };
        let steps = ((value - self.min) / self.step).round();
        (self.min + steps * self.step).clamp(self.min, self.max)
    }

    /// The value as the slider shows it beside itself: whole pixels, or a multiplier to one decimal.
    pub fn label(&self, value: f32) -> String {
        if self.step < 1. {
            format!("{value:.1}{}", self.unit)
        } else {
            format!("{value:.0}{}", self.unit)
        }
    }
}
