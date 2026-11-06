use std::net::IpAddr;

use super::{decode_page_picture, is_public};

/// A PNG header saying 100,000 by 100,000, with no pixels after it.
const HUGE: &[u8] = b"\x89\x50\x4e\x47\x0d\x0a\x1a\x0a\x00\x00\x00\x0d\x49\x48\x44\x52\x00\x01\x86\xa0\x00\x01\x86\xa0\x08\x06\x00\x00\x00\xa8\x52\x0b\xc8\x00\x00\x00\x00\x49\x45\x4e\x44\xae\x42\x60\x82";
/// 8000 by 8000: each side allowed, 64 million pixels in all.
const SQUARE_8000: &[u8] = b"\x89\x50\x4e\x47\x0d\x0a\x1a\x0a\x00\x00\x00\x0d\x49\x48\x44\x52\x00\x00\x1f\x40\x00\x00\x1f\x40\x08\x06\x00\x00\x00\x06\xf1\xad\xf4\x00\x00\x00\x00\x49\x45\x4e\x44\xae\x42\x60\x82";
/// A one-pixel GIF with two frames.
const TWO_FRAMES: &[u8] = b"\x47\x49\x46\x38\x39\x61\x01\x00\x01\x00\x80\x00\x00\xff\xff\xff\x00\x00\x00\x21\xf9\x04\x00\x0a\x00\x00\x00\x2c\x00\x00\x00\x00\x01\x00\x01\x00\x00\x02\x02\x44\x01\x00\x21\xf9\x04\x00\x0a\x00\x00\x00\x2c\x00\x00\x00\x00\x01\x00\x01\x00\x00\x02\x02\x44\x01\x00\x3b";

fn ip(text: &str) -> IpAddr {
    text.parse().unwrap()
}

#[test]
fn a_picture_too_big_to_hold_is_turned_away_from_its_header() {
    assert!(decode_page_picture(HUGE).is_none());
    assert!(decode_page_picture(SQUARE_8000).is_none());
}

#[test]
fn a_moving_picture_keeps_only_its_first_frame() {
    let gpui = gpui::Image::from_bytes(gpui::ImageFormat::Gif, TWO_FRAMES.to_vec());
    let whole = gpui
        .to_image_data(gpui::SvgRenderer::new(std::sync::Arc::new(())))
        .unwrap();
    assert_eq!(whole.frame_count(), 2, "the sample really has two frames");
    let picture = decode_page_picture(TWO_FRAMES).expect("decodes");
    assert_eq!(picture.frame_count(), 1);
}

#[test]
fn only_internet_addresses_count_as_public() {
    for public in [
        "8.8.8.8",
        "1.1.1.1",
        "2606:4700:4700::1111",
        "::ffff:8.8.8.8",
    ] {
        assert!(is_public(ip(public)), "{public}");
    }
    for private in [
        "127.0.0.1",
        "10.0.0.1",
        "172.16.0.1",
        "192.168.1.2",
        "169.254.169.254",
        "100.100.1.1",
        "0.0.0.0",
        "255.255.255.255",
        "224.0.0.1",
        "::1",
        "::",
        "fe80::1",
        "fd00::1",
        "::ffff:127.0.0.1",
        "::ffff:192.168.1.2",
        "64:ff9b::7f00:1",
        "2002:7f00:1::",
        "::127.0.0.1",
    ] {
        assert!(!is_public(ip(private)), "{private}");
    }
}

#[test]
fn a_name_that_looks_up_to_this_machine_is_never_connected_to() {
    let agent = super::super::page_agent();
    let error = agent.get("https://localhost/shot.png").call().unwrap_err();
    assert!(matches!(error, ureq::Error::HostNotFound), "{error:?}");
}
