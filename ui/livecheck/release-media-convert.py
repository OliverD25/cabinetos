"""Turns the raw media of release-media.ps1 into the files a release page uses.

Every <name>.png becomes <name>.webp, 1600 pixels wide (never enlarged), about 300 KB at most.
Every <name>.raw.mp4 becomes <name>.webm: VP9, 1280 pixels wide, no sound, under 3 MB. A GIF is made only when
the VP9 encode fails. Both limits are the ones of .claude/skills/release-notes-page/SKILL.md.

It runs on the PC, after the raw files came back from the laptop. It needs Pillow (python -m pip install pillow)
and ffmpeg (C:\\ffmpeg\\bin\\ffmpeg.exe, or --ffmpeg, or the one on the PATH).

    python ui\\livecheck\\release-media-convert.py --in <folder of the raw files> --out <folder for the page's media>

It reads only the top level of --in, writes only the converted files into --out, and never changes the raw files.
Exit code 0 when every file was converted (a size above its limit is a warning, not a failure), 1 otherwise.
"""
import argparse
import pathlib
import re
import shutil
import subprocess
import sys

from PIL import Image

IMAGE_WIDTH = 1600
IMAGE_LIMIT = 300 * 1024
# Screenshots of a window are flat colours and text: these steps shrink them a long way before the picture suffers.
IMAGE_QUALITIES = (85, 78, 70, 62)
VIDEO_WIDTH = 1280
VIDEO_LIMIT = 3 * 1024 * 1024
VIDEO_CRFS = (33, 40)


def kb(size: int) -> str:
    return f"{size / 1024:,.0f} KB"


def find_ffmpeg(given: str) -> str | None:
    if given:
        return given if pathlib.Path(given).exists() else None
    default = pathlib.Path(r"C:\ffmpeg\bin\ffmpeg.exe")
    if default.exists():
        return str(default)
    return shutil.which("ffmpeg")


def run(command: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run(command, capture_output=True, text=True, errors="replace")


def probe(ffmpeg: str, path: pathlib.Path) -> tuple[float, str]:
    """The length in seconds and the picture size of a video, read from what ffmpeg prints about it."""
    text = run([ffmpeg, "-hide_banner", "-i", str(path)]).stderr
    seconds = 0.0
    if m := re.search(r"Duration: (\d+):(\d+):([\d.]+)", text):
        seconds = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
    size = ""
    if m := re.search(r"Video: .*?, (\d{2,5})x(\d{2,5})[ ,\[]", text):
        size = f"{m.group(1)}x{m.group(2)}"
    return seconds, size


def looks_black(ffmpeg: str, path: pathlib.Path, seconds: float) -> bool:
    """A recording of a window the GPU draws can come out black (gdigrab by window title): say so before anyone publishes it."""
    text = run([ffmpeg, "-hide_banner", "-nostats", "-i", str(path), "-vf", "blackdetect=d=0.5:pix_th=0.05", "-an", "-f", "null", "-"]).stderr
    black = sum(float(m) for m in re.findall(r"black_duration:([\d.]+)", text))
    return seconds > 0 and black >= seconds * 0.5


def convert_image(source: pathlib.Path, target: pathlib.Path) -> list[str]:
    warnings: list[str] = []
    with Image.open(source) as opened:
        picture = opened.convert("RGB")
    if picture.width > IMAGE_WIDTH:
        picture = picture.resize((IMAGE_WIDTH, round(picture.height * IMAGE_WIDTH / picture.width)), Image.LANCZOS)
    elif picture.width < IMAGE_WIDTH:
        warnings.append(f"{source.name} is only {picture.width} pixels wide; it is not enlarged")
    quality = IMAGE_QUALITIES[0]
    for quality in IMAGE_QUALITIES:
        picture.save(target, "WEBP", quality=quality, method=6)
        if target.stat().st_size <= IMAGE_LIMIT:
            break
    size = target.stat().st_size
    if size > IMAGE_LIMIT:
        warnings.append(f"{target.name} is {kb(size)}, over the {kb(IMAGE_LIMIT)} limit, even at quality {quality}")
    print(f"image  {target.name}  {picture.width}x{picture.height}  quality {quality}  {kb(size)}")
    return warnings


def encode_webm(ffmpeg: str, source: pathlib.Path, target: pathlib.Path, crf: int) -> bool:
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(source),
        # min() so a recording narrower than 1280 is not enlarged; -2 keeps the height even.
        "-vf", f"scale='min({VIDEO_WIDTH},iw)':-2",
        "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", str(crf), "-row-mt", "1", "-cpu-used", "2",
        "-pix_fmt", "yuv420p", "-an", str(target),
    ]
    result = run(command)
    if result.returncode != 0 or not target.exists():
        print(f"ffmpeg failed for {source.name}: {result.stderr.strip()[-300:]}")
        return False
    return True


