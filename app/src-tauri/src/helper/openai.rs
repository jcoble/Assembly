//! Phrasing one request for OpenAI's Responses API, and reading the answer.
//!
//! Request and response shapes taken from the Responses API reference on
//! 2026-08-19 (developers.openai.com/api/docs/api-reference/responses/create).

use super::{
    vendor_error, HelperCompletion, HelperError, HelperHttpRequest, HelperHttpResponse,
    HelperRequest,
};
use serde_json::json;

const RESPONSES_URL: &str = "https://api.openai.com/v1/responses";

pub fn build_request(model: &str, key: &str, request: &HelperRequest) -> HelperHttpRequest {
    HelperHttpRequest {
        url: RESPONSES_URL,
        headers: vec![("Authorization".to_string(), format!("Bearer {key}"))],
        body: json!({
            "model": model,
            "instructions": request.system,
            "input": request.input,
            "max_output_tokens": request.max_output,
        }),
    }
}

pub fn read_response(response: &HelperHttpResponse) -> Result<HelperCompletion, HelperError> {
    if response.status < 200 || response.status >= 300 {
        return Err(vendor_error(response));
    }
    let document: serde_json::Value = serde_json::from_str(&response.body)
        .map_err(|error| HelperError::BadResponse(error.to_string()))?;
    // The answer arrives as a list of output items, each with its own list of
    // content parts. Only the text parts are wanted, and a model that reasons
    // before answering puts other kinds of item in front of them.
    let text: String = document["output"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|item| item["content"].as_array())
        .flatten()
        .filter(|part| part["type"] == "output_text")
        .filter_map(|part| part["text"].as_str())
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
