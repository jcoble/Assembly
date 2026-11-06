//! A task's raw Notion properties as text. Only the title, Status and Priority are edited; the rest are read-only.

use serde_json::Value;

use super::task_body::rich_plain;

/// The page's title, whatever its title property is called.
pub fn title_of(properties: &Value) -> String {
    properties
        .as_object()
        .into_iter()
        .flat_map(|map| map.values())
        .find(|property| property["type"] == "title")
        .map(|property| rich_plain(&property["title"]))
        .unwrap_or_default()
}

pub fn choice_of(properties: &Value, name: &str) -> String {
    let property = &properties[name];
    let kind = property["type"].as_str().unwrap_or_default();
    property[kind]["name"]
        .as_str()
        .unwrap_or_default()
        .to_owned()
}

/// Every other property with a value, in the order Notion's page gives them, as (name, text).
pub fn other_properties(properties: &Value) -> Vec<(String, String)> {
    let Some(map) = properties.as_object() else {
        return Vec::new();
    };
    map.iter()
        .filter(|(name, property)| {
            property["type"] != "title" && name.as_str() != "Status" && name.as_str() != "Priority"
        })
        .filter_map(|(name, property)| Some((name.clone(), value_text(property)?)))
        .collect()
}

fn value_text(property: &Value) -> Option<String> {
    let kind = property["type"].as_str()?;
    let value = &property[kind];
    let text = match kind {
        "rich_text" => rich_plain(value),
        "select" | "status" => value["name"].as_str()?.to_owned(),
        "multi_select" | "people" | "relation" => joined(value, kind)?,
        "checkbox" => if value.as_bool()? { "Yes" } else { "No" }.to_owned(),
        "date" => date_text(value)?,
        "url" | "email" | "phone_number" | "created_time" | "last_edited_time" => {
            value.as_str()?.to_owned()
        }
        "number" => value.as_f64().map(|number| number.to_string())?,
        "unique_id" => unique_id(value)?,
        "created_by" | "last_edited_by" => value["name"].as_str()?.to_owned(),
        "formula" | "rollup" => {
            let kind = value["type"].as_str()?;
            scalar(&value[kind])?
        }
        _ => return None,
    };
    (!text.is_empty()).then_some(text)
}

fn joined(value: &Value, kind: &str) -> Option<String> {
    let items = value.as_array()?;
    if kind == "relation" {
        return (!items.is_empty()).then(|| format!("{} linked", items.len()));
    }
    let names: Vec<&str> = items
        .iter()
        .filter_map(|item| item["name"].as_str())
        .collect();
    Some(names.join(", "))
}

fn date_text(value: &Value) -> Option<String> {
    let start = value["start"].as_str()?;
    Some(match value["end"].as_str() {
        Some(end) => format!("{start} → {end}"),
        None => start.to_owned(),
    })
}

fn unique_id(value: &Value) -> Option<String> {
    let number = value["number"].as_i64()?;
    Some(match value["prefix"].as_str() {
        Some(prefix) => format!("{prefix}-{number}"),
        None => number.to_string(),
    })
}

fn scalar(value: &Value) -> Option<String> {
    match value {
        Value::String(text) => Some(text.clone()),
        Value::Number(number) => Some(number.to_string()),
        Value::Bool(flag) => Some(if *flag { "Yes" } else { "No" }.to_owned()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn page() -> Value {
        json!({
            "Name":{"type":"title","title":[{"plain_text":"Fix it"}]},
            "Status":{"type":"status","status":{"name":"Doing"}},
            "Priority":{"type":"select","select":{"name":"High"}},
            "Task ID":{"type":"unique_id","unique_id":{"prefix":"TSK","number":12}},
            "Due":{"type":"date","date":{"start":"2026-11-04","end":null}},
            "Owner":{"type":"people","people":[{"name":"A"},{"name":"B"}]},
            "Empty":{"type":"rich_text","rich_text":[]},
            "Link":{"type":"url","url":null},
            "Done":{"type":"checkbox","checkbox":false},
            "Project":{"type":"relation","relation":[{"id":"x"}]},
            "Odd":{"type":"button","button":{}},
        })
    }

    #[test]
    fn the_edited_properties_come_from_their_own_slots() {
        assert_eq!(title_of(&page()), "Fix it");
        assert_eq!(choice_of(&page(), "Status"), "Doing");
        assert_eq!(choice_of(&page(), "Priority"), "High");
        assert_eq!(choice_of(&page(), "Missing"), "");
    }

    #[test]
    fn the_rest_read_as_text_and_empty_ones_are_left_out() {
        let rows = other_properties(&page());
        let names: Vec<&str> = rows.iter().map(|(name, _)| name.as_str()).collect();
        assert!(!names.contains(&"Empty") && !names.contains(&"Link") && !names.contains(&"Odd"));
        assert!(!names.contains(&"Status") && !names.contains(&"Name"));
        let value = |name: &str| rows.iter().find(|row| row.0 == name).unwrap().1.as_str();
        assert_eq!(value("Task ID"), "TSK-12");
        assert_eq!(value("Due"), "2026-11-04");
        assert_eq!(value("Owner"), "A, B");
        assert_eq!(value("Done"), "No");
        assert_eq!(value("Project"), "1 linked");
    }
}
