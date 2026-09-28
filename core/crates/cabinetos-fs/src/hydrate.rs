//! What Explorer shows beside a name: the shell's type name and icon
//! (Phase 2 moved this "hydration" to the shell's phase). The UI asks for
//! the entries it shows; nothing is streamed.
//!
//! **Type names** come from `SHGetFileInfoW` with `SHGFI_TYPENAME` and
//! `SHGFI_USEFILEATTRIBUTES`: by the extension and the folder attribute
//! only, so nothing is read from the disk. They are cached per extension.
//! When a program registered an extension without a readable name, the
//! shell answers the program's internal name for it (a program
//! identifier, such as `txtfile` for `.gitattributes`); such a name
//! becomes `{EXT} File`, the form Explorer uses for a type nobody named.
//!
//! **Icon keys** name an icon for [`Hydrator::icon_png`]: `folder` for
//! every folder, `ext:<extension>` (lower case, with its dot) for a file by
//! its extension, `generic` for a file without one, and `path:<hash>` for
//! `.exe`, `.ico` and `.lnk` files, whose icon lives in the file itself.
//! The hash is FNV-1a (64 bits, 16 hex digits) over the lower-case full
//! path, the last-write time and the size, so a changed file gets a new key
//! and a client's icon cache stays right; the hydrator keeps which path each
//! key stands for.
//!
//! **Icons** come from the system image lists: `SHGetFileInfoW` with
//! `SHGFI_SYSICONINDEX` (plus `SHGFI_USEFILEATTRIBUTES`, except for a
//! `path:` key, whose file is read) gives the icon's index, and
//! `SHGetImageList` the list of the size: `SHIL_SMALL` for 16 pixels,
//! `SHIL_LARGE` for 32 and `SHIL_EXTRALARGE` for 48. 24 is the 48-pixel
//! icon halved, which is sharper than enlarging the 16-pixel one or
//! shrinking the 32-pixel one by a quarter. The icon's pixels are read with
//! `GetIconInfo` and `GetDIBits` and encoded as a PNG; the PNGs are cached
//! per key and size.

use std::collections::{HashMap, VecDeque};
use std::hash::Hash;
use std::io;
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use cabinetos_protocol::EntryDetail;
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAP, BITMAPINFO, BITMAPINFOHEADER, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC,
    DeleteObject, GetDIBits, GetObjectW, HBITMAP, HDC, HGDIOBJ,
};
use windows::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL, FILE_FLAGS_AND_ATTRIBUTES,
};
use windows::Win32::UI::Controls::{IImageList, ILD_TRANSPARENT};
use windows::Win32::UI::Shell::{
    SHFILEINFOW, SHGFI_SYSICONINDEX, SHGFI_TYPENAME, SHGFI_USEFILEATTRIBUTES, SHGetFileInfoW,
    SHGetImageList, SHIL_EXTRALARGE, SHIL_LARGE, SHIL_SMALL,
};
use windows::Win32::UI::WindowsAndMessaging::{DestroyIcon, GetIconInfo, HICON, ICONINFO};
use windows::core::PCWSTR;

use crate::com::Apartment;
use crate::section::{EntryView, LayoutError, ListingReader};
use crate::{FsError, attributes};

/// Icons are drawn one at a time in the whole process: the system image
/// lists belong to the process, not to a hydrator, and two drawings at once
/// make `SHGetFileInfoW` answer that there is no icon (two hydrators in
/// the tests did, every run).
static DRAWING: Mutex<()> = Mutex::new(());

/// The icon sizes [`Hydrator::icon_png`] makes, in pixels.
pub const ICON_SIZES: [u32; 4] = [16, 24, 32, 48];

/// PNGs kept, one per key and size.
pub const ICONS_KEPT: usize = 2_000;

/// Type names kept, one per extension.
const TYPE_NAMES_KEPT: usize = 4_096;

/// `path:` keys kept, each with its path. A key forgotten here is
/// `not_found` until its folder is described again.
const PATHS_KEPT: usize = 16_384;

/// The extensions whose icon lives in the file itself.
const OWN_ICON: [&str; 3] = [".exe", ".ico", ".lnk"];

/// PNGs by icon key and size.
type IconCache = Bounded<(String, u32), Arc<[u8]>>;

/// Type names and icons for listing entries, with their caches. One serves
/// every connection.
#[derive(Debug)]
pub struct Hydrator {
    type_names: Mutex<Bounded<TypeKey, String>>,
    paths: Mutex<Bounded<String, String>>,
    icons: Mutex<IconCache>,
}

/// What a type name depends on.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
enum TypeKey {
    Folder,
    NoExtension,
    Extension(String),
}

