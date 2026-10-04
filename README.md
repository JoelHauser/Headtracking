<p align="center"><img src="docs/icon.png" width="96" alt="Head Tracking icon"></p>

# Head Tracking for SPT

Look around in Escape from Tarkov (SPT) by turning your head, using an ordinary webcam. Turn your head
a little and the view turns further, so your eyes stay on the monitor. Your mouse still aims and turns
your body as normal: head tracking moves only the freelook view, the way holding the freelook key does.

It comes in two parts:

- **HeadTracking.exe**, a desktop app that sits in your SPT folder. It reads your webcam (or
  OpenTrack), works out where your head is pointing, and holds every setting, all adjustable live
  while you play.
- **A BepInEx plugin**, which applies the result to the freelook camera only when it is safe to
  (not in menus, not while aiming down sights) and reports back what the game is doing.

> **Status: 0.4.0.** Built for one complaint about 0.2.0 and 0.3.0: the view kept moving while the
> head held still. Most of that came from the tracker's own frame-to-frame noise, which 0.4.0 halves
> at the source and then locks out. On a still head the view now stays still, and it still follows
> real turns, now with less lag. Replayed against webcam-sized noise, the view moves 3x less at rest
> than in 0.3.0 (1.8 vs 5.2 °/s), with 40% less lag (63 vs 111 ms). Not yet played in raid in this
> form.

## Features

- **Built-in webcam tracking.** No OpenTrack, no markers, no extra hardware. Uses OpenTrack's
  open-source neural head-pose models, run locally with ONNX Runtime.
- **OpenTrack input** too, for anything OpenTrack supports (TrackIR-style IR clips, ArUco markers,
  phone apps, other cameras).
- **Freelook with your head.** Yaw and pitch, with sensitivity, dead zone, maximum angle, response
  curve and invert per axis.
- **A still head gives a still view.** A *stillness lock* holds the view exactly still until your
  head has moved further than the tracker's own noise. The lock measures that noise while it runs,
  so a noisier camera or a darker room is held still too. Real turns get through straight away,
  and tiny deliberate adjustments still arrive after a moment.
- **Quieter tracking at the source.** The tracker crops your face from a steadier box, and checks
  every frame a second time, mirrored, then averages the two. With the Balanced model, now the
  default, this cut the frame-to-frame noise on a Logitech C920 from 1.05° to 0.45°. The camera also
  runs at its full 30 fps; webcams halve their frame rate in dim light unless told not to.
- **One Feel slider** from *Snappy* to *Very smooth* sets all the smoothing at once. Every
  underlying setting is still there to fine-tune.
- **Smooth in game.** The view glides between camera frames at your frame rate instead of stepping
  30 times a second.
- **You can see what it does.** The Overview shows a *Live motion* graph: your raw head angle
  against what the game shows, over the last 8 seconds. Next to it, a *Tracking quality* card rates
  the tracker's measured jitter and names the one change most likely to help, such as more light or
  the frame-rate switch.
- **Plays nicely with the game.** Respects EFT's own freelook limits (and mods that change them).
  Pauses while aiming down sights, in menus, inventory, dialogs and cutscenes, and when alt-tabbed.
  Never touches aim or recoil. Inside the dead zone the camera is exactly vanilla.
- **No snapping, ever.** Losing your face holds the view briefly, then eases back to centre;
  finding it again glides back. Pauses, recentering and switching on/off all fade.
- **In-game keys:** recenter (F7) and on/off (F8) by default, changeable in the app.

## Screenshots

| Overview | Tracking source | Response |
|---|---|---|
| ![Overview page](docs/overview.png) | ![Tracking source page](docs/tracking.png) | ![Response page](docs/response.png) |

## Requirements

