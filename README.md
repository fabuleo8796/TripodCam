# Tripod Cam (0.5.0)

A Flipping is Hard mod (BepInEx 6 IL2CPP) by Fabuleo8796.
- **T**: fly a camera to frame a shot, then plant a tripod there. In placing mode, **WASD / Q E** fly (**Shift** 12×
  faster, **Ctrl** slower), the scroll wheel sets the speed, **Alt+Scroll** zooms, **H** brings the camera back to
  Flippy, **1 / 2 / 3 / 4** pick Fixed, Aim at Flippy, Follow Flippy or Keyframes, **Enter** or a click plants,
  **Esc** cancels.
- **Keyframes (4)**: a camera move you design. While placing, fly to a spot and press **I** to set a keyframe; the
  next **I** adds the next one 2 s later. A timeline in seconds and frames shows the keys: **arrows** pick a key (the
  camera jumps there), **I** on a key updates it, **Del** deletes it, **Space** plays the path. Click (or **Tab**) to
  use the mouse on the timeline: drag keys to retime them (past an end to go further), drag the playhead to scrub,
  right-click deletes, **Scroll** moves along the timeline and **Ctrl+Scroll** zooms it. The path
  is drawn in the world while you edit. The camera glides through the keys on smooth curves (no overshoot, easing in
  and out at the ends). Recording plays the path from its start, then holds, loops or stops the recording (panel);
  **I** while recording starts the path over.
- **Y**: record what the tripod sees to an MP4 while you keep playing (without the game's speed lines, which belong to
  your own view), with the game's sound as AAC (only the game's: Windows' application loopback capture, not Discord
  or music). Videos go to `Videos\Flipping is Hard`.