/// Where an icon comes from.
enum IconSource {
    /// A kind of entry, by attributes and a made-up name with its extension.
    Kind {
        probe: String,
        attributes: FILE_FLAGS_AND_ATTRIBUTES,
    },
    /// A file whose icon is its own.
    File(String),
}

impl Default for Hydrator {
    fn default() -> Self {
        Self::new()
    }
}

impl Hydrator {
    /// A hydrator with empty caches.
    #[must_use]
    pub fn new() -> Self {
        Self {
            type_names: Mutex::new(Bounded::new(TYPE_NAMES_KEPT)),
            paths: Mutex::new(Bounded::new(PATHS_KEPT)),
            icons: Mutex::new(Bounded::new(ICONS_KEPT)),
        }
    }

    /// The details of entries `from..from + count` of the listing in
    /// `section` (fewer at its end, none past it), and the generation of the
    /// section. `folder` is the listing's folder: a `path:` key stands for a
    /// file in it. Blocking: the shell is asked for new extensions.
    pub fn describe(
        &self,
        section: &[u8],
        folder: &str,
        from: u32,
        count: u32,
    ) -> Result<(u32, Vec<EntryDetail>), LayoutError> {
        let reader = ListingReader::new(section)?;
        let start = from as usize;
        let end = start.saturating_add(count as usize).min(reader.len());
        let _apartment = Apartment::enter();
        let details = (start..end)
            .map(|index| Ok(self.detail(&reader.entry(index)?, folder)))
            .collect::<Result<_, LayoutError>>()?;
        Ok((reader.header().generation, details))
    }

    fn detail(&self, entry: &EntryView, folder: &str) -> EntryDetail {
        if entry.meta.attributes & attributes::DIRECTORY != 0 {
            return EntryDetail {
                type_name: self.type_name(TypeKey::Folder),
                icon_key: "folder".to_owned(),
            };
        }
        let Some(extension) = extension(&entry.name) else {
            return EntryDetail {
                type_name: self.type_name(TypeKey::NoExtension),
                icon_key: "generic".to_owned(),
            };
        };
        let icon_key = if OWN_ICON.contains(&extension.as_str()) {
            self.path_key(folder, entry)
        } else {
            format!("ext:{extension}")
        };
        EntryDetail {
            type_name: self.type_name(TypeKey::Extension(extension)),
            icon_key,
        }
    }

    /// The shell's name for the type, cached. Empty when the shell has none.
    fn type_name(&self, key: TypeKey) -> String {
        if let Some(name) = lock(&self.type_names).get(&key) {
            return name;
        }
        let (probe, attributes) = probe_of(&key);
        let Some(name) = shell_type_name(&probe, attributes) else {
            return String::new();
        };
        let name = match &key {
            TypeKey::Extension(extension) => readable_type_name(name, extension),
            TypeKey::Folder | TypeKey::NoExtension => name,
        };
        lock(&self.type_names).insert(key, name.clone());
        name
    }

    /// The `path:` key of `entry` in `folder`, remembered with its path.
    fn path_key(&self, folder: &str, entry: &EntryView) -> String {
        let path = Path::new(folder)
            .join(&entry.name)
            .to_string_lossy()
            .into_owned();
        let mut hash = Fnv::new();
        for unit in path.to_lowercase().encode_utf16() {
            hash.write(&unit.to_le_bytes());
        }
        hash.write(&entry.meta.modified.to_le_bytes());
        hash.write(&entry.meta.size.to_le_bytes());
        let key = format!("path:{:016x}", hash.0);
        lock(&self.paths).insert(key.clone(), path);
        key
    }

    /// The icon of `key` as a PNG of `size` by `size` pixels (one of
    /// [`ICON_SIZES`]) with an alpha channel. An unknown key, or a `path:`
    /// key whose file is gone, is [`FsError::NotFound`]; a failure of the
    /// shell is [`FsError::Io`] with its message. Blocking.
    pub fn icon_png(&self, key: &str, size: u32) -> Result<Arc<[u8]>, FsError> {
        if !ICON_SIZES.contains(&size) {
            return Err(FsError::InvalidPath {
                path: key.to_owned(),
                reason: format!("icons come in 16, 24, 32 or 48 pixels, not {size}"),
            });
        }
        let cached = (key.to_owned(), size);
        if let Some(png) = lock(&self.icons).get(&cached) {
            return Ok(png);
        }
        let source = self.icon_source(key)?;
        let failed = |message: String| FsError::Io {
            path: key.to_owned(),
            source: io::Error::other(message),
        };
        let png: Arc<[u8]> = {
            let _one_at_a_time = lock(&DRAWING);
            let rgba = draw_icon(&source, size).map_err(failed)?;
            encode_png(&rgba, size).map_err(failed)?.into()
        };
        lock(&self.icons).insert(cached, Arc::clone(&png));
        Ok(png)
    }

