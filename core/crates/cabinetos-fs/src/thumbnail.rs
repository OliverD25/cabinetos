//! The shell's pictures of a file (Quick View, ADR 0023): the thumbnail
//! Explorer shows, and a larger drawing of an image for a viewer page that
//! cannot decode it.
//!
//! Both come from `IShellItemImageFactory::GetImage`, which needs a COM
//! apartment on the calling thread. [`ShellImages`] is that apartment: the
//! core keeps one on each of its thumbnail threads and never calls it from
//! a thread that answers the pipe, because a thumbnail handler that hangs
//! hangs its caller. The bitmap the shell answers is read with `GetDIBits`
//! and encoded as a PNG with straight alpha.

use std::marker::PhantomData;

use windows::Win32::Foundation::SIZE;
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAP, BITMAPINFO, BITMAPINFOHEADER, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC,
    DeleteObject, GetDIBits, GetObjectW, HBITMAP, HDC, HGDIOBJ,
};
use windows::Win32::UI::Shell::{
    IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF, SIIGBF_BIGGERSIZEOK,
    SIIGBF_INCACHEONLY, SIIGBF_RESIZETOFIT, SIIGBF_THUMBNAILONLY,
};
use windows::core::PCWSTR;

use crate::FsError;
use crate::com::Apartment;

/// What to ask the shell for.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ImageRequest {
    /// The thumbnail at about this size: a real thumbnail or nothing, never
    /// a generic icon, and a bigger one from the cache is welcome.
    Thumbnail,
    /// The thumbnail only if the shell's cache has it: a file not on this
    /// disk must not be downloaded to be shown.
    CachedThumbnail,
    /// The image drawn to fit this size, for a viewer page.
    Render,
}

impl ImageRequest {
    fn flags(self) -> SIIGBF {
        let flags = match self {
            Self::Thumbnail => {
                SIIGBF_RESIZETOFIT.0 | SIIGBF_BIGGERSIZEOK.0 | SIIGBF_THUMBNAILONLY.0
            }
            Self::CachedThumbnail => {
                SIIGBF_RESIZETOFIT.0
                    | SIIGBF_BIGGERSIZEOK.0
                    | SIIGBF_THUMBNAILONLY.0
                    | SIIGBF_INCACHEONLY.0
            }
            Self::Render => SIIGBF_RESIZETOFIT.0 | SIIGBF_THUMBNAILONLY.0,
        };
        SIIGBF(flags)
    }
}

/// A picture of a file, as a PNG.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShellImage {
    /// Its width in pixels.
    pub width: u32,
    /// Its height in pixels.
    pub height: u32,
    /// The PNG, RGBA with straight alpha.
    pub png: Vec<u8>,
}

/// Why the shell gave no picture.
#[derive(Debug, thiserror::Error)]
pub enum ShellImageError {
    /// The path is not there, not allowed or malformed.
    #[error(transparent)]
    Path(FsError),
    /// The shell has no picture of this file (no thumbnail handler, not in
    /// the cache, a handler that failed), or its bitmap cannot be read.
    #[error("{0}")]
    NoImage(String),
}

/// A COM apartment on this thread for the shell's image factory. Not
/// `Send`: COM must be left on the thread that entered it.
pub struct ShellImages {
    _apartment: Apartment,
    _thread_bound: PhantomData<*const ()>,
}

impl Default for ShellImages {
    fn default() -> Self {
        Self::enter()
    }
}

impl ShellImages {
    /// Joins a single-threaded apartment on this thread.
    #[must_use]
    pub fn enter() -> Self {
        Self {
            _apartment: Apartment::enter(),
            _thread_bound: PhantomData,
        }
    }

    /// The shell's picture of `path` (an absolute path to a file or a
    /// folder), `size` pixels on its longer side at most, aspect kept. A
    /// bigger bitmap from the shell is scaled down; a smaller image keeps
    /// its size. Blocks for as long as the shell takes.
    pub fn image(
        &self,
        path: &str,
        size: u32,
        request: ImageRequest,
    ) -> Result<ShellImage, ShellImageError> {
        let factory = factory_of(path)?;
        let pixels = i32::try_from(size).unwrap_or(i32::MAX);
        // SAFETY: `factory` is a live interface on this thread, which is in
        // an apartment (`self`); the bitmap it returns belongs to us and is
        // deleted by the guard.
        let bitmap = unsafe {
            factory.GetImage(
                SIZE {
                    cx: pixels,
                    cy: pixels,
                },
                request.flags(),
            )
        }
        .map_err(|error| ShellImageError::NoImage(error.message()))?;
        let bitmap = OwnedBitmap(bitmap);
        let (width, height, rgba) = read_bitmap(bitmap.0).map_err(ShellImageError::NoImage)?;
        let (width, height, rgba) = fit(&rgba, width, height, size);
        let png = encode_png(&rgba, width, height).map_err(ShellImageError::NoImage)?;
        Ok(ShellImage { width, height, png })
    }

