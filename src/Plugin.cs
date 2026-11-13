using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace TripodCam;

/// Tripod Cam for Flipping is Hard: press T to fly a camera to a spot and plant a tripod there (fixed, aiming at
/// Flippy, following Flippy, or gliding through keyframes you set with I), press Y to record what it sees (and the
/// game's sound) to an MP4 while you keep playing, M for the panel. A little camera on a tripod stands where it films
/// (see-through until you record, never in the video).
/// Nothing is sent to other players, unless FIH Sync is installed: then modded players see your camera model too.
[BepInPlugin(Guid, "Tripod Cam", "0.5.1")]
[BepInDependency("com.fabuleo8796.fihsync", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.tripodcam";
    internal static ManualLogSource Logger;
    internal static ConfigEntry<Key> TripodKey, RecordKey, MenuKey, KeyframeKey, ToFlippyKey;
    internal static ConfigEntry<int> Width, Height, Fps;
    internal static ConfigEntry<VideoQuality> Quality;
    internal static ConfigEntry<bool> GpuEncoder, HideNames, Shadows, SmoothEdges, Preview, Sound, ShowCamera;
    internal static ConfigEntry<int> SoundOffsetMs;
    internal static ConfigEntry<string> Folder;
    internal static ConfigEntry<float> FlySpeed, LookSpeed, ShiftBoost, Smoothing, AimHeight, KeyGap, CameraSize, GhostOpacity;
    internal static ConfigEntry<PathEnd> PathEndMode;

    public override void Load()
    {
        Logger = Log;
        TripodKey = Config.Bind("Keys", "Tripod", Key.T, "Place or move the tripod.");
        RecordKey = Config.Bind("Keys", "Record", Key.Y, "Start or stop recording.");
        MenuKey = Config.Bind("Keys", "Menu", Key.M, "Open or close the Tripod Cam panel.");
        KeyframeKey = Config.Bind("Keys", "Keyframe", Key.I, "Keyframes mode: add a keyframe where the camera is (while placing), or start the path over (while recording).");
        ToFlippyKey = Config.Bind("Keys", "ToFlippy", Key.H, "While placing: bring the camera back to Flippy, seen like the game shows it.");
        Width = Config.Bind("Video", "Width", 1920, "Video width in pixels.");
        Height = Config.Bind("Video", "Height", 1080, "Video height in pixels.");
        Fps = Config.Bind("Video", "Fps", 30, new ConfigDescription("Frames per second of the video.", new AcceptableValueRange<int>(15, 60)));
        Quality = Config.Bind("Video", "Quality", VideoQuality.High, "Low (small files), High or Max. High is 16 Mbps at 1080p30; it scales with the size and fps.");
        GpuEncoder = Config.Bind("Video", "GpuEncoder", true, "Use the graphics card's video encoder when there is one (much lighter on the game).");
        HideNames = Config.Bind("Video", "HideNamesAndPings", true, "Keep player names and pings out of the video.");
        Shadows = Config.Bind("Video", "Shadows", true, "Shadows in the video. Off makes recording lighter on the game.");
        SmoothEdges = Config.Bind("Video", "SmoothEdges", true, "Smooth edges (MSAA, like the game). Off makes recording lighter on the game.");
        Preview = Config.Bind("Video", "LivePreview", true, "A small live view of the tripod while recording.");
        Sound = Config.Bind("Video", "Sound", true, "Record the game's sound: only the game's, not Discord, music or other apps. Needs Windows 10 2004 or newer.");
        SoundOffsetMs = Config.Bind("Video", "SoundOffsetMs", 0, new ConfigDescription("Moves the sound against the picture, in milliseconds: + later, - earlier.", new AcceptableValueRange<int>(-500, 500)));
        Folder = Config.Bind("Video", "Folder", "", "Where videos are saved (the panel's Change button picks it). Empty = Videos\\Flipping is Hard.");
        FlySpeed = Config.Bind("Placing", "FlySpeed", 6f, "Starting fly speed while placing the tripod (units per second).");
        LookSpeed = Config.Bind("Placing", "LookSpeed", 1f, "Mouse look speed while placing the tripod.");
        ShiftBoost = Config.Bind("Placing", "ShiftBoost", 12f, new ConfigDescription("How many times faster the camera flies while Shift is held.", new AcceptableValueRange<float>(2f, 50f)));
        Smoothing = Config.Bind("Tripod", "Smoothing", 0.35f, new ConfigDescription("How smoothly Aim and Follow turn and move (0 = locked on).", new AcceptableValueRange<float>(0f, 2f)));
        AimHeight = Config.Bind("Tripod", "AimHeight", 0.3f, "Aim this far above Flippy's middle.");
        ShowCamera = Config.Bind("Tripod", "ShowCamera", true, "While recording, a little camera on a tripod stands where the tripod films (never in your own videos). With FIH Sync, modded players see yours and you see theirs. Off hides them all.");
        CameraSize = Config.Bind("Tripod", "CameraSize", 1f, new ConfigDescription("Size of the camera model (1 = about half as long as Flippy).", new AcceptableValueRange<float>(0.5f, 3f)));
        GhostOpacity = Config.Bind("Tripod", "GhostOpacity", 0.35f, new ConfigDescription("While the tripod is planted but not recording, the camera model shows see-through at this opacity (0 = only show it while recording, 1 = solid).", new AcceptableValueRange<float>(0f, 1f)));
        KeyGap = Config.Bind("Keyframes", "Gap", 2f, new ConfigDescription("Seconds between keyframes added one after another (drag them on the timeline to change it).", new AcceptableValueRange<float>(0.25f, 30f)));
        PathEndMode = Config.Bind("Keyframes", "AtEnd", PathEnd.Hold, "After the last keyframe while recording: Hold (the camera stays there), Loop (the path starts over) or Stop (recording stops).");

        ClassInjector.RegisterTypeInIl2Cpp<TripodDriver>();
        var host = new GameObject("TripodCam");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<TripodDriver>();
        Log.LogInfo($"Tripod Cam loaded: {TripodKey.Value} places the tripod, {RecordKey.Value} records, {MenuKey.Value} opens the panel, {KeyframeKey.Value} adds keyframes. Videos go to {VideoFolder}");
    }

    /// Bits per second for a video: the quality's rate at 1080p30, scaled by size and (less than linearly) fps.
    internal static int Bitrate(int width, int height, int fps)
    {
        double mbps = Quality.Value switch { VideoQuality.Low => 8, VideoQuality.Max => 32, _ => 16 };
        mbps *= width * (double)height / (1920 * 1080) * Math.Pow(fps / 30.0, 0.6);
        return (int)(Math.Clamp(mbps, 2, 120) * 1_000_000);
    }

    internal static string VideoFolder => string.IsNullOrWhiteSpace(Folder.Value) ? DefaultFolder : Folder.Value;

    internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Flipping is Hard");

    internal static string ConfigFolder => Path.Combine(Paths.ConfigPath, "TripodCam");
}

internal enum VideoQuality { Low, High, Max }

internal enum PathEnd { Hold, Loop, Stop }