    fn icon_source(&self, key: &str) -> Result<IconSource, FsError> {
        let unknown = || FsError::NotFound {
            path: key.to_owned(),
        };
        let kind = |probe: &str, attributes| IconSource::Kind {
            probe: probe.to_owned(),
            attributes,
        };
        match key {
            "folder" => Ok(kind("folder", FILE_ATTRIBUTE_DIRECTORY)),
            "generic" => Ok(kind("file", FILE_ATTRIBUTE_NORMAL)),
            _ => {
                if let Some(extension) = key.strip_prefix("ext:") {
                    return if is_extension_key(extension) {
                        Ok(kind(&format!("file{extension}"), FILE_ATTRIBUTE_NORMAL))
                    } else {
                        Err(unknown())
                    };
                }
                if key.starts_with("path:") {
                    let path = lock(&self.paths).get(&key.to_owned()).ok_or_else(unknown)?;
                    return if Path::new(&path).exists() {
                        Ok(IconSource::File(path))
                    } else {
                        Err(unknown())
                    };
                }
                Err(unknown())
            }
        }
    }
}

/// The extension of `name`, lower case with its dot: from the last dot on,
/// as Windows sees it (`.gitignore` has one). `None` when there is no dot
/// or nothing after it.
fn extension(name: &str) -> Option<String> {
    let dot = name.rfind('.')?;
    (dot + 1 < name.len()).then(|| name[dot..].to_lowercase())
}

/// `name`, unless it looks like a program identifier: no space, and either
/// no capital letter or ending in `file` (`txtfile`, `Textfile`). Then
/// `{EXT} File`, as Explorer names a type nobody named, such as
/// `GITATTRIBUTES File`.
fn readable_type_name(name: String, extension: &str) -> String {
    let progid = !name.contains(char::is_whitespace)
        && (!name.chars().any(char::is_uppercase) || name.to_lowercase().ends_with("file"));
    if progid {
        format!("{} File", extension.trim_start_matches('.').to_uppercase())
    } else {
        name
    }
}

/// Whether `extension` (after `ext:`) is one `describe` could have made.
fn is_extension_key(extension: &str) -> bool {
    extension.len() > 1
        && extension.starts_with('.')
        && extension == extension.to_lowercase()
        && !extension.contains(['\\', '/', ':', '\0'])
}

/// A made-up name and attributes that give the shell the type of `key`.
fn probe_of(key: &TypeKey) -> (String, FILE_FLAGS_AND_ATTRIBUTES) {
    match key {
        TypeKey::Folder => ("folder".to_owned(), FILE_ATTRIBUTE_DIRECTORY),
        TypeKey::NoExtension => ("file".to_owned(), FILE_ATTRIBUTE_NORMAL),
        TypeKey::Extension(extension) => (format!("file{extension}"), FILE_ATTRIBUTE_NORMAL),
    }
}

/// The shell's type name for `probe` with `attributes`, by extension only.
fn shell_type_name(probe: &str, attributes: FILE_FLAGS_AND_ATTRIBUTES) -> Option<String> {
    let wide: Vec<u16> = probe.encode_utf16().chain([0]).collect();
    let mut info = SHFILEINFOW::default();
    // SAFETY: `wide` is NUL-terminated and `info` a valid local, both
    // outliving the call; the size passed is the structure's. With
    // SHGFI_USEFILEATTRIBUTES the path is not opened.
    let done = unsafe {
        SHGetFileInfoW(
            PCWSTR(wide.as_ptr()),
            attributes,
            Some(&raw mut info),
            size_of_u32::<SHFILEINFOW>(),
            SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES,
        )
    };
    (done != 0)
        .then(|| from_wide(&info.szTypeName))
        .filter(|name| !name.is_empty())
}

/// The icon of `source`, `size` by `size` pixels, as RGBA bytes.
fn draw_icon(source: &IconSource, size: u32) -> Result<Vec<u8>, String> {
    let list_id = match size {
        16 => SHIL_SMALL,
        32 => SHIL_LARGE,
        _ => SHIL_EXTRALARGE,
    };
    let _apartment = Apartment::enter();
    let index = system_icon_index(source)?;
    // SAFETY: COM is ready on this thread; the list is the process's own
    // system image list and is released before the apartment is left.
    let list: IImageList =
        unsafe { SHGetImageList(i32::try_from(list_id).unwrap_or(0)) }.map_err(|error| {
            format!(
                "the shell's image list is not available: {}",
                error.message()
            )
        })?;
    // SAFETY: `index` came from the shell for this list's icons.
    let icon = unsafe { list.GetIcon(index, ILD_TRANSPARENT.0) }
        .map_err(|error| format!("the shell cannot draw the icon: {}", error.message()))?;
    // SAFETY: `icon` is a new icon this function owns.
    let pixels = unsafe { icon_rgba(icon) };
    // SAFETY: `icon` is not used after this.
    let _ = unsafe { DestroyIcon(icon) };
    let (width, height, rgba) = pixels?;
    Ok(resize(&rgba, width, height, size))
}

