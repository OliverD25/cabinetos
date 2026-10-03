# Media fixtures

One tiny file of each kind the Media Viewer of Quick View claims
(`sdk/tools/media-viewer`), 1.5 s long, 188 KB in all, for
`ui/livecheck/media-probe.ps1` (which kinds does this machine's Edge engine
play?) and for the live check's Quick View section. `hevc.mp4` is only for the
probe's report: HEVC is a codec the browser may lack, not a kind.

Made once with ffmpeg 8 (no metadata, so a rebuild gives the same bytes):

```text
video: -f lavfi -i testsrc2=size=160x120:rate=10 -f lavfi -i sine=frequency=440:sample_rate=16000 -t 1.5 -shortest
  sample.mp4 / .m4v / .mov / .mkv   -c:v libx264 -crf 35 -pix_fmt yuv420p -c:a aac -b:a 24k   (-f mp4 for .m4v, -f mov for .mov)
  sample.webm                        -c:v libvpx-vp9 -crf 40 -b:v 0 -c:a libopus -b:a 24k
  hevc.mp4                           -c:v libx265 -crf 35 -pix_fmt yuv420p -tag:v hvc1 -c:a aac -b:a 24k
audio: -f lavfi -i sine=frequency=440:sample_rate=16000 -t 1.5
  sample.mp3 libmp3lame 24k | .m4a aac -f ipod | .aac aac -f adts | .flac | .wav pcm_s16le mono
  sample.ogg libvorbis -q:a 0 | .opus libopus 24k | .weba libopus -f webm
```