- SPT 4.1.x (EFT 0.16.9) on Windows 10 or 11, 64-bit.
- A webcam, or OpenTrack with any tracker it supports.
- Nothing else to install. The app runs on .NET Framework 4.8, which is part of Windows. If it ever
  reports that ONNX Runtime cannot load, install the
  [Microsoft Visual C++ 2015-2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- CPU: the webcam tracker takes about 7 ms of one core per camera frame, or roughly a quarter of
  one core at 30 fps. Turning off "Check every frame twice" or choosing the Fast model halves that.

## Install

Download `HeadTracking-<version>.zip` from Releases and unzip it **over your SPT folder** (the one
with `EscapeFromTarkov.exe`). You get:

```
HeadTracking.exe                      <- start this
HeadTracking.exe.config
HeadTrackingApp\                      <- the app's libraries, the face models, its settings and logs
BepInEx\plugins\HeadTracking.Plugin.dll
```

**Upgrading from 0.2.0 or 0.3.0:** close the game and the app first, then unzip over the old
version. On first start, 0.4.0 resets the smoothing settings to the new defaults, switches the model
to Balanced and turns on the mirrored check, because the old values were the ones that shook. All
your other settings (sensitivity, dead zones, keys, camera) are kept.

## Use

1. Start **HeadTracking.exe**. The Overview page shows your head on the left pad as you move.
2. Sit as you play, look at the middle of your screen and press **Recenter**.
3. Check the **Tracking quality** card. *Good* or *Excellent* is what you want. If it says *Fair* or
   *Poor*, follow its tip first: light and frame rate matter more than any setting.
4. Start SPT as usual. The app's header turns to **Game connected**, and to **In raid, applying** in raid.
5. In raid, turn your head slightly. If the view goes the wrong way, flip **Invert** for that axis
   on the Response page (it applies instantly).
6. **F7** recenters, **F8** turns head tracking off and on. Aiming down sights pauses it.

Leave the app running while you play; closing it eases the view back to centre.

### Tuning the feel

Use the **Feel** slider on the Overview, and watch the *Live motion* graph while you do. The thin
grey line is your raw head reading and the thick gold line is what the game shows.

- **The view twitches while you hold still:** move Feel to the right. The gold line should go flat
  while the grey one wobbles.
- **The view lags behind your head or feels floaty:** move Feel to the left. The gold line should
  follow the grey one's turns closely.

The default (a quarter of the way along, *Balanced*) is the setting that measured best: still at
rest, about 60 ms behind a turn. The Response page has the four settings behind the slider for
fine-tuning, and shows the jitter and stillness band in use right now.

### The pages

| Page | What it controls |
|---|---|
| Overview | Live head and in-game pads (the head pad's faint dot is the raw reading), the Live motion graph, the Feel slider, Tracking quality with a tip, a camera preview, a warning if the camera runs slow, quick start. |
| Tracking source | Webcam or OpenTrack. Which camera and picture format; keep the full frame rate in low light; the camera's own settings dialog; model (Fast, Balanced, Accurate); check every frame twice (mirrored); CPU threads; camera field of view; face-detection confidence; how long an unsure detector is trusted. |
| Response | Per axis: sensitivity, dead zone, furthest turn, curve and invert, each with a live graph. Smoothness: Feel, plus stillness, motion smoothing, steadiness, smoothing and fast-movement response, with the live jitter readout. Auto-centre. |
| In game | When to pause (aiming, cursor showing, window unfocused) and how fast to fade; what happens when tracking drops out (hold, return, glide back); the in-game keys. |
| Diagnostics | Plugin found or not, rates and timings, and the live log. |

### Using OpenTrack instead of the webcam tracker

Choose **OpenTrack** on the Tracking source page. In OpenTrack set Output to **UDP over network**,
IP `127.0.0.1`, port `4242`, and set the dead zones of OpenTrack's filter to 0. This app does its own
smoothing, and OpenTrack's dead zone makes a still head look like a lost one.

## Privacy

The webcam is opened by HeadTracking.exe on your PC and each frame is analysed in memory, then
discarded. Video is never recorded, saved or sent anywhere. The only thing that leaves the app is a
few numbers per frame (the head angles), sent to the game on this same PC (127.0.0.1). Nothing about
your head goes to the SPT server or to other players.

## Multiplayer (FIKA)

Untested. By design the plugin is client-only: it changes only your own camera and leaves alone the
head-rotation value that FIKA sends to other players, so they see your mouse freelook but not your
head tracking.

## Troubleshooting

| Symptom | Look at |
|---|---|
| The view shakes or drifts while you hold still | The Tracking quality card on the Overview: its tip names the likeliest cause. Then move Feel to the right. The log's `Status:` line every 10 s gives the camera's real frame rate, the measured jitter and the stillness band. |
| "Looking for your face" never changes | Under about 40/255 brightness the room is too dark; the quality card says so, and Diagnostics shows the value. Face the camera; try a lower face-detection confidence. |
| The camera runs under 25 fps | Turn on "Keep the full frame rate" (Tracking source), add light, or shorten the exposure in Camera settings. |
| "Not tracking" with an error | The message says what to do: camera in use by another program (Discord, OBS, Teams, a browser), no camera found, or Windows' camera privacy setting. |
| "Game not connected" in raid | The plugin must be in `BepInEx\plugins`. The game's log, `BepInEx\LogOutput.log`, has lines starting `[Info :Head Tracking]`; look for `Listening for HeadTracking.exe` and `HeadTracking.exe connected`. |
| The view moves the wrong way | Invert that axis on the Response page. The game's log has a `Direction check` line the first time you turn and tilt. |
| The view drifts back to centre while you hold still (OpenTrack only) | Set OpenTrack's filter dead zones to 0, or raise "OpenTrack pose frozen for" on the In game page. |

The app's log is `HeadTrackingApp\logs\HeadTracking.log` (Diagnostics > Open log folder).

## How it works

```
 webcam --> HeadTracking.exe ------------------------------- UDP 127.0.0.1:4243 --> BepInEx plugin --> freelook camera
            face detector + head-pose network (ONNX),           head offset,          pauses (menus, ADS...),
            mirrored check, centre, steadiness, stillness lock, settings              game look limits, smooth follow
            dead zone, curve; tracking-loss hold / return  <--- UDP 127.0.0.1:4244 --- status, in-game keys
 OpenTrack -- UDP 127.0.0.1:4242 --^
```

- **Webcam tracking** is a C# port of OpenTrack's `tracker-neuralnet`. A face localizer finds the
  head, and a pose network gives its rotation, centre and size. OpenTrack's geometry turns that into
  yaw/pitch/roll and distance, corrected for the head sitting off the image centre.
  - The face crop follows the head through a damped box, so the network does not see a crop that
    shifts by a few pixels every frame.
  - With the mirrored check on, each crop also runs flipped. The flipped answer is flipped back and
    averaged with the first, which cancels much of the network's per-image error.
  - Frames are converted straight from the camera's YUV data to greyscale, and the networks always
    work on the newest frame, so a slow frame costs a frame, never latency.
  - The camera's low light compensation (UVC auto-exposure priority) is switched off through
    DirectShow. On a Logitech C920 that took it from 15 to 30 fps.
- **Filtering** happens once per camera frame, using the real time between frames.
  1. OpenTrack's soft dead zone, sized by the pose network's own per-frame uncertainty output.
  2. The stillness lock: the output stays put until the head leaves a band around it, then follows
     dragging the band, like gear backlash. The band is 1.5x the jitter measured live (a robust
     percentile of recent frame-to-frame steps), so it fits the camera and the light. While held,
     the view creeps toward the head (time constant 4 s), so small adjustments are not lost.
  3. Optionally a 1€ filter, on the smoother half of the Feel slider.
- **In the game**, the plugin is a Harmony prefix on `Player.VisualPass`. After the game's own
  freelook has run and before the camera is placed, it sets the camera's head rotation to
  *mouse freelook + head offset*, clamped and shaped exactly as `Player.Look` shapes mouse freelook.
  - It never writes `Player.HeadRotation`, so nothing accumulates and nothing is sent to other
    players.
  - It follows the app's newest pose with a critically damped spring every rendered frame, so 30 Hz
    updates become continuous motion at 60, 144 or 240 fps. It settles to exactly zero at centre.

## Building from source

Needs the .NET 10 SDK, an SPT 4.1 install (the plugin compiles
against its patched `Assembly-CSharp.dll`), and OpenTrack 2026.1.0 installed for its model files.

```powershell
scripts\pack.ps1 -SPTPath H:\SPT4.1.X                 # build app + plugin, test, smoke-test, zip into dist\
scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install        # ...and install into that SPT folder
scripts\pack.ps1 -SPTPath H:\SPT4.1.X -ModelsPath "C:\Program Files (x86)\opentrack\modules\models"
```

| Path | |
|---|---|
| `src/HeadTracking.App` | HeadTracking.exe: WPF on .NET Framework 4.8. `Webcam/` is the tracker, `Tracking/` the head maths and filters, `Engine/` settings, link and the engine thread, `UI/` the window, `Dev/` the developer modes. |
| `src/HeadTracking.Plugin` | The BepInEx plugin (net472). |
| `src/Shared` | The link protocol, pause logic and the render-rate follow, compiled into both. |
| `tests/HeadTracking.Tests` | xUnit tests for everything that does not need the game or a camera. |
| `scripts/fake-game.ps1` | Stands in for the game: prints what the app sends and answers like the plugin. |
| `scripts/send-test-poses.ps1` | Stands in for OpenTrack: scripted head movements, face loss and recovery, and optional `-Noise`, for testing without a camera. |

Developer modes of `HeadTracking.exe`. None of them keeps or shows camera pictures; they print
numbers only.

| Mode | What it does |
|---|---|
| `--test-image face.png --out results.txt` | Runs the webcam tracker on still images, also mirrored and shifted. |
| `--jitter-test face.png` | Measures how much each model and filter setting shakes on a still face with added sensor noise. |
| `--live-jitter [--csv angles.csv]` | Measures the live camera's tracking jitter for each model, crop and mirror option, with the in-game result of each filter setting. Optionally saves the head angles (numbers, not pictures) for `--replay`. |
| `--replay angles.csv` | Runs recorded or synthetic head angles through a grid of filter settings and scores each on rest motion, lag and error. |
| `--camera-test` | Measures the frame rate the camera really delivers. |
| `--snapshot <folder>` | Renders every page of the window to PNG. |

## Credits

The webcam tracker is a port of [OpenTrack](https://github.com/opentrack/opentrack)'s neuralnet
tracker by Michael Welter (ISC licence), and uses its models. Camera capture by
[FlashCap](https://github.com/kekyo/FlashCap) (Apache-2.0); inference by
[ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT). See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