/// The index of `source`'s icon in the system image lists.
fn system_icon_index(source: &IconSource) -> Result<i32, String> {
    let (path, attributes, flags) = match source {
        IconSource::Kind { probe, attributes } => (
            probe.as_str(),
            *attributes,
            SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES,
        ),
        IconSource::File(path) => (
            path.as_str(),
            FILE_FLAGS_AND_ATTRIBUTES(0),
            SHGFI_SYSICONINDEX,
        ),
    };
    let wide: Vec<u16> = path.encode_utf16().chain([0]).collect();
    let mut info = SHFILEINFOW::default();
    // SAFETY: as in `shell_type_name`; without SHGFI_USEFILEATTRIBUTES the
    // shell reads the file for its icon.
    let list = unsafe {
        SHGetFileInfoW(
            PCWSTR(wide.as_ptr()),
            attributes,
            Some(&raw mut info),
            size_of_u32::<SHFILEINFOW>(),
            flags,
        )
    };
    if list == 0 {
        return Err("the shell has no icon for it".to_owned());
    }
    Ok(info.iIcon)
}

/// The pixels of `icon` as RGBA with straight alpha, and its size. An icon
/// without an alpha channel gets one from its mask.
///
/// # Safety
///
/// `icon` must be a valid icon handle for the duration of the call.
unsafe fn icon_rgba(icon: HICON) -> Result<(u32, u32, Vec<u8>), String> {
    let mut info = ICONINFO::default();
    // SAFETY: `icon` is valid (the caller's promise); `info` is a valid
    // local. The call creates two bitmaps, deleted by the guard below.
    unsafe { GetIconInfo(icon, &raw mut info) }
        .map_err(|error| format!("cannot read the icon: {}", error.message()))?;
    let _bitmaps = Bitmaps([info.hbmColor, info.hbmMask]);
    if info.hbmColor.is_invalid() {
        return Err("the icon is black and white only".to_owned());
    }
    let mut bitmap = BITMAP::default();
    // SAFETY: the handle is the icon's color bitmap; `bitmap` is a local of
    // the size passed.
    let filled = unsafe {
        GetObjectW(
            HGDIOBJ(info.hbmColor.0),
            i32::try_from(size_of::<BITMAP>()).unwrap_or(0),
            Some((&raw mut bitmap).cast()),
        )
    };
    let (Ok(width), Ok(height)) = (
        u32::try_from(bitmap.bmWidth),
        u32::try_from(bitmap.bmHeight),
    ) else {
        return Err("the icon's bitmap has no size".to_owned());
    };
    if filled == 0 || width == 0 || height == 0 {
        return Err("the icon's bitmap has no size".to_owned());
    }
    let dc = MemoryDc::new()?;
    let mut bgra = read_bits(dc.0, info.hbmColor, width, height)?;
    if bgra.as_chunks::<4>().0.iter().all(|pixel| pixel[3] == 0) {
        // An old icon without alpha: its mask is black where it shows.
        let mask = read_bits(dc.0, info.hbmMask, width, height)?;
        for (pixel, masked) in bgra
            .as_chunks_mut::<4>()
            .0
            .iter_mut()
            .zip(mask.as_chunks::<4>().0)
        {
            pixel[3] = if masked[0] == 0 { 255 } else { 0 };
        }
    }
    for pixel in bgra.as_chunks_mut::<4>().0 {
        pixel.swap(0, 2);
    }
    Ok((width, height, bgra))
}

/// The top `height` rows of `bitmap`, `width` pixels each, as 32-bit BGRA.
fn read_bits(dc: HDC, bitmap: HBITMAP, width: u32, height: u32) -> Result<Vec<u8>, String> {
    let (Ok(signed_width), Ok(signed_height)) = (i32::try_from(width), i32::try_from(height))
    else {
        return Err("the icon is too large".to_owned());
    };
    let mut header = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of_u32::<BITMAPINFOHEADER>(),
            biWidth: signed_width,
            // Negative: the rows come top down.
            biHeight: -signed_height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..BITMAPINFOHEADER::default()
        },
        ..BITMAPINFO::default()
    };
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    // SAFETY: `dc` and `bitmap` are valid; `pixels` holds `height` rows of
    // `width` 32-bit pixels, which is what `header` asks for; the bitmap is
    // not selected into any device context.
    let lines = unsafe {
        GetDIBits(
            dc,
            bitmap,
            0,
            height,
            Some(pixels.as_mut_ptr().cast()),
            &raw mut header,
            DIB_RGB_COLORS,
        )
    };
    if lines != signed_height {
        return Err("cannot read the icon's pixels".to_owned());
    }
    Ok(pixels)
}