    /// Has the shell load its image factory and the thumbnail cache for
    /// `folder`, so the first real request does not pay for the start.
    /// Asks only the cache, so nothing is drawn and nothing is downloaded.
    pub fn warm(&self, folder: &str) {
        if let Ok(factory) = factory_of(folder) {
            // SAFETY: as in `image`; a bitmap that comes back is deleted.
            let answer = unsafe {
                factory.GetImage(
                    SIZE { cx: 96, cy: 96 },
                    ImageRequest::CachedThumbnail.flags(),
                )
            };
            if let Ok(bitmap) = answer {
                drop(OwnedBitmap(bitmap));
            }
        }
    }
}

/// The image factory of the shell item at `path`.
fn factory_of(path: &str) -> Result<IShellItemImageFactory, ShellImageError> {
    if path.is_empty() || path.contains('\0') {
        return Err(ShellImageError::Path(FsError::InvalidPath {
            path: path.to_owned(),
            reason: "the path is empty or contains a NUL character".to_owned(),
        }));
    }
    // The shell's parsing names do not take the `\\?\` form.
    let wide: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: `wide` is NUL-terminated and outlives the call; no bind
    // context is passed.
    unsafe { SHCreateItemFromParsingName(PCWSTR(wide.as_ptr()), None) }.map_err(|error| {
        // HRESULT_FROM_WIN32 of a Win32 error keeps the code in the low word.
        let code = error.code().0.cast_unsigned();
        if code & 0xFFFF_0000 == 0x8007_0000 {
            ShellImageError::Path(FsError::from_win32(path, code & 0xFFFF))
        } else {
            ShellImageError::NoImage(error.message())
        }
    })
}

/// Deletes a bitmap when dropped.
struct OwnedBitmap(HBITMAP);

impl Drop for OwnedBitmap {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            // SAFETY: the shell made the bitmap for this caller, and nothing
            // uses it after this.
            let _ = unsafe { DeleteObject(HGDIOBJ(self.0.0)) };
        }
    }
}

/// A memory device context, deleted when dropped.
struct MemoryDc(HDC);

impl Drop for MemoryDc {
    fn drop(&mut self) {
        // SAFETY: the context was made by `read_bitmap` and nothing uses it
        // after this.
        let _ = unsafe { DeleteDC(self.0) };
    }
}

/// The bitmap's size and its pixels as RGBA with straight alpha, top row
/// first.
fn read_bitmap(bitmap: HBITMAP) -> Result<(u32, u32, Vec<u8>), String> {
    let mut info = BITMAP::default();
    // SAFETY: the handle is a live bitmap; `info` is a local of the size
    // passed.
    let filled = unsafe {
        GetObjectW(
            HGDIOBJ(bitmap.0),
            i32::try_from(size_of::<BITMAP>()).unwrap_or(0),
            Some((&raw mut info).cast()),
        )
    };
    let (Ok(width), Ok(height)) = (u32::try_from(info.bmWidth), u32::try_from(info.bmHeight))
    else {
        return Err("the shell's bitmap has no size".to_owned());
    };
    if filled == 0 || width == 0 || height == 0 {
        return Err("the shell's bitmap has no size".to_owned());
    }
    let mut header = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: u32::try_from(size_of::<BITMAPINFOHEADER>()).unwrap_or(u32::MAX),
            biWidth: info.bmWidth,
            // Negative: the rows come top down.
            biHeight: -info.bmHeight,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..BITMAPINFOHEADER::default()
        },
        ..BITMAPINFO::default()
    };
    // SAFETY: a plain call; the context is deleted by the guard.
    let dc = MemoryDc(unsafe { CreateCompatibleDC(None) });
    if dc.0.is_invalid() {
        return Err("cannot make a device context".to_owned());
    }
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    // SAFETY: `dc` and `bitmap` are valid; `pixels` holds `height` rows of
    // `width` 32-bit pixels, which is what `header` asks for; the bitmap is
    // not selected into any device context.
    let lines = unsafe {
        GetDIBits(
            dc.0,
            bitmap,
            0,
            height,
            Some(pixels.as_mut_ptr().cast()),
            &raw mut header,
            DIB_RGB_COLORS,
        )
    };
    if lines != info.bmHeight {
        return Err("cannot read the shell's bitmap".to_owned());
    }
    straighten(&mut pixels);
    Ok((width, height, pixels))
}

