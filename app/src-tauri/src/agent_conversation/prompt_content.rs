//! Prompt content blocks sent from the composer.
//!
//! The composer sends an ordered list of blocks: the typed message plus any
//! images the user pasted or dropped. ACP carries image bytes as base64 text,
//! so the raw bytes that arrive from the window are encoded here once, on the
//! way into the provider prompt.

use serde::{Deserialize, Serialize};

use super::providers::{AgentPrompt, AgentPromptImage};

/// One block of an outgoing prompt, in the shape the window sends.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum AgentPromptContentBlock {
    Text {
        text: String,
    },
    #[serde(rename_all = "camelCase")]
    Image {
        mime_type: String,
        data: Vec<u8>,
        #[serde(default)]
        name: Option<String>,
    },
}

/// Build the provider prompt from the message text and its content blocks.
///
/// Text blocks are ignored: the request already carries the message text, and
/// duplicating it would send the message twice.
pub fn prompt_from_blocks(
    text: &str,
    blocks: Vec<AgentPromptContentBlock>,
) -> Result<AgentPrompt, String> {
    let mut images = Vec::new();
    for block in blocks {
        let AgentPromptContentBlock::Image {
            mime_type, data, ..
        } = block
        else {
            continue;
        };
        if data.is_empty() {
            return Err("Image content block has no data".to_string());
        }
        let mime_type = mime_type.trim();
        if !mime_type.starts_with("image/") {
            return Err(format!("Content block type {mime_type} is not an image"));
        }
        images.push(AgentPromptImage {
            data: encode_base64(&data),
            mime_type: mime_type.to_string(),
        });
    }
    if text.is_empty() && images.is_empty() {
        return Err("Message cannot be empty".to_string());
    }
    Ok(AgentPrompt {
        text: text.to_string(),
        images,
        attachment_ids: Vec::new(),
    })
}

const BASE64_ALPHABET: &[u8; 64] =
    b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Standard base64 with padding, the encoding ACP expects for image data.
fn encode_base64(bytes: &[u8]) -> String {
    let mut out = String::with_capacity((bytes.len() + 2) / 3 * 4);
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let triple = (b0 << 16) | (b1 << 8) | b2;
        out.push(BASE64_ALPHABET[(triple >> 18) as usize & 63] as char);
        out.push(BASE64_ALPHABET[(triple >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 {
            BASE64_ALPHABET[(triple >> 6) as usize & 63] as char
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            BASE64_ALPHABET[triple as usize & 63] as char
        } else {
            '='
        });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_conversation::manager::prompt_params;

    #[test]
    fn base64_matches_the_standard_padded_encoding() {
        assert_eq!(encode_base64(b""), "");
        assert_eq!(encode_base64(b"f"), "Zg==");
        assert_eq!(encode_base64(b"fo"), "Zm8=");
        assert_eq!(encode_base64(b"foo"), "Zm9v");
        assert_eq!(encode_base64(b"foob"), "Zm9vYg==");
        assert_eq!(encode_base64(b"foobar"), "Zm9vYmFy");
        assert_eq!(encode_base64(&[0xff, 0xfe, 0xfd]), "//79");
    }

    #[test]
    fn content_blocks_deserialize_from_the_window_payload() {
        let blocks: Vec<AgentPromptContentBlock> = serde_json::from_str(
            r#"[{"type":"text","text":"look"},{"type":"image","mimeType":"image/png","data":[102,111,111],"name":"paste.png"}]"#,
        )
        .unwrap();
        assert_eq!(
            blocks,
            vec![
                AgentPromptContentBlock::Text {
                    text: "look".to_string()
                },
                AgentPromptContentBlock::Image {
                    mime_type: "image/png".to_string(),
                    data: vec![102, 111, 111],
                    name: Some("paste.png".to_string())
                }
            ]
        );
    }

    #[test]
    fn images_become_base64_prompt_images_and_text_is_not_duplicated() {
        let prompt = prompt_from_blocks(
            "look",
            vec![
                AgentPromptContentBlock::Text {
                    text: "look".to_string(),
                },
                AgentPromptContentBlock::Image {
                    mime_type: "image/png".to_string(),
                    data: b"foo".to_vec(),
                    name: None,
                },
            ],
        )
        .unwrap();
        assert_eq!(prompt.text, "look");
        assert_eq!(
            prompt.images,
            vec![AgentPromptImage {
                data: "Zm9v".to_string(),
                mime_type: "image/png".to_string()
            }]
        );
    }

    #[test]
    fn an_image_alone_is_a_valid_prompt_and_empty_content_is_rejected() {
        let prompt = prompt_from_blocks(
            "",
            vec![AgentPromptContentBlock::Image {
                mime_type: "image/png".to_string(),
                data: b"foo".to_vec(),
                name: None,
            }],
        )
        .unwrap();
        assert_eq!(prompt.text, "");
        assert_eq!(prompt.images.len(), 1);
        assert!(prompt_from_blocks("", Vec::new()).is_err());
        assert!(prompt_from_blocks(
            "look",
            vec![AgentPromptContentBlock::Image {
                mime_type: "text/plain".to_string(),
                data: b"foo".to_vec(),
                name: None,
            }]
        )
        .is_err());
    }

    /// Pins the ACP wire shape a prompt with images produces.
    #[test]
    fn acp_prompt_params_carry_text_then_image_blocks() {
        let prompt = prompt_from_blocks(
            "look",
            vec![AgentPromptContentBlock::Image {
                mime_type: "image/png".to_string(),
                data: b"foo".to_vec(),
                name: None,
            }],
        )
        .unwrap();
        let params = prompt_params("session-1".to_string(), prompt);
        assert_eq!(
            params,
            serde_json::json!({
                "sessionId": "session-1",
                "prompt": [
                    { "type": "text", "text": "look" },
                    { "type": "image", "data": "Zm9v", "mimeType": "image/png" }
                ]
            })
        );
    }
}