/// `rgba` of `width` by `height` pixels scaled to `size` by `size`: each
/// pixel is the average of the pixels it covers, weighted by alpha, so
/// halving a 48-pixel icon gives a sharp 24-pixel one.
fn resize(rgba: &[u8], width: u32, height: u32, size: u32) -> Vec<u8> {
    if width == size && height == size {
        return rgba.to_vec();
    }
    let (width, height, size) = (width as usize, height as usize, size as usize);
    let mut out = vec![0u8; size * size * 4];
    for ty in 0..size {
        let (y0, y1) = (
            ty * height / size,
            ((ty + 1) * height / size).max(ty * height / size + 1),
        );
        for tx in 0..size {
            let (x0, x1) = (
                tx * width / size,
                ((tx + 1) * width / size).max(tx * width / size + 1),
            );
            let (mut color, mut alpha, mut count) = ([0u64; 3], 0u64, 0u64);
            for y in y0..y1.min(height) {
                for x in x0..x1.min(width) {
                    let pixel = &rgba[(y * width + x) * 4..][..4];
                    let a = u64::from(pixel[3]);
                    for (sum, channel) in color.iter_mut().zip(pixel) {
                        *sum += u64::from(*channel) * a;
                    }
                    alpha += a;
                    count += 1;
                }
            }
            let target = &mut out[(ty * size + tx) * 4..][..4];
            for (channel, sum) in target.iter_mut().zip(color) {
                // A clear pixel keeps its zeros.
                *channel = sum
                    .checked_div(alpha)
                    .map_or(0, |value| u8::try_from(value).unwrap_or(u8::MAX));
            }
            target[3] = u8::try_from(alpha / count.max(1)).unwrap_or(u8::MAX);
        }
    }
    out
}