/// BGRA with premultiplied alpha, as the shell's bitmaps are, to RGBA with
/// straight alpha. A bitmap whose alpha is zero everywhere has none (a
/// photo's thumbnail): it is opaque.
fn straighten(pixels: &mut [u8]) {
    let opaque = pixels.as_chunks::<4>().0.iter().all(|pixel| pixel[3] == 0);
    for pixel in pixels.as_chunks_mut::<4>().0 {
        pixel.swap(0, 2);
        if opaque {
            pixel[3] = 255;
        } else if pixel[3] != 0 && pixel[3] != 255 {
            let alpha = u32::from(pixel[3]);
            for channel in &mut pixel[..3] {
                let straight = (u32::from(*channel) * 255 + alpha / 2) / alpha;
                *channel = u8::try_from(straight.min(255)).unwrap_or(u8::MAX);
            }
        }
    }
}

/// `rgba` scaled down so that its longer side is at most `size`, aspect
/// kept: each pixel the average of those it covers, weighted by alpha. A
/// picture that fits already is returned as it is.
fn fit(rgba: &[u8], width: u32, height: u32, size: u32) -> (u32, u32, Vec<u8>) {
    let longer = width.max(height);
    if longer <= size || size == 0 {
        return (width, height, rgba.to_vec());
    }
    let scaled = |side: u32| {
        u32::try_from(
            (u64::from(side) * u64::from(size) + u64::from(longer) / 2) / u64::from(longer),
        )
        .unwrap_or(1)
        .max(1)
    };
    let (new_width, new_height) = (scaled(width), scaled(height));
    let (w, h, nw, nh) = (
        width as usize,
        height as usize,
        new_width as usize,
        new_height as usize,
    );
    let mut out = vec![0u8; nw * nh * 4];
    for ty in 0..nh {
        let (y0, y1) = (ty * h / nh, ((ty + 1) * h / nh).max(ty * h / nh + 1).min(h));
        for tx in 0..nw {
            let (x0, x1) = (tx * w / nw, ((tx + 1) * w / nw).max(tx * w / nw + 1).min(w));
            let (mut color, mut alpha, mut count) = ([0u64; 3], 0u64, 0u64);
            for y in y0..y1 {
                for x in x0..x1 {
                    let pixel = &rgba[(y * w + x) * 4..][..4];
                    let a = u64::from(pixel[3]);
                    for (sum, channel) in color.iter_mut().zip(pixel) {
                        *sum += u64::from(*channel) * a;
                    }
                    alpha += a;
                    count += 1;
                }
            }
            let target = &mut out[(ty * nw + tx) * 4..][..4];
            for (channel, sum) in target.iter_mut().zip(color) {
                *channel = sum
                    .checked_div(alpha)
                    .map_or(0, |value| u8::try_from(value).unwrap_or(u8::MAX));
            }
            target[3] = u8::try_from(alpha / count.max(1)).unwrap_or(u8::MAX);
        }
    }
    (new_width, new_height, out)
}

