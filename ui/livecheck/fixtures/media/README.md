# Media fixtures

One tiny file of each kind the Media Viewer of Quick View claims
(`sdk/tools/media-viewer`), 1.5 s long and silent (so a run on a machine
somebody sits at makes no noise), for `ui/livecheck/media-probe.ps1` (which
kinds does this machine's Edge engine play?) and for the live check's Quick
View section, which opens each in the real panel. `hevc.mp4` is only for the
report: HEVC is a codec the browser may lack, not a kind. Two files are for
the timing: `clip-1080p.mp4` (the ADR 0023 goal, "a video playing within
1,000 ms": H.264 1080p, 24 fps, 3 s, with a silent AAC track, 455 KB) and
`silence.mp3` (3 s).

Made once with ffmpeg 8 (no metadata and bit-exact flags, so a rebuild gives
the same bytes), 596 KB in all:

```text
video: -f lavfi -i testsrc2=size=160x120:rate=10 -f lavfi -i anullsrc=r=16000:cl=mono -t 1.5 -shortest
  sample.mp4 / .m4v / .mov / .mkv   -c:v libx264 -crf 35 -pix_fmt yuv420p -c:a aac -b:a 24k   (-f mp4 for .m4v, -f mov for .mov)
  sample.webm                        -c:v libvpx-vp9 -crf 40 -b:v 0 -c:a libopus -b:a 24k
  hevc.mp4                           -c:v libx265 -crf 35 -pix_fmt yuv420p -tag:v hvc1 -c:a aac -b:a 24k
audio: -f lavfi -i anullsrc=r=16000:cl=mono -t 1.5
  sample.mp3 libmp3lame 24k | .m4a aac -f ipod | .aac aac -f adts | .flac | .wav pcm_s16le mono
  sample.ogg libvorbis -q:a 0 | .opus libopus 24k | .weba libopus -f webm
clip-1080p.mp4: -f lavfi -i testsrc2=size=1920x1080:rate=24 -f lavfi -i anullsrc=r=44100:cl=stereo -t 3 -shortest
  -c:v libx264 -crf 34 -preset slow -pix_fmt yuv420p -c:a aac -b:a 32k
silence.mp3: -f lavfi -i anullsrc=r=44100:cl=stereo -t 3 -c:a libmp3lame -b:a 32k
```
