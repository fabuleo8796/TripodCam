# Tripod Cam (0.3.0)

A Flipping is Hard mod (BepInEx 6 IL2CPP) by Fabuleo8796.
- **T**: fly a camera to frame a shot, then plant a tripod there. In placing mode, **1 / 2 / 3** pick Fixed, Aim at Flippy or Follow Flippy, **Enter** or a click plants, **Esc** cancels.
- **Y**: record what the tripod sees to an MP4 while you keep playing (without the game's speed lines, which belong to
  your own view), with the game's sound as AAC (only the game's: Windows' application loopback capture, not Discord
  or music). Videos go to `Videos\Flipping is Hard`.
- **M**: the panel: record button, tripod mode / smoothing / field of view, video size, fps, quality, shadows, smooth
  edges, hide names, record sound, live preview, the video folder (Change opens Windows' folder picker, × goes back to the
  default), shortcuts (click to change) and live stats while recording.

## Install

Needs Flipping is Hard with BepInEx 6 (IL2CPP) and Windows 10 2004 or newer (for the sound).
Download `release/TripodCam.dll` and put it in `<game>\BepInEx\plugins\TripodCam\`. Settings are in
`BepInEx\config\com.fabuleo8796.tripodcam.cfg` and in the panel (**M**).

Client-side only: nothing is sent to other players. MIT license.

## Layout

| Path | What |
|---|---|
| `release/TripodCam.dll` | The built mod (0.3.0) |
| `src/` | The mod: `Plugin` (settings), `TripodDriver` (keys, placing, tripod modes), `Recorder` (tripod camera, NV12 conversion, readback, encoder thread, stats), `MfEncoder` (Media Foundation H.264 MP4, NV12 or BGRA input, optional AAC sound stream), `AudioCapture` (the game's own sound from Windows' application loopback, on its own thread), `Overlay` (placing view, REC badge, preview, toasts), `Panel` (the M panel), `FolderPicker` (Windows' Select folder window on its own thread), `GameControls` (pauses the game's input while placing or in the panel) |
| `src/Resources/` | Geist fonts (OFL), embedded |
| `unity/` | Minimal Unity 6000.0.66f1 project (no packages) holding the NV12 shader and `TripodCamBuild`, which builds the shader bundle and checks the shader's output against the same maths on the CPU |
| `tests/EncoderTest/` | Offline test of `MfEncoder` (writes test MP4s, no game needed) |
| `tests/SoundTest/` | Offline test of `AudioCapture` + the AAC stream: plays two quiet beeps, captures them, stalls the encoder side for 1.6 s, writes an MP4 with white frames at the beeps, decodes it and checks sound and picture line up (`_private/soundtest.rsp`) |
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
- `-resource:src\Resources\<file>,<file>` for the three font files, and `-resource:build\bundle\tripodcam.shaders,tripodcam.shaders`;
- all of `src\*.cs`.

There are too many references for a command line, so they go in a response file.

Install: copy `build\TripodCam.dll` to `<game>\BepInEx\plugins\TripodCam\`.
