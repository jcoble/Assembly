//! `Machine` is one machine from `machines/list`. Main answers names only; whether it's reachable comes with the base
//! lane's machines step M1, and is added here then.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize)]
pub struct Machine {
    pub name: String,
}
