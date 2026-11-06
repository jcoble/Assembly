//! A picture on a task's page: where it may be read from, and how it's turned into pixels. Someone who can edit a page
//! chooses its address, so the reader only connects to public addresses, checked after the name is looked up, and a
//! picture is decoded only after its header says it's a sensible size, and only its first frame.
use std::io::Cursor;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::Arc;

use gpui_kit::{ImageFormat, RenderImage};
use image::{DynamicImage, Frame, ImageDecoder as _, ImageReader, Limits};
use ureq::config::Config;
use ureq::http::Uri;
use ureq::unversioned::resolver::{DefaultResolver, ResolvedSocketAddrs, Resolver};
use ureq::unversioned::transport::NextTimeout;

use super::rules::picture_format;

/// Wider or taller than this, it opens in the browser instead.
pub(super) const LONGEST_SIDE: u32 = 8192;
/// A 6K screenshot is about 20 million.
pub(super) const MOST_PIXELS: u64 = 40_000_000;

fn public_v4(ip: Ipv4Addr) -> bool {
    let [a, b, c, _] = ip.octets();
    !(ip.is_unspecified()
        || ip.is_loopback()
        || ip.is_private()
        || ip.is_link_local()
        || ip.is_broadcast()
        || ip.is_documentation()
        || ip.is_multicast()
        || a == 0
        || (a == 100 && (64..128).contains(&b))
        || (a == 192 && b == 0 && c == 0)
        || (a == 198 && (b == 18 || b == 19))
        || a >= 240)
}

fn public_v6(ip: Ipv6Addr) -> bool {
    let s = ip.segments();
    !(ip.is_unspecified()
        || ip.is_loopback()
        || ip.is_multicast()
        || (s[0] & 0xfe00) == 0xfc00
        || (s[0] & 0xffc0) == 0xfe80
        || (s[0] == 0x2001 && s[1] == 0x0db8)
        || (s[0] == 0x0064 && s[1] == 0xff9b)
        || s[0] == 0x2002
        || s[..6] == [0; 6])
}

/// Somewhere on the internet, not this machine, its network or a provider's metadata service.
pub(super) fn is_public(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(ip) => public_v4(ip),
        IpAddr::V6(ip) => ip.to_ipv4_mapped().map_or_else(|| public_v6(ip), public_v4),
    }
}

/// Looks the name up as usual and keeps only public addresses, which are the ones connected to, so a name can't be
/// pointed somewhere private after it was checked.
#[derive(Debug, Default)]
pub(super) struct PublicOnly(DefaultResolver);

impl Resolver for PublicOnly {
    fn resolve(
        &self,
        uri: &Uri,
        config: &Config,
        timeout: NextTimeout,
    ) -> Result<ResolvedSocketAddrs, ureq::Error> {
        let found = self.0.resolve(uri, config, timeout)?;
        let mut public = self.empty();
        for address in found
            .iter()
            .filter(|address: &&SocketAddr| is_public(address.ip()))
        {
            public.push(*address);
        }
        if public.is_empty() {
            return Err(ureq::Error::HostNotFound);
        }
        Ok(public)
    }
}

fn decoder_format(format: ImageFormat) -> Option<image::ImageFormat> {
    match format {
        ImageFormat::Png => Some(image::ImageFormat::Png),
        ImageFormat::Jpeg => Some(image::ImageFormat::Jpeg),
        ImageFormat::Gif => Some(image::ImageFormat::Gif),
        ImageFormat::Webp => Some(image::ImageFormat::WebP),
        _ => None,
    }
}

/// The picture's first frame, upright, as GPUI draws it. `None` if it isn't a picture, or is too big to hold.
pub fn decode_page_picture(bytes: &[u8]) -> Option<Arc<RenderImage>> {
    let format = decoder_format(picture_format(bytes)?)?;
    let mut limits = Limits::default();
    limits.max_image_width = Some(LONGEST_SIDE);
    limits.max_image_height = Some(LONGEST_SIDE);
    // Room for a 16-bit picture's decode and its copy into 8 bits.
    limits.max_alloc = Some(MOST_PIXELS * 12);
    let mut reader = ImageReader::with_format(Cursor::new(bytes), format);
    reader.limits(limits);
    let mut decoder = reader.into_decoder().ok()?;
    let (width, height) = decoder.dimensions();
    if u64::from(width) * u64::from(height) > MOST_PIXELS {
        return None;
    }
    let orientation = decoder.orientation().ok()?;
    let mut picture = DynamicImage::from_decoder(decoder).ok()?;
    picture.apply_orientation(orientation);
    let mut pixels = picture.into_rgba8();
    for pixel in pixels.as_chunks_mut::<4>().0 {
        pixel.swap(0, 2);
    }
    Some(Arc::new(RenderImage::new([Frame::new(pixels)])))
}

#[cfg(test)]
#[path = "page_picture_tests.rs"]
mod tests;
