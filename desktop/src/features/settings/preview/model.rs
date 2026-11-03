//! In-memory fixtures for the native Settings preview. No network or persistence.
#[derive(Clone, Debug, PartialEq)]
pub struct Machine {
    pub id: u64,
    pub name: String,
    pub destination: String,
    pub connected: bool,
    pub installed: bool,
}

#[derive(Default)]
pub struct Connections {
    pub machines: Vec<Machine>,
    next_id: u64,
}

impl Connections {
    pub fn sample() -> Self {
        Self {
            next_id: 3,
            machines: vec![
                Machine {
                    id: 0,
                    name: "Workbox".into(),
                    destination: "workbox.example".into(),
                    connected: true,
                    installed: true,
                },
                Machine {
                    id: 1,
                    name: "Linux development".into(),
                    destination: "dev@linux.example".into(),
                    connected: false,
                    installed: true,
                },
                Machine {
                    id: 2,
                    name: "New server".into(),
                    destination: "dev@new-server.example".into(),
                    connected: false,
                    installed: false,
                },
            ],
        }
    }

    pub fn save(
        &mut self,
        name: &str,
        destination: &str,
        editing: Option<usize>,
    ) -> Result<(), &'static str> {
        let (name, destination) = (name.trim(), destination.trim());
        if name.is_empty() {
            return Err("Enter a machine name.");
        }
        if destination.is_empty()
            || destination.chars().any(char::is_whitespace)
            || destination.starts_with('-')
        {
            return Err("Enter an SSH destination or alias without spaces.");
        }
        if self
            .machines
            .iter()
            .enumerate()
            .any(|(i, m)| Some(i) != editing && m.name.eq_ignore_ascii_case(name))
        {
            return Err("A machine with that name already exists.");
        }
        match editing {
            Some(i) => {
                let machine = self
                    .machines
                    .get_mut(i)
                    .ok_or("This machine is no longer available.")?;
                machine.name = name.into();
                machine.destination = destination.into();
            }
            None => {
                self.machines.push(Machine {
                    id: self.next_id,
                    name: name.into(),
                    destination: destination.into(),
                    connected: false,
                    installed: false,
                });
                self.next_id += 1;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_and_duplicate_profiles_leave_sample_machines_unchanged() {
        let mut model = Connections::sample();
        let before = model.machines.clone();
        assert!(model.save("", "example", None).is_err());
        assert!(model.save("A", "bad host", None).is_err());
        assert!(model.save("workBOX", "example", None).is_err());
        assert_eq!(model.machines, before);
    }
    #[test]
    fn editing_preserves_installation_and_connection_state() {
        let mut model = Connections::sample();
        model.save(" Renamed ", " build.example ", Some(0)).unwrap();
        assert_eq!(model.machines.len(), 3);
        assert_eq!(model.machines[0].name, "Renamed");
        assert!(model.machines[0].installed && model.machines[0].connected);
    }
    #[test]
    fn a_new_profile_is_not_an_installed_or_connected_backend() {
        let mut model = Connections::sample();
        model.save("New", "new.example", None).unwrap();
        let added = model.machines.last().unwrap();
        assert!(!added.connected && !added.installed);
    }
}