def encode_gif(ffmpeg: str, source: pathlib.Path, target: pathlib.Path) -> bool:
    graph = "fps=12,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse"
    result = run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(source), "-vf", graph, "-loop", "0", str(target)])
    return result.returncode == 0 and target.exists()


def convert_video(ffmpeg: str, source: pathlib.Path, target: pathlib.Path) -> tuple[list[str], bool]:
    warnings: list[str] = []
    seconds, _ = probe(ffmpeg, source)
    if looks_black(ffmpeg, source, seconds):
        warnings.append(f"{source.name} looks black: record again with the other -Capture mode")
    crf = VIDEO_CRFS[0]
    ok = False
    for crf in VIDEO_CRFS:
        ok = encode_webm(ffmpeg, source, target, crf)
        if not ok or target.stat().st_size <= VIDEO_LIMIT:
            break
        print(f"{target.name} is {kb(target.stat().st_size)} at crf {crf}; trying again with a higher crf")
    if ok:
        size = target.stat().st_size
        length, picture = probe(ffmpeg, target)
        if size > VIDEO_LIMIT:
            warnings.append(f"{target.name} is {kb(size)}, over the {kb(VIDEO_LIMIT)} limit, even at crf {crf}")
        print(f"video  {target.name}  {picture}  {length:.1f} s  crf {crf}  {kb(size)}")
        return warnings, True
    gif = target.with_suffix(".gif")
    if encode_gif(ffmpeg, source, gif):
        warnings.append(f"{target.name} could not be made; {gif.name} was made instead ({kb(gif.stat().st_size)}). Use a GIF only as a last resort")
        print(f"video  {gif.name}  gif fallback  {kb(gif.stat().st_size)}")
        return warnings, True
    return warnings, False


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert the raw release media to WebP and WebM.")
    parser.add_argument("--in", dest="source", required=True, help="the folder with the .png and .raw.mp4 files")
    parser.add_argument("--out", dest="target", required=True, help="the folder for the .webp and .webm files")
    parser.add_argument("--ffmpeg", default="", help="the path of ffmpeg.exe (default: C:\\ffmpeg\\bin\\ffmpeg.exe, else the PATH)")
    args = parser.parse_args()

    source = pathlib.Path(args.source)
    target = pathlib.Path(args.target)
    if not source.is_dir():
        print(f"STOP: {source} is not a folder")
        return 1
    images = sorted(source.glob("*.png"))
    videos = sorted(source.glob("*.raw.mp4"))
    if not images and not videos:
        print(f"STOP: {source} has no .png and no .raw.mp4 file")
        return 1
    ffmpeg = find_ffmpeg(args.ffmpeg)
    if videos and not ffmpeg:
        print("STOP: ffmpeg was not found (give --ffmpeg)")
        return 1
    target.mkdir(parents=True, exist_ok=True)

    warnings: list[str] = []
    failed: list[str] = []
    for image in images:
        try:
            warnings += convert_image(image, target / f"{image.stem}.webp")
        except OSError as error:
            failed.append(f"{image.name}: {error}")
    for video in videos:
        name = video.name[: -len(".raw.mp4")]
        more, ok = convert_video(ffmpeg, video, target / f"{name}.webm")
        warnings += more
        if not ok:
            failed.append(video.name)

    for warning in warnings:
        print(f"WARN: {warning}")
    for failure in failed:
        print(f"FAILED: {failure}")
    total = sum(f.stat().st_size for f in target.iterdir() if f.suffix in (".webp", ".webm", ".gif"))
    print(f"done: {len(images)} image(s), {len(videos)} video(s) into {target}, {kb(total)} in all")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
