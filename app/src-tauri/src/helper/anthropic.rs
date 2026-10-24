//! Phrasing one request for Anthropic's Messages API, and reading the answer.
//!
//! Request and response shapes, and the version header, taken from the Messages
//! API reference on 2026-08-19 (platform.claude.com/docs/en/api/messages).

use super::{
    vendor_error, HelperCompletion, HelperError, HelperHttpRequest, HelperHttpResponse,
    HelperRequest,
};
use serde_json::json;

const MESSAGES_URL: &str = "https://api.anthropic.com/v1/messages";
/// The API version this code is written against. It is not a date to keep
/// current — it pins the wire format, and changing it changes the shapes above.
const API_VERSION: &str = "2023-06-01";

pub fn build_request(model: &str, key: &str, request: &HelperRequest) -> HelperHttpRequest {
    HelperHttpRequest {
        url: MESSAGES_URL,
        headers: vec![
            ("x-api-key".to_string(), key.to_string()),
            ("anthropic-version".to_string(), API_VERSION.to_string()),
        ],
        body: json!({
            "model": model,
            "max_tokens": request.max_output,
            "system": request.system,
            "messages": [{ "role": "user", "content": request.input }],
        }),
    }
}

pub fn read_response(response: &HelperHttpResponse) -> Result<HelperCompletion, HelperError> {
    if response.status < 200 || response.status >= 300 {
        return Err(vendor_error(response));
    }
    let document: serde_json::Value = serde_json::from_str(&response.body)
        .map_err(|error| HelperError::BadResponse(error.to_string()))?;
    // Content is a list of blocks; only the text ones are the answer.
    let text: String = document["content"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|block| block["type"] == "text")
        .filter_map(|block| block["text"].as_str())
        .collect::<Vec<_>>()
        .join("");
    if text.trim().is_empty() {
        return Err(HelperError::BadResponse(
            "the answer held no text".to_string(),
        ));
    }
    Ok(HelperCompletion {
        text: text.trim().to_string(),
        input_tokens: document["usage"]["input_tokens"].as_u64().unwrap_or(0),
        output_tokens: document["usage"]["output_tokens"].as_u64().unwrap_or(0),
    })
}
