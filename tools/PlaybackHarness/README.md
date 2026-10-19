# Playback harness

Drives the real `AudioEngine` headlessly, with either a fake sink or the real sound device, and
reports whether the audio actually came out. Many playback bugs are in the
seam between the engine, the OS scheduler and OpenAL. A unit test cannot reach that seam. The app can,
but the app cannot tell you *why* it went quiet.

It compiles `AudioEngine.cs`, `OpenAlSink.cs` and `PipelineProbe.cs` from the app project directly,
unmodified. It is not in `RPM.sln`, so it never takes part in a normal build.

## Running

```bash
cd tools/PlaybackHarness

# make some material (any audio file works; sine waves keep it obvious)
ffmpeg -f lavfi -i "sine=frequency=440:duration=30" -ac 2 -ar 44100 a.wav
ffmpeg -f lavfi -i "sine=frequency=660:duration=30" -ac 2 -ar 44100 b.wav
ffmpeg -f lavfi -i "sine=frequency=440:duration=3"  -ac 2 -ar 44100 s1.wav

dotnet run -- a.wav b.wav                      # engine only, fake sink
dotnet run -- a.wav b.wav --real               # through the sound device (silent: volume 0)
dotnet run -- s1.wav s1.wav s1.wav --real --playlist   # end of track feeds the next, plus pause/resume
dotnet run -- a.wav b.wav --real --churn       # twelve track changes in a burst
dotnet run -- tiny.wav a.wav --real --edges    # track shorter than the pre-roll; pause during pre-roll
```

Every mode prints PASS or FAIL and exits non-zero on failure, so they can be chained.

## Reading the output

`pos=` is the engine's play position, taken from the sink's own frame count. It climbing in real time
is the whole point; standing still while a track is supposedly playing is the failure this catches.
`underruns=` is `OpenAlSink.Underruns`, the count of times the device ran out of audio. Each one is an
audible break, and the count should stay at zero.

This harness detects two known failures:

- **A later track plays nothing, position jumps to the end of the file.** `alSourceStop` leaves the
  source in `AL_STOPPED`, where OpenAL reports every queued buffer as already processed. Code that
  reclaims buffers before the source has been told to play therefore throws the audio away and
  runs through the file at memcpy speed. The first track of a session hides the fault, because its
  source is still `AL_INITIAL`.
- **Stutter under a busy visualiser.** `Thread.Priority` does nothing on macOS, so an audio thread
  that has not asked for a QoS class competes as background work and can be scheduled onto an
  efficiency core. See `AudioThread`.