/// `rgba`, `size` by `size` pixels, as a PNG.
fn encode_png(rgba: &[u8], size: u32) -> Result<Vec<u8>, String> {
    let mut png = Vec::new();
    let mut encoder = png::Encoder::new(&mut png, size, size);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
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

/// Deletes an icon's two bitmaps when dropped.
struct Bitmaps([HBITMAP; 2]);

impl Drop for Bitmaps {
    fn drop(&mut self) {
        for bitmap in self.0 {
            if !bitmap.is_invalid() {
                // SAFETY: GetIconInfo made the bitmap for this caller, and
                // nothing uses it after this.
                let _ = unsafe { DeleteObject(HGDIOBJ(bitmap.0)) };
            }
        }
    }
}

/// A memory device context, deleted when dropped.
struct MemoryDc(HDC);

impl MemoryDc {
    fn new() -> Result<Self, String> {
        // SAFETY: a plain call; the context is deleted in `drop`.
        let dc = unsafe { CreateCompatibleDC(None) };
        if dc.is_invalid() {
            return Err("cannot make a device context".to_owned());
        }
        Ok(Self(dc))
    }
}

impl Drop for MemoryDc {
    fn drop(&mut self) {
        // SAFETY: the context was made by `new` and nothing uses it after.
        let _ = unsafe { DeleteDC(self.0) };
    }
}

/// FNV-1a, 64 bits: small, fast and the same in every build.
struct Fnv(u64);

impl Fnv {
    fn new() -> Self {
        Self(0xcbf2_9ce4_8422_2325)
    }

    fn write(&mut self, bytes: &[u8]) {
        for byte in bytes {
            self.0 ^= u64::from(*byte);
            self.0 = self.0.wrapping_mul(0x0100_0000_01b3);
        }
    }
}

/// A map that forgets its oldest entries beyond a number.
#[derive(Debug)]
struct Bounded<K, V> {
    map: HashMap<K, V>,
    order: VecDeque<K>,
    capacity: usize,
}

impl<K: Clone + Eq + Hash, V: Clone> Bounded<K, V> {
    fn new(capacity: usize) -> Self {
        Self {
            map: HashMap::new(),
            order: VecDeque::new(),
            capacity,
        }
    }

    fn get(&self, key: &K) -> Option<V> {
        self.map.get(key).cloned()
    }

    fn insert(&mut self, key: K, value: V) {
        if self.map.insert(key.clone(), value).is_none() {
            self.order.push_back(key);
        }
        while self.order.len() > self.capacity {
            if let Some(oldest) = self.order.pop_front() {
                self.map.remove(&oldest);
            }
        }
    }

    #[cfg(test)]
    fn len(&self) -> usize {
        self.map.len()
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn size_of_u32<T>() -> u32 {
    u32::try_from(size_of::<T>()).unwrap_or(u32::MAX)
}

/// A NUL-terminated UTF-16 buffer as a string.
fn from_wide(buffer: &[u16]) -> String {
    let end = buffer
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end])
}

#[cfg(test)]
mod tests {
    use std::fs;

    use windows::Win32::Globalization::GetUserDefaultUILanguage;

    use super::*;
    use crate::{ListOptions, ListingWriter, list_directory};

    fn scratch() -> tempfile::TempDir {
        let root = std::env::temp_dir().join("cabinetos-fs-test");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("hydrate")
            .tempdir_in(root)
            .unwrap()
    }

    /// A folder with a text file, a program, a folder and a file without
    /// an extension, as a listing section (sorted: the folder first).
    fn section_of(dir: &Path) -> Vec<u8> {
        fs::write(dir.join("notes.txt"), "text").unwrap();
        fs::copy(std::env::current_exe().unwrap(), dir.join("tool.exe")).unwrap();
        fs::create_dir(dir.join("sub")).unwrap();
        fs::write(dir.join("README"), "read me").unwrap();
        let listing = list_directory(dir.to_str().unwrap(), &ListOptions::default()).unwrap();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 3).unwrap();
        section
    }

    fn is_english() -> bool {
        // SAFETY: a plain call.
        let language = unsafe { GetUserDefaultUILanguage() };
        language & 0x3ff == 0x09
    }

    #[test]
    fn entries_get_type_names_and_icon_keys() {
        let dir = scratch();
        let section = section_of(dir.path());
        let hydrator = Hydrator::new();
        let (generation, details) = hydrator
            .describe(&section, dir.path().to_str().unwrap(), 0, 10)
            .unwrap();
        assert_eq!(generation, 3);
        let keys: Vec<&str> = details.iter().map(|d| d.icon_key.as_str()).collect();
        // sub, notes.txt, README, tool.exe in the listing's order.
        assert_eq!(keys[..3], ["folder", "ext:.txt", "generic"]);
        assert!(
            keys[3].starts_with("path:") && keys[3].len() == 5 + 16,
            "{keys:?}"
        );
        let names: Vec<&str> = details.iter().map(|d| d.type_name.as_str()).collect();
        for (index, name) in names.iter().enumerate() {
            assert!(!name.is_empty(), "{index}: {names:?}");
            assert!(!names[..index].contains(name), "{names:?}");
        }
        if is_english() {
            assert_eq!(
                names,
                ["File folder", "Text Document", "File", "Application"]
            );
        }
        // Past the end: nothing; in the middle: from there.
        let (_, none) = hydrator.describe(&section, "x", 9, 5).unwrap();
        assert!(none.is_empty());
        let (_, tail) = hydrator
            .describe(&section, dir.path().to_str().unwrap(), 2, 5)
            .unwrap();
        assert_eq!(tail.len(), 2);
        assert_eq!(tail[0].icon_key, "generic");
    }

    #[test]
    fn names_beyond_ascii_get_type_names_and_icons() {
        let dir = scratch();
        fs::create_dir(dir.path().join("Ґанок")).unwrap();
        for name in [
            "Звіт 2026.txt",
            "Їжак і Єнот.md",
            "日本語のファイル.txt",
            "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
            "cafe\u{301}.txt",
            "مستند.txt",
            "Документ.ТХТ",
            "📁 без розширення",
        ] {
            fs::write(dir.path().join(name), "x").unwrap();
        }
        fs::copy(
            std::env::current_exe().unwrap(),
            dir.path().join("Програма.exe"),
        )
        .unwrap();
        let folder = dir.path().to_str().unwrap();
        let listing = list_directory(folder, &ListOptions::default()).unwrap();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 1).unwrap();
        let hydrator = Hydrator::new();
        let (_, details) = hydrator.describe(&section, folder, 0, 20).unwrap();
        assert_eq!(details.len(), listing.len());
        let detail = |name: &str| {
            let index = listing
                .entries()
                .iter()
                .position(|entry| listing.name_string(entry) == name)
                .unwrap_or_else(|| panic!("{name} is not listed"));
            &details[index]
        };

        assert_eq!(detail("Ґанок").icon_key, "folder");
        for text in [
            "Звіт 2026.txt",
            "日本語のファイル.txt",
            "𝔘𝔫𝔦𝔠𝔬𝔡𝔢.txt",
            "cafe\u{301}.txt",
            "مستند.txt",
        ] {
            assert_eq!(detail(text).icon_key, "ext:.txt", "{text}");
            assert_eq!(detail(text).type_name, detail("Звіт 2026.txt").type_name);
        }
        assert_eq!(detail("Їжак і Єнот.md").icon_key, "ext:.md");
        // An extension beyond ASCII is lowercased like any other.
        assert_eq!(detail("Документ.ТХТ").icon_key, "ext:.тхт");
        assert_eq!(detail("📁 без розширення").icon_key, "generic");
        let program = &detail("Програма.exe").icon_key;
        assert!(program.starts_with("path:"), "{program}");

        for entry in &details {
            assert!(!entry.type_name.is_empty(), "{entry:?}");
            let png = hydrator.icon_png(&entry.icon_key, 16).unwrap();
            assert!(!png.is_empty(), "{entry:?}");
        }
        if is_english() {
            assert_eq!(detail("Документ.ТХТ").type_name, "ТХТ File");
        }
    }

    #[test]
    fn a_folder_deeper_than_260_characters_gets_type_names_and_icons() {
        let dir = scratch();
        let mut deep = dir.path().to_path_buf();
        while deep.as_os_str().len() < 300 {
            deep.push("segment-of-a-long-path-0123456789");
        }
        let verbatim = format!(r"\\?\{}", deep.display());
        fs::create_dir_all(&verbatim).unwrap();
        fs::write(format!(r"{verbatim}\notes.txt"), "text").unwrap();
        fs::copy(
            std::env::current_exe().unwrap(),
            format!(r"{verbatim}\tool.exe"),
        )
        .unwrap();
        fs::create_dir(format!(r"{verbatim}\sub")).unwrap();
        let folder = deep.to_str().unwrap();
        let listing = list_directory(folder, &ListOptions::default()).unwrap();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 1).unwrap();
        let hydrator = Hydrator::new();
        let (_, details) = hydrator.describe(&section, folder, 0, 10).unwrap();
        let keys: Vec<&str> = details.iter().map(|d| d.icon_key.as_str()).collect();
        assert_eq!(keys[..2], ["folder", "ext:.txt"], "{keys:?}");
        assert_eq!(details.len(), 3);
        for detail in &details {
            assert!(!detail.type_name.is_empty(), "{detail:?}");
            for size in [16, 32] {
                let png = hydrator
                    .icon_png(&detail.icon_key, size)
                    .unwrap_or_else(|error| panic!("{detail:?} at {size}: {error}"));
                assert!(!png.is_empty());
            }
        }
    }

    #[test]
    fn a_progid_is_not_shown_as_a_type_name() {
        let readable = |name: &str, extension: &str| readable_type_name(name.to_owned(), extension);
        for (name, extension, shown) in [
            ("txtfile", ".gitattributes", "GITATTRIBUTES File"),
            (
                "gitattributes_auto_file",
                ".gitattributes",
                "GITATTRIBUTES File",
            ),
            ("Textfile", ".cfg", "CFG File"),
            ("vlc.mp4", ".mp4", "MP4 File"),
            ("Text Document", ".txt", "Text Document"),
            ("Markdown Source File", ".md", "Markdown Source File"),
            ("EDITORCONFIG File", ".editorconfig", "EDITORCONFIG File"),
            ("Application", ".exe", "Application"),
            ("Textdokument", ".txt", "Textdokument"),
        ] {
            assert_eq!(readable(name, extension), shown, "{name}");
        }
    }

    #[test]
    fn a_gitattributes_file_gets_a_readable_type_name() {
        let dir = scratch();
        fs::write(dir.path().join(".gitattributes"), "* text=auto").unwrap();
        let listing =
            list_directory(dir.path().to_str().unwrap(), &ListOptions::default()).unwrap();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 1).unwrap();
        let (_, details) = Hydrator::new()
            .describe(&section, dir.path().to_str().unwrap(), 0, 1)
            .unwrap();
        let name = &details[0].type_name;
        // The shell may know a friendly name, know none (Windows then says
        // "GITATTRIBUTES File" itself), or answer a ProgID such as
        // `txtfile` (as where Git registered it); never the ProgID.
        assert!(name.contains(' '), "{name}");
        assert_ne!(name, "txtfile");
    }

    #[test]
    fn a_changed_program_gets_a_new_key() {
        let dir = scratch();
        let hydrator = Hydrator::new();
        let folder = dir.path().to_str().unwrap();
        let key = |section: &[u8]| {
            let (_, details) = hydrator.describe(section, folder, 0, 10).unwrap();
            details.last().unwrap().icon_key.clone()
        };
        let before = key(&section_of(dir.path()));
        fs::write(dir.path().join("tool.exe"), "not a program any more").unwrap();
        let listing = list_directory(folder, &ListOptions::default()).unwrap();
        let writer = ListingWriter::new(&listing).unwrap();
        let mut section = vec![0u8; writer.section_size()];
        writer.write(&mut section, 4).unwrap();
        let after = key(&section);
        assert_ne!(before, after);
        assert!(after.starts_with("path:"));
    }

    /// Decodes a PNG: its width, height and RGBA pixels.
    fn decode(png: &[u8]) -> (u32, u32, Vec<u8>) {
        let decoder = png::Decoder::new(std::io::Cursor::new(png));
        let mut reader = decoder.read_info().unwrap();
        let mut pixels = vec![0u8; reader.output_buffer_size().unwrap()];
        let frame = reader.next_frame(&mut pixels).unwrap();
        assert_eq!(frame.color_type, png::ColorType::Rgba);
        assert_eq!(frame.bit_depth, png::BitDepth::Eight);
        pixels.truncate(frame.buffer_size());
        (frame.width, frame.height, pixels)
    }

    #[test]
    fn icons_are_pngs_of_the_size_asked_with_alpha() {
        let dir = scratch();
        let section = section_of(dir.path());
        let hydrator = Hydrator::new();
        let (_, details) = hydrator
            .describe(&section, dir.path().to_str().unwrap(), 0, 10)
            .unwrap();
        for detail in &details {
            for size in ICON_SIZES {
                let png = hydrator.icon_png(&detail.icon_key, size).unwrap();
                let (width, height, pixels) = decode(&png);
                assert_eq!(
                    (width, height),
                    (size, size),
                    "{} at {size}",
                    detail.icon_key
                );
                let alpha: Vec<u8> = pixels
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .map(|pixel| pixel[3])
                    .collect();
                assert!(
                    alpha.iter().any(|&a| a > 0),
                    "{} at {size}: all clear",
                    detail.icon_key
                );
                assert!(
                    alpha.iter().any(|&a| a < 255),
                    "{} at {size}: no transparency",
                    detail.icon_key
                );
            }
        }
        // Asked again, the same bytes come from the cache.
        let first = hydrator.icon_png("ext:.txt", 32).unwrap();
        let again = hydrator.icon_png("ext:.txt", 32).unwrap();
        assert!(Arc::ptr_eq(&first, &again));
    }

    #[test]
    fn unknown_keys_and_sizes_are_refused() {
        let hydrator = Hydrator::new();
        for key in [
            "",
            "nope",
            "ext:",
            "ext:txt",
            "ext:.TXT",
            "ext:.a/b",
            "path:0123456789abcdef",
        ] {
            let error = hydrator.icon_png(key, 32).unwrap_err();
            assert!(
                matches!(error, FsError::NotFound { .. }),
                "{key:?}: {error:?}"
            );
        }
        let error = hydrator.icon_png("folder", 20).unwrap_err();
        assert!(matches!(error, FsError::InvalidPath { .. }), "{error:?}");
    }

    #[test]
    fn a_program_that_is_gone_has_no_icon() {
        let dir = scratch();
        let section = section_of(dir.path());
        let hydrator = Hydrator::new();
        let (_, details) = hydrator
            .describe(&section, dir.path().to_str().unwrap(), 0, 10)
            .unwrap();
        let key = &details[3].icon_key;
        fs::remove_file(dir.path().join("tool.exe")).unwrap();
        let error = hydrator.icon_png(key, 16).unwrap_err();
        assert!(matches!(error, FsError::NotFound { .. }), "{error:?}");
    }

    #[test]
    fn the_caches_forget_their_oldest_entries() {
        let mut cache = Bounded::new(3);
        for n in 0..5 {
            cache.insert(n, n * 10);
        }
        assert_eq!(cache.len(), 3);
        assert_eq!(cache.get(&0), None);
        assert_eq!(cache.get(&1), None);
        assert_eq!(cache.get(&4), Some(40));
        // Replacing a value keeps one entry.
        cache.insert(4, 41);
        assert_eq!((cache.len(), cache.get(&4)), (3, Some(41)));
        assert_eq!(ICONS_KEPT, 2_000);
    }

    #[test]
    fn extensions_are_what_follows_the_last_dot() {
        assert_eq!(extension("a.TXT").as_deref(), Some(".txt"));
        assert_eq!(extension("archive.tar.gz").as_deref(), Some(".gz"));
        assert_eq!(extension(".gitignore").as_deref(), Some(".gitignore"));
        assert_eq!(extension("README"), None);
        assert_eq!(extension("trailing."), None);
    }

    #[test]
    fn halving_averages_by_alpha() {
        // 2x2 → 1x1: one opaque red pixel and three clear ones.
        let rgba = [255, 0, 0, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0];
        assert_eq!(resize(&rgba, 2, 2, 1), [255, 0, 0, 63]);
        let clear = [0u8; 16];
        assert_eq!(resize(&clear, 2, 2, 1), [0, 0, 0, 0]);
    }
}