/// RGBA pixels as a PNG.
fn encode_png(rgba: &[u8], width: u32, height: u32) -> Result<Vec<u8>, String> {
    let mut png = Vec::new();
    let mut encoder = png::Encoder::new(&mut png, width, height);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    // A thumbnail is made once and shown at once: speed over size.
    encoder.set_compression(png::Compression::Fast);
    let mut writer = encoder
        .write_header()
        .map_err(|error| format!("cannot write the PNG: {error}"))?;
    writer
        .write_image_data(rgba)
        .map_err(|error| format!("cannot write the PNG: {error}"))?;
    writer
        .finish()
        .map_err(|error| format!("cannot write the PNG: {error}"))?;
    Ok(png)
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::Path;
    use std::time::Instant;

    use super::*;

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("thumbnail")
            .tempdir_in(root)
            .unwrap()
    }

    /// A PNG of `width` by `height` pixels, a gradient, so the shell has
    /// something to draw.
    pub(crate) fn write_png(path: &Path, width: u32, height: u32) {
        let mut rgba = Vec::with_capacity(width as usize * height as usize * 4);
        for y in 0..height {
            for x in 0..width {
                rgba.extend([
                    u8::try_from(x * 255 / width).unwrap(),
                    u8::try_from(y * 255 / height).unwrap(),
                    128,
                    255,
                ]);
            }
        }
        fs::write(path, encode_png(&rgba, width, height).unwrap()).unwrap();
    }

    fn decode(png: &[u8]) -> (u32, u32) {
        let decoder = png::Decoder::new(std::io::Cursor::new(png));
        let reader = decoder.read_info().unwrap();
        let info = reader.info();
        (info.width, info.height)
    }

    #[test]
    fn a_png_gets_a_thumbnail_with_its_aspect() {
        let dir = scratch();
        let file = dir.path().join("wide.png");
        write_png(&file, 800, 400);
        let images = ShellImages::enter();
        let started = Instant::now();
        let image = images
            .image(&file.display().to_string(), 256, ImageRequest::Thumbnail)
            .unwrap();
        let first_ms = started.elapsed().as_secs_f64() * 1000.0;
        assert_eq!((image.width, image.height), (256, 128));
        assert_eq!(decode(&image.png), (256, 128));
        let started = Instant::now();
        images
            .image(&file.display().to_string(), 256, ImageRequest::Thumbnail)
            .unwrap();
        let second_ms = started.elapsed().as_secs_f64() * 1000.0;
        // Printed for the report: `cargo test -p cabinetos-fs thumbnail -- --nocapture`.
        println!("thumbnail of an 800x400 PNG: first {first_ms:.1} ms, again {second_ms:.1} ms");
    }

    #[test]
    fn a_file_the_shell_has_no_thumbnail_for_gives_none() {
        let dir = scratch();
        let file = dir.path().join("notes.xyz");
        fs::write(&file, b"no handler knows this").unwrap();
        let images = ShellImages::enter();
        let error = images
            .image(&file.display().to_string(), 256, ImageRequest::Thumbnail)
            .unwrap_err();
        assert!(matches!(error, ShellImageError::NoImage(_)), "{error:?}");
    }

    #[test]
    fn a_missing_file_is_not_found() {
        let dir = scratch();
        let images = ShellImages::enter();
        let error = images
            .image(
                &dir.path().join("gone.png").display().to_string(),
                256,
                ImageRequest::Thumbnail,
            )
            .unwrap_err();
        assert!(
            matches!(error, ShellImageError::Path(FsError::NotFound { .. })),
            "{error:?}"
        );
    }

    #[test]
    fn a_render_fits_the_size_asked() {
        let dir = scratch();
        let file = dir.path().join("big.png");
        write_png(&file, 1024, 768);
        let images = ShellImages::enter();
        let image = images
            .image(&file.display().to_string(), 512, ImageRequest::Render)
            .unwrap();
        assert_eq!((image.width, image.height), (512, 384));
    }

    #[test]
    fn warming_a_folder_never_fails() {
        let dir = scratch();
        let images = ShellImages::enter();
        images.warm(&dir.path().display().to_string());
        images.warm(r"C:\no such folder here");
    }

    /// The numbers for a report, not a check: a 12-megapixel PNG's
    /// thumbnail the first time (the shell decodes it), again (the shell's
    /// thumbnail cache), and the 768 size. Run it with
    /// `cargo test -p cabinetos-fs measure_thumbnail_times -- --ignored --nocapture`.
    #[test]
    #[ignore = "prints timings for a report"]
    fn measure_thumbnail_times() {
        let dir = scratch();
        let images = ShellImages::enter();
        let started = Instant::now();
        images.warm(&dir.path().display().to_string());
        println!(
            "warm start on the folder: {:.1} ms",
            started.elapsed().as_secs_f64() * 1000.0
        );
        for round in 0..3 {
            let file = dir.path().join(format!("photo-{round}.png"));
            write_png(&file, 4000, 3000);
            let path = file.display().to_string();
            let mut times = Vec::new();
            for size in [256, 256, 768] {
                let started = Instant::now();
                let image = images.image(&path, size, ImageRequest::Thumbnail).unwrap();
                times.push(format!(
                    "{size}: {:.1} ms ({}x{})",
                    started.elapsed().as_secs_f64() * 1000.0,
                    image.width,
                    image.height
                ));
            }
            println!("12 MP PNG, round {round}: {}", times.join(", "));
        }
    }

    #[test]
    fn fitting_keeps_the_aspect_and_never_enlarges() {
        let rgba = vec![255u8; 400 * 100 * 4];
        let (w, h, out) = fit(&rgba, 400, 100, 256);
        assert_eq!((w, h, out.len()), (256, 64, 256 * 64 * 4));
        let (w, h, _) = fit(&rgba, 400, 100, 512);
        assert_eq!((w, h), (400, 100));
        let tall = vec![255u8; 10 * 3000 * 4];
        let (w, h, _) = fit(&tall, 10, 3000, 256);
        assert_eq!((w, h), (1, 256));
    }

    #[test]
    fn premultiplied_pixels_become_straight_and_no_alpha_means_opaque() {
        let mut half = vec![10, 20, 64, 128];
        straighten(&mut half);
        assert_eq!(half, [128, 40, 20, 128]);
        let mut photo = vec![1, 2, 3, 0, 4, 5, 6, 0];
        straighten(&mut photo);
        assert_eq!(photo, [3, 2, 1, 255, 6, 5, 4, 255]);
    }
}
