//! `Machine` is one machine from `machines/list`. Main answers `{machines: [name]}`, names only; whether it's reachable comes with the base
//! lane's machines step M1, and is added here then.

#[derive(Clone, Debug)]
pub struct Machine {
    pub name: String,
}