- **M**: the panel, on the right of the screen (clear of other mods' windows): record button, tripod mode / smoothing /
  field of view (or the keyframe path and what happens at its end), video size, fps, quality, shadows, smooth edges,
  hide names, record sound, live preview, the video folder (Change opens Windows' folder picker, × goes back to the
  default), shortcuts (click to change) and live stats while recording.
- **Camera model**: a little cinema camera on a tripod stands where the tripod films, pointing the way it films (it
  turns and tilts with Aim, Follow and Keyframes, its legs reach down to the ground). Once planted it's see-through, so
  you can see where your shot is; while you record it's solid and its REC light blinks. It's never in your own videos,
  and it hides when your own view gets right up to it. Switch it off in the panel (**Show the camera in the world**);
  in the cfg, `CameraSize` makes it bigger or smaller and `GhostOpacity` sets how see-through it is before you record
  (0 = only while recording). It's built in code
  (boxes and cylinders), no game art.

> **FIH Sync comes included** (since 0.5.1): `FIHSync.dll` is in the mod's folder, so sharing with other modded players works without installing anything else. With several of my mods installed, BepInEx runs only one copy of it (the newest).

## Install

Needs Flipping is Hard with BepInEx 6 (IL2CPP) and Windows 10 2004 or newer (for the sound).
Download the zip from the latest release and extract it into the game folder (you get
`<game>\BepInEx\plugins\TripodCam\` with `TripodCam.dll` and `FIHSync.dll`), or install it from FIH Mod Manager. Settings are in
`BepInEx\config\com.fabuleo8796.tripodcam.cfg` and in the panel (**M**).

Nothing is sent to other players, with one exception: through the included **FIH Sync**, other modded players with Tripod
Cam see your camera model while you record, and you see theirs. Only your tripod's settings go out
(where it stands, its mode, smoothing and keyframe path), once when you start recording, never your video. MIT license.

## Layout

| Path | What |
|---|---|
| `release/TripodCam.dll` | The built mod (the latest release) |
| `src/` | The mod: `Plugin` (settings), `TripodDriver` (keys, placing, tripod modes), `Recorder` (tripod camera, NV12 conversion, readback, encoder thread, stats), `MfEncoder` (Media Foundation H.264 MP4, NV12 or BGRA input, optional AAC sound stream), `AudioCapture` (the game's own sound from Windows' application loopback, on its own thread), `Overlay` (placing view, REC badge, preview, toasts), `Panel` (the M panel), `KeyPath` (keyframes and the smooth curves through them, no Unity types), `Keyframes` (the keyframe editor and path playback), `Timeline` (the timeline and the path drawn over the world), `FolderPicker` (Windows' Select folder window on its own thread), `GameControls` (pauses the game's input while placing or in the panel), `CameraModel` (the camera on a tripod you see in the world while recording, meshes built in code, kept off the tripod camera's layers), `TripodPose` (where a tripod's camera is each frame, worked out from its settings; also its one-line form for FIH Sync), `SyncLink` (the optional FIH Sync link) |
| `src/Resources/` | Geist fonts (OFL), embedded |
| `unity/` | Minimal Unity 6000.0.66f1 project (no packages) holding the NV12 shader and `TripodCamBuild`, which builds the shader bundle and checks the shader's output against the same maths on the CPU |
| `tests/EncoderTest/` | Offline test of `MfEncoder` (writes test MP4s, no game needed) |
| `tests/SoundTest/` | Offline test of `AudioCapture` + the AAC stream: plays two quiet beeps, captures them, stalls the encoder side for 1.6 s, writes an MP4 with white frames at the beeps, decodes it and checks sound and picture line up (`_private/soundtest.rsp`) |
| `tests/KeyPathTest/` | Offline test of `KeyPath`: hits every key, smooth speed through keys, eases at the ends, no overshoot, holds stay still, yaw the short way, retiming (`_private/keypathtest.rsp`) |
| `tests/FolderPickerTest/` | Offline test of `FolderPicker`: opens the window, presses OK and Close by itself, checks the write test (`_private/pickertest.rsp`) |
| `probe/` | TripodCamProbe, the M0 recon plugin |
| `tools/MetaDump/` | Lists classes and members of interop DLLs (no download needed) |
| `tools/NativeCheck/` | Finds game methods the mod calls that Il2CppInterop rebuilt (stripped from the game; some crash it). Run `scan build\TripodCam.dll <game>\BepInEx\interop` before installing |
| `tools/mdread.py` | Reads a Windows crash dump (`%LOCALAPPDATA%\CrashDumps`): crashing thread, registers, modules on its stack |
| `_private/` | Dumps, probe outputs and build files. Git-ignored, never published |

## Building (no .NET SDK)

1. **Shader bundle** (only when `unity/Assets/Shaders/TripodCamNV12.shader` changes; takes about a minute):
   `Unity.exe -batchmode -projectPath unity -buildTarget Win64 -executeMethod TripodCamBuild.BuildAndTest -logFile _private\unity_build.log`.
   It writes `build\bundle\tripodcam.shaders`, `_private\enc\shader_test.txt` (must end with `ALL OK`) and
   `_private\enc\nv12_1080p.bin` (a test frame that `tests\EncoderTest` encodes).
2. **The plugin**: use Unity 6000.0.66f1's bundled compiler: `<Unity>\Editor\Data\NetCoreRuntime\dotnet.exe <Unity>\Editor\Data\DotNetSdkRoslyn\csc.dll -noconfig @tripodcam.rsp`.

The response file holds:
- `-nostdlib -target:library -langversion:10 -unsafe -optimize -deterministic -nowarn:CS1701,CS1702,CS0618 -out:build\TripodCam.dll`
- references to the game's `dotnet\System.*.dll`, `mscorlib` and `netstandard` (skip `*Native*`);
- references to `BepInEx\core` (BepInEx.Core, BepInEx.Unity.IL2CPP, BepInEx.Unity.Common, 0Harmony, Il2CppInterop.Runtime/Common);
- every `BepInEx\interop\*.dll` **except the 3 KB `UnityEngine.dll` facade**;
- `lib\FIHSync.dll` (FIH Sync's DLL, for the optional camera model sharing; Tripod Cam runs fine without it installed);
- `-resource:src\Resources\<file>,<file>` for the three font files, and `-resource:build\bundle\tripodcam.shaders,tripodcam.shaders`;
- all of `src\*.cs`.

There are too many references for a command line, so they go in a response file. After a game update, list the
interop folder again: DLLs come and go (the November 2026 update dropped GameAnalytics and two Il2Cpp ones).

Install: copy `build\TripodCam.dll` to `<game>\BepInEx\plugins\TripodCam\`.
