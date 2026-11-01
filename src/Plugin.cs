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
/// Flippy, or following Flippy), press Y to record what it sees (and the game's sound) to an MP4 while you keep playing,
/// M for the panel.
/// Client-side only: nothing is sent to other players.
[BepInPlugin(Guid, "Tripod Cam", "0.3.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.tripodcam";
    internal static ManualLogSource Logger;
    internal static ConfigEntry<Key> TripodKey, RecordKey, MenuKey;
    internal static ConfigEntry<int> Width, Height, Fps;
    internal static ConfigEntry<VideoQuality> Quality;
    internal static ConfigEntry<bool> GpuEncoder, HideNames, Shadows, SmoothEdges, Preview, Sound;
    internal static ConfigEntry<int> SoundOffsetMs;
    internal static ConfigEntry<string> Folder;
    internal static ConfigEntry<float> FlySpeed, LookSpeed, Smoothing, AimHeight;

    public override void Load()
    {
        Logger = Log;
        TripodKey = Config.Bind("Keys", "Tripod", Key.T, "Place or move the tripod.");
        RecordKey = Config.Bind("Keys", "Record", Key.Y, "Start or stop recording.");
        MenuKey = Config.Bind("Keys", "Menu", Key.M, "Open or close the Tripod Cam panel.");
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
        Smoothing = Config.Bind("Tripod", "Smoothing", 0.35f, new ConfigDescription("How smoothly Aim and Follow turn and move (0 = locked on).", new AcceptableValueRange<float>(0f, 2f)));
        AimHeight = Config.Bind("Tripod", "AimHeight", 0.3f, "Aim this far above Flippy's middle.");

        ClassInjector.RegisterTypeInIl2Cpp<TripodDriver>();
        var host = new GameObject("TripodCam");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<TripodDriver>();
        Log.LogInfo($"Tripod Cam loaded: {TripodKey.Value} places the tripod, {RecordKey.Value} records, {MenuKey.Value} opens the panel. Videos go to {VideoFolder}");
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
