using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TripodCam;

/// The Tripod Cam panel (menu key, M), on the right of the screen so it stays clear of other mods' windows: record
/// button, tripod mode and sliders (or the keyframe path's settings in mode 4), video settings, shortcuts you can change
/// by clicking them, and live stats while recording. Like the overlay it's built once ahead of time and only switched
/// on and off; while it's open it frees the cursor and pauses the game's controls (StickerSwap's way), and it only
/// refreshes its numbers twice a second.
internal static class Panel
{
    const float W = 468f, H = 1004f, Pad = 26f, Inner = W - 2 * Pad;
    const float ChangeX = W - Pad - 80f - 8f - 104f, FolderWidth = ChangeX - 10f - Pad;
    static readonly Color Back = new(0.05f, 0.11f, 0.12f, 0.88f);
    static readonly Color Soft = new(1f, 1f, 1f, 0.09f);
    static readonly Color Glass = new(1f, 1f, 1f, 0.14f);
    static readonly Color Dim = new(1f, 1f, 1f, 0.5f);
    static readonly Color Ink = new(1f, 1f, 1f, 0.9f);
    static readonly string[] Modes = { "Fixed", "Aim", "Follow", "Keys" };
    static readonly string[] ShortcutNames = { "Tripod", "Record", "Menu", "Keyframe", "To Flippy" };
    static readonly int[] Heights = { 720, 1080, 1440 };
    static readonly int[] Rates = { 30, 60 };
    static readonly Key[] Blocked =
    {
        Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E, Key.Space, Key.Escape, Key.Enter, Key.NumpadEnter, Key.Tab, Key.Backspace,
        Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl,
        Key.LeftAlt, Key.RightAlt, Key.UpArrow, Key.DownArrow, Key.LeftArrow, Key.RightArrow, Key.P, Key.Delete,
    };
    static Key[] allKeys;

    static GameObject root;
    static RectTransform panel;
    static TextMeshProUGUI menuKeyText, recordText, recordKeyText, moveText, moveKeyText, smoothValue, fovValue, videoInfo, statsText;
    static TextMeshProUGUI keysValue, keysHint, placingHelp;
    static TextMeshProUGUI folderText, changeText;
    static Image recordBack, recordIcon, changeBack;
    static Sprite stopSprite;
    static RectTransform recordKeyChip, moveChip, resetFolder;
    static Segmented modeSeg, sizeSeg, rateSeg, qualitySeg, lookSeg, endSeg;
    static GameObject steadyRows, keyRows;
    static Switch previewSwitch, soundSwitch;
    static Slider smoothSlider, fovSlider;
    static CanvasGroup fovGroup, videoGroup;
    static readonly KeyTile[] tiles = new KeyTile[5];
    static int rebinding = -1, rebindFrame;
    static CursorLockMode oldLock;
    static bool oldVisible;
    static float nextRefresh;

    public static bool IsOpen => root != null && root.activeSelf;
    public static bool Rebinding => rebinding >= 0;

    // ---------------------------------------------------------------- build (once)

    public static void Build()
    {
        if (root != null) return;
        Overlay.Build(); // fonts and sprites
        try
        {
            var watch = Stopwatch.StartNew();
            root = new GameObject("TripodCam Panel");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30020;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            stopSprite = Overlay.MakeRounded(32, 6);
            panel = Overlay.Node(root.transform, "Panel");
            Overlay.Anchor(panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-64f, -38f), new Vector2(W, H));
            Fill(panel, Overlay.rounded, Back, true); // catches clicks so they never fall through

            BuildHeader();
            BuildRecordButton();
            BuildTripod();
            BuildVideo();
            BuildShortcuts();
            root.SetActive(false);
            Plugin.Logger.LogInfo($"Tripod Cam panel ready ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("couldn't build the panel: " + e);
        }
    }

    static void BuildHeader()
    {
        // a little tripod: camera body, lens, three legs
        var icon = At(panel, "Icon", Pad, 22f, 32f, 32f);
        foreach (var (x, angle, length) in new[] { (16f, 24f, 17f), (16f, -24f, 17f), (16f, 0f, 15f) })
        {
            var leg = Overlay.Plain(icon, Overlay.Accent);
            leg.anchorMin = leg.anchorMax = new Vector2(0f, 1f);
            leg.pivot = new Vector2(0.5f, 1f);
            leg.anchoredPosition = new Vector2(x, -14f);
            leg.sizeDelta = new Vector2(3f, length);
            leg.localRotation = Quaternion.Euler(0f, 0f, angle);
        }
        Fill(At(icon, "Body", 4f, 1f, 24f, 15f), Overlay.small, Overlay.Accent);
        Fill(At(icon, "Lens", 12.5f, 5f, 7f, 7f), Overlay.circle, Back);
        Label(panel, "Tripod Cam", Overlay.semiBold, 28f, Color.white, Pad + 42f, 18f, 260f, 40f);

        var close = KeyCap(panel, Plugin.MenuKey.Value, W - Pad - 34f, 24f, out menuKeyText);
        Click(close.gameObject, close.GetComponent<Image>(), Close);
    }

    static void BuildRecordButton()
    {
        var button = At(panel, "Record", Pad, 74f, Inner, 62f);
        recordBack = Fill(button, Overlay.rounded, Overlay.Accent, true);
        var icon = At(button, "Icon", 22f, 21f, 20f, 20f);
        recordIcon = Fill(icon, Overlay.circle, Overlay.RecRed);
        recordText = Label(button, "", Overlay.semiBold, 23f, Color.white, 56f, 0f, Inner - 120f, 62f);
        recordKeyChip = KeyCap(button, Plugin.RecordKey.Value, Inner - 52f, 16f, out recordKeyText);
        Click(button.gameObject, recordBack, RecordPressed);
    }

    static void BuildTripod()
    {
        Section(panel, "TRIPOD", 168f);
        var move = At(panel, "Move", W - Pad - 172f, 156f, 172f, 42f);
        var moveBack = Fill(move, Overlay.small, Glass, true);
        moveText = Label(move, "Move tripod", Overlay.semiBold, 18f, Color.white, 14f, 0f, 120f, 42f);
        moveChip = KeyCap(move, Plugin.TripodKey.Value, 172f - 44f, 6f, out moveKeyText);
        Click(move.gameObject, moveBack, () => TripodDriver.Instance?.PlaceFromPanel());

        modeSeg = new Segmented(panel, Pad, 210f, Inner, 46f, Modes, i => TripodDriver.Instance?.SetMode((TripodDriver.Mode)i));

        // Fixed, Aim and Follow: smoothing and field of view
        var steady = At(panel, "Steady", 0f, 270f, W, 126f);
        steadyRows = steady.gameObject;
        Label(steady, "Smoothing", Overlay.medium, 18f, Ink, Pad, 0f, 200f, 26f);
        smoothValue = Label(steady, "", Overlay.medium, 18f, Ink, W - Pad - 100f, 0f, 100f, 26f, TextAlignmentOptions.MidlineRight);
        smoothSlider = MakeSlider(steady, 28f, 0f, 2f, v =>
        {
            Plugin.Smoothing.Value = Mathf.Round(v * 20f) / 20f;
            smoothValue.text = Plugin.Smoothing.Value.ToString("0.00", CultureInfo.InvariantCulture);
        });

        var fovRow = At(steady, "Fov", 0f, 64f, W, 62f);
        fovGroup = fovRow.gameObject.AddComponent<CanvasGroup>();
        Label(fovRow, "Field of view", Overlay.medium, 18f, Ink, Pad, 0f, 200f, 26f);
        fovValue = Label(fovRow, "", Overlay.medium, 18f, Ink, W - Pad - 100f, 0f, 100f, 26f, TextAlignmentOptions.MidlineRight);
        fovSlider = MakeSlider(fovRow, 28f, 15f, 110f, v =>
        {
            float fov = Mathf.Round(v);
            TripodDriver.Instance?.SetFov(fov);
            fovValue.text = $"{fov:0}°";
        });

        // Keyframes: what the path has, and what happens after its last key
        var keys = At(panel, "Keyframes", 0f, 270f, W, 126f);
        keyRows = keys.gameObject;
        Label(keys, "Keyframes", Overlay.medium, 18f, Ink, Pad, 0f, 200f, 26f);
        keysValue = Label(keys, "", Overlay.medium, 18f, Ink, W - Pad - 240f, 0f, 240f, 26f, TextAlignmentOptions.MidlineRight);
        Label(keys, "At the end", Overlay.medium, 18f, Ink, Pad, 38f, 120f, 44f);
        endSeg = new Segmented(keys, Pad + 116f, 38f, Inner - 116f, 44f, new[] { "Hold", "Loop", "Stop rec" }, i => Plugin.PathEndMode.Value = (PathEnd)i);
        keysHint = Label(keys, "", Overlay.medium, 14f, Dim, Pad, 88f, Inner, 38f);
        keysHint.enableWordWrapping = true;
        keyRows.SetActive(false);

        previewSwitch = new Switch(panel, 404f, "Live preview while recording", on => Plugin.Preview.Value = on);
    }

    static void BuildVideo()
    {
        Section(panel, "VIDEO", 460f);
        var box = At(panel, "Video", 0f, 484f, W, 236f);
        videoGroup = box.gameObject.AddComponent<CanvasGroup>();
        sizeSeg = new Segmented(box, Pad, 0f, Inner, 46f, new[] { "720p", "1080p", "1440p" }, i =>
        {
            Plugin.Height.Value = Heights[i];
            Plugin.Width.Value = Heights[i] * 16 / 9;
        });
        rateSeg = new Segmented(box, Pad, 56f, 156f, 46f, new[] { "30 fps", "60 fps" }, i => Plugin.Fps.Value = Rates[i]);
        qualitySeg = new Segmented(box, Pad + 166f, 56f, Inner - 166f, 46f, new[] { "Low", "High", "Max" }, i => Plugin.Quality.Value = (VideoQuality)i);
        lookSeg = new Segmented(box, Pad, 112f, Inner, 46f, new[] { "Shadows", "Smooth edges", "Hide names" }, i =>
        {
            var entry = i == 0 ? Plugin.Shadows : i == 1 ? Plugin.SmoothEdges : Plugin.HideNames;
            entry.Value = !entry.Value;
        });
        soundSwitch = new Switch(box, 166f, "Record the game's sound", on => Plugin.Sound.Value = on);
        videoInfo = Label(box, "", Overlay.medium, 14f, Dim, Pad, 204f, Inner, 40f);
        videoInfo.enableWordWrapping = true;

        // where videos go: the path, a × back to the default folder (only when it's changed), Change and Open
        folderText = Label(panel, "", Overlay.medium, 16f, Ink, Pad, 736f, FolderWidth, 40f);
        folderText.overflowMode = TextOverflowModes.Ellipsis;
        resetFolder = At(panel, "Default", ChangeX - 38f, 742f, 28f, 28f);
        var resetBack = Fill(resetFolder, Overlay.circle, Glass, true);
        foreach (var angle in new[] { 45f, -45f })
        {
            var bar = Overlay.Plain(resetFolder, Color.white);
            bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(0.5f, 0.5f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(12f, 2f);
            bar.localRotation = Quaternion.Euler(0f, 0f, angle);
        }
        Click(resetFolder.gameObject, resetBack, ResetFolder);
        var change = At(panel, "Change", ChangeX, 736f, 104f, 40f);
        changeBack = Fill(change, Overlay.small, Glass, true);
        changeText = Label(change, "Change", Overlay.semiBold, 17f, Color.white, 0f, 0f, 104f, 40f, TextAlignmentOptions.Center);
        Click(change.gameObject, changeBack, ChangeFolder);
        var open = At(panel, "Open", W - Pad - 80f, 736f, 80f, 40f);
        var openBack = Fill(open, Overlay.small, Glass, true);
        Label(open, "Open", Overlay.semiBold, 17f, Color.white, 0f, 0f, 80f, 40f, TextAlignmentOptions.Center);
        Click(open.gameObject, openBack, OpenFolder);
    }

    static void BuildShortcuts()
    {
        Section(panel, "SHORTCUTS · CLICK TO CHANGE", 796f);
        float w = (Inner - 40f) / 5f;
        for (int i = 0; i < 5; i++)
        {
            int index = i;
            tiles[i] = new KeyTile(panel, Pad + i * (w + 10f), 824f, w, 72f, ShortcutNames[i], () => StartRebind(index));
        }
        placingHelp = Label(panel, "", Overlay.medium, 14f, Dim, Pad, 906f, Inner, 54f);
        placingHelp.enableWordWrapping = true;
        statsText = Label(panel, "", Overlay.medium, 12.5f, new Color(1f, 1f, 1f, 0.42f), Pad, 964f, Inner, 32f);
        statsText.enableWordWrapping = true;
    }

    // ---------------------------------------------------------------- open, close, per frame

    public static void Open()
    {
        if (root == null) Build();
        if (root == null) return;
        EnsureEventSystem();
        rebinding = -1;
        Refresh();
        root.SetActive(true);
        GameControls.Pause();
        oldLock = Cursor.lockState; oldVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    public static void Close()
    {
        if (!IsOpen) return;
        if (rebinding >= 0) StopRebind();
        root.SetActive(false);
        GameControls.Resume();
        Cursor.lockState = oldLock; Cursor.visible = oldVisible;
    }

    /// While open. Returns true when it used this frame's key press (changing a shortcut).
    public static bool Tick(Keyboard keyboard)
    {
        if (!IsOpen) return false;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None; // the game may grab it back
        if (!Cursor.visible) Cursor.visible = true;
        if (Time.unscaledTime >= nextRefresh) Refresh();
        return rebinding >= 0 && CatchKey(keyboard);
    }

    /// Puts every control in line with the settings and the tripod; cheap enough for twice a second.
    public static void Refresh()
    {
        nextRefresh = Time.unscaledTime + 0.5f;
        var driver = TripodDriver.Instance;
        var recorder = driver?.Recorder;
        bool planted = driver != null && driver.Planted;

        if (recorder != null && recorder.Recording)
        {
            int s = (int)recorder.Seconds;
            SetRecord(Overlay.RecRed, Color.white, $"Stop recording<pos=68%>{s / 60:00}:{s % 60:00}", Color.white, true, Plugin.RecordKey.Value);
            recordIcon.sprite = stopSprite;
        }
        else if (recorder != null && (recorder.Active || recorder.Busy))
            SetRecord(Glass, Color.white, recorder.Busy ? "Saving the video…" : "Getting ready…", Color.clear, false, Plugin.RecordKey.Value);
        else if (planted)
        {
            SetRecord(Overlay.Accent, Overlay.AccentText, "Start recording", Overlay.RecRed, true, Plugin.RecordKey.Value);
            recordIcon.sprite = Overlay.circle;
        }
        else SetRecord(Glass, Color.white, "Place the tripod first", Color.clear, true, Plugin.TripodKey.Value);

        bool keys = driver != null && driver.CurrentMode == TripodDriver.Mode.Keys;
        moveText.text = keys ? "Edit keys" : planted ? "Move tripod" : "Place tripod";
        modeSeg.Select(driver != null ? (int)driver.CurrentMode : 1);
        if (steadyRows.activeSelf == keys)
        {
            steadyRows.SetActive(!keys);
            keyRows.SetActive(keys);
        }
        if (keys)
        {
            var path = driver.Path;
            keysValue.text = path.Count == 0 ? "none yet" : $"{path.Count} key{(path.Count == 1 ? "" : "s")} · {path.End.ToString("0.00", CultureInfo.InvariantCulture)} s";
            endSeg.Select((int)Plugin.PathEndMode.Value);
            string add = KeyName(Plugin.KeyframeKey.Value);
            keysHint.text = path.Count == 0
                ? $"<color=#3DFF7A>Edit keys</color>, fly to a spot and press <color=#3DFF7A>{add}</color>. Each {add} adds the next keyframe {Plugin.KeyGap.Value.ToString("0.#", CultureInfo.InvariantCulture)} s later."
                : $"Recording plays the path from its start. <color=#3DFF7A>{add}</color> while recording starts it over.";
        }
        smoothSlider.SetValueWithoutNotify(Plugin.Smoothing.Value);
        smoothValue.text = Plugin.Smoothing.Value.ToString("0.00", CultureInfo.InvariantCulture);
        float fov = driver != null ? driver.Fov : 60f;
        fovSlider.SetValueWithoutNotify(fov);
        fovValue.text = planted ? $"{fov:0}°" : "–";
        SetGroup(fovGroup, planted);
        previewSwitch.Set(Plugin.Preview.Value);
        soundSwitch.Set(Plugin.Sound.Value);

        int height = Plugin.Height.Value, rate = Plugin.Fps.Value;
        sizeSeg.Select(Array.IndexOf(Heights, height));
        rateSeg.Select(Array.IndexOf(Rates, rate));
        qualitySeg.Select((int)Plugin.Quality.Value);
        lookSeg.Toggles(Plugin.Shadows.Value, Plugin.SmoothEdges.Value, Plugin.HideNames.Value);
        bool locked = recorder != null && (recorder.Active || recorder.Busy);
        SetGroup(videoGroup, !locked);
        int width = Math.Max(16, Plugin.Width.Value / 2 * 2);
        videoInfo.text = $"{width}×{height / 2 * 2} · {Math.Clamp(rate, 15, 60)} fps · {Plugin.Bitrate(width, height, rate) / 1_000_000} Mbps" +
                         (Plugin.Sound.Value ? " · AAC sound 192 kbps\n" : " · no sound\n") +
                         (locked ? "Changes apply to the next video" : "Lighter recording: smaller size, shadows or smooth edges off");
        bool custom = !string.IsNullOrWhiteSpace(Plugin.Folder.Value);
        folderText.text = ShortFolder();
        folderText.rectTransform.sizeDelta = new Vector2(custom ? FolderWidth - 38f : FolderWidth, 40f);
        resetFolder.gameObject.SetActive(custom);
        changeText.text = FolderPicker.Busy ? "Choosing…" : "Change";
        changeBack.color = FolderPicker.Busy ? Soft : Glass;

        menuKeyText.text = KeyName(Plugin.MenuKey.Value);
        Overlay.SetChipText(moveChip, moveKeyText, KeyName(Plugin.TripodKey.Value), 10f);
        for (int i = 0; i < 5; i++) tiles[i].Show(KeyName(Entry(i).Value), rebinding == i);
        placingHelp.text = "Placing: <color=#3DFF7A>WASD</color> fly · <color=#3DFF7A>Q E</color> down/up · <color=#3DFF7A>Mouse</color> look · " +
                           "<color=#3DFF7A>Scroll</color> speed · <color=#3DFF7A>Alt+Scroll</color> zoom · <color=#3DFF7A>Shift</color> fast · " +
                           $"<color=#3DFF7A>Ctrl</color> slow · <color=#3DFF7A>1 2 3 4</color> mode · <color=#3DFF7A>{KeyName(Plugin.ToFlippyKey.Value)}</color> to Flippy · " +
                           "<color=#3DFF7A>Enter</color> plant · <color=#3DFF7A>Esc</color> cancel";
        statsText.text = recorder != null && recorder.Recording && recorder.Stats.Length > 0 ? recorder.Stats
            : recorder != null && recorder.Recording ? "Measuring…" : "Live stats show here while recording.";
    }

    static void SetRecord(Color back, Color text, string label, Color icon, bool showKey, Key key)
    {
        recordBack.color = back;
        recordText.color = text;
        recordText.text = label;
        recordIcon.color = icon;
        recordText.rectTransform.anchoredPosition = new Vector2(icon.a > 0f ? 56f : 22f, 0f);
        recordKeyChip.gameObject.SetActive(showKey);
        if (showKey) recordKeyText.text = KeyName(key);
    }

    static void RecordPressed()
    {
        var driver = TripodDriver.Instance;
        if (driver == null) return;
        if (!driver.Planted && !(driver.Recorder?.Active ?? false)) driver.PlaceFromPanel();
        else driver.ToggleRecording();
        Refresh();
    }

    static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Plugin.VideoFolder);
            Process.Start(new ProcessStartInfo { FileName = Plugin.VideoFolder, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("couldn't open the video folder: " + e.Message);
        }
    }

    static void ChangeFolder()
    {
        if (rebinding >= 0) StopRebind();
        if (FolderPicker.Show(Plugin.VideoFolder)) Refresh();
    }

    static void ResetFolder()
    {
        Plugin.Folder.Value = "";
        FolderChanged();
    }

    /// Every frame (from TripodDriver): once Windows' folder window closes, use the folder that was picked there.
    internal static void TakeFolder()
    {
        if (!FolderPicker.TryTake(out var folder, out var error)) return;
        if (error != null)
        {
            Overlay.Toast(error);
            Plugin.Logger.LogWarning("video folder: " + error);
        }
        else if (folder != null)
        {
            bool isDefault = string.Equals(Path.TrimEndingDirectorySeparator(folder), Plugin.DefaultFolder, StringComparison.OrdinalIgnoreCase);
            Plugin.Folder.Value = isDefault ? "" : folder;
            FolderChanged();
            return;
        }
        if (IsOpen) Refresh();
    }

    static void FolderChanged()
    {
        var recorder = TripodDriver.Instance?.Recorder;
        bool busy = recorder != null && (recorder.Active || recorder.Busy);
        Overlay.Toast($"Videos now save to {ShortFolder()}" + (busy ? " (from the next video)" : ""));
        Plugin.Logger.LogInfo("videos now go to " + Plugin.VideoFolder);
        if (IsOpen) Refresh();
    }

    /// The path as the player thinks of it: from their user folder, and only the last two folders when it's long.
    static string ShortFolder()
    {
        var folder = Plugin.VideoFolder;
        if (folder.Length > 3) folder = Path.TrimEndingDirectorySeparator(folder); // keep "D:\" whole
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\";
        if (folder.StartsWith(home, StringComparison.OrdinalIgnoreCase)) folder = folder.Substring(home.Length);
        var parts = folder.Split('\\');
        return parts.Length > 3 ? "…\\" + parts[^2] + "\\" + parts[^1] : folder;
    }

    // ---------------------------------------------------------------- changing a shortcut

    static BepInEx.Configuration.ConfigEntry<Key> Entry(int i) => i switch
    {
        0 => Plugin.TripodKey,
        1 => Plugin.RecordKey,
        2 => Plugin.MenuKey,
        3 => Plugin.KeyframeKey,
        _ => Plugin.ToFlippyKey,
    };

    static void StartRebind(int index)
    {
        if (rebinding == index) { StopRebind(); return; }
        rebinding = index;
        rebindFrame = Time.frameCount;
        Refresh();
    }

    static void StopRebind()
    {
        rebinding = -1;
        Refresh();
    }

    static bool CatchKey(Keyboard keyboard)
    {
        if (keyboard == null || Time.frameCount == rebindFrame || !keyboard.anyKey.wasPressedThisFrame) return false;
        allKeys ??= (Key[])Enum.GetValues(typeof(Key));
        foreach (var key in allKeys)
        {
            if (key == Key.None) continue;
            bool pressed;
            try { pressed = keyboard[key].wasPressedThisFrame; }
            catch { continue; } // not a real key on this keyboard
            if (!pressed) continue;
            if (key == Key.Escape) { StopRebind(); return true; }
            if (Array.IndexOf(Blocked, key) >= 0)
            {
                Overlay.Toast($"{KeyName(key)} is used by the game or for flying the tripod, pick another key");
                return true;
            }
            var entry = Entry(rebinding);
            var old = entry.Value;
            for (int i = 0; i < 5; i++)
                if (i != rebinding && Entry(i).Value == key) Entry(i).Value = old; // swap with the action that had it
            entry.Value = key;
            Overlay.Toast($"{ShortcutNames[rebinding]} is now {KeyName(key)}");
            StopRebind();
            return true;
        }
        return false;
    }

    internal static string KeyName(Key key)
    {
        string s = key.ToString();
        if (s.StartsWith("Digit")) return s.Substring(5);
        if (s.StartsWith("Numpad")) return "Num " + s.Substring(6);
        return s switch
        {
            "Backquote" => "`", "Minus" => "-", "Equals" => "=", "LeftBracket" => "[", "RightBracket" => "]", "Semicolon" => ";",
            "Quote" => "'", "Comma" => ",", "Period" => ".", "Slash" => "/", "Backslash" => "\\", "PageUp" => "PgUp", "PageDown" => "PgDn",
            _ => s,
        };
    }

    // ---------------------------------------------------------------- pieces

    sealed class Segmented
    {
        readonly Image[] backs;
        readonly TextMeshProUGUI[] labels;

        public Segmented(Transform parent, float x, float y, float w, float h, string[] names, Action<int> pick)
        {
            var bar = At(parent, "Segmented", x, y, w, h);
            Fill(bar, Overlay.small, Soft);
            backs = new Image[names.Length];
            labels = new TextMeshProUGUI[names.Length];
            float sw = (w - 8f) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var seg = At(bar, names[i], 4f + i * sw, 4f, sw, h - 8f);
                backs[i] = Fill(seg, Overlay.small, Color.clear, true);
                labels[i] = Label(seg, names[i], Overlay.semiBold, 18f, Ink, 0f, 0f, sw, h - 8f, TextAlignmentOptions.Center);
                Click(seg.gameObject, backs[i], () => { pick(index); Refresh(); });
            }
        }

        public void Select(int index)
        {
            for (int i = 0; i < backs.Length; i++) Paint(i, i == index);
        }

        public void Toggles(params bool[] on)
        {
            for (int i = 0; i < backs.Length; i++) Paint(i, on[i]);
        }

        void Paint(int i, bool on)
        {
            backs[i].color = on ? Overlay.Accent : Color.clear;
            labels[i].color = on ? Overlay.AccentText : Ink;
        }
    }

    sealed class Switch
    {
        readonly Image track;
        readonly RectTransform knob;

        public Switch(Transform parent, float y, string text, Action<bool> changed)
        {
            var row = At(parent, text, Pad, y, Inner, 32f);
            var hit = Fill(row, null, Color.clear, true);
            Label(row, text, Overlay.medium, 18f, Ink, 0f, 0f, Inner - 70f, 32f);
            track = Fill(At(row, "Track", Inner - 54f, 1f, 54f, 30f), Overlay.small, Overlay.Accent);
            knob = At(track.transform, "Knob", 3f, 3f, 24f, 24f);
            Fill(knob, Overlay.circle, Color.white);
            Click(row.gameObject, hit, () => { changed(!IsOn); Refresh(); });
        }

        bool IsOn => knob.anchoredPosition.x > 10f;

        public void Set(bool on)
        {
            track.color = on ? Overlay.Accent : new Color(1f, 1f, 1f, 0.2f);
            knob.anchoredPosition = new Vector2(on ? 27f : 3f, -3f);
        }
    }

    sealed class KeyTile
    {
        readonly Image back;
        readonly TextMeshProUGUI key, label;
        readonly RectTransform cap;
        readonly string name;
        readonly float width;

        public KeyTile(Transform parent, float x, float y, float w, float h, string name, Action click)
        {
            this.name = name;
            width = w;
            var tile = At(parent, name, x, y, w, h);
            back = Fill(tile, Overlay.small, new Color(0f, 0f, 0f, 0.22f), true);
            cap = KeyCap(tile, Key.None, (w - 40f) / 2f, 10f, out key);
            label = Label(tile, name, Overlay.medium, 15f, Ink, 0f, 44f, w, 22f, TextAlignmentOptions.Center);
            Click(tile.gameObject, back, click);
        }

        public void Show(string keyName, bool waiting)
        {
            back.color = waiting ? new Color(0.239f, 1f, 0.478f, 0.22f) : new Color(0f, 0f, 0f, 0.22f);
            label.text = waiting ? "Press a key…" : name;
            label.color = waiting ? Overlay.Accent : Ink;
            Overlay.SetChipText(cap, key, waiting ? "?" : keyName, 12f);
            cap.anchoredPosition = new Vector2((width - cap.sizeDelta.x) / 2f, -10f);
        }
    }

    /// A small key cap ("T") on a glass chip, sized to its text.
    static RectTransform KeyCap(Transform parent, Key key, float x, float y, out TextMeshProUGUI text)
    {
        var chip = Overlay.Chip(parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), 30f, 10f,
            new Color(1f, 1f, 1f, 0.2f), key == Key.None ? "" : KeyName(key), Overlay.semiBold, 16f, Color.white, out text);
        chip.sizeDelta = new Vector2(Mathf.Max(34f, chip.sizeDelta.x), 30f);
        chip.GetComponent<Image>().sprite = Overlay.small;
        return chip;
    }

    static Slider MakeSlider(Transform parent, float y, float min, float max, Action<float> changed)
    {
        var area = At(parent, "Slider", Pad, y, Inner, 28f);
        Fill(area, null, Color.clear, true);
        Fill(At(area, "Track", 0f, 10f, Inner, 8f), Overlay.small, new Color(1f, 1f, 1f, 0.16f));
        var fillArea = At(area, "Fill Area", 0f, 10f, Inner, 8f);
        var fill = Overlay.Node(fillArea, "Fill");
        Fill(fill, Overlay.small, Overlay.Accent);
        fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f); fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
        var handleArea = At(area, "Handle Area", 12f, 0f, Inner - 24f, 28f);
        var knob = Overlay.Node(handleArea, "Knob");
        var knobImage = Fill(knob, Overlay.circle, Color.white);

        var slider = area.gameObject.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.fillRect = fill;
        slider.handleRect = knob;
        slider.targetGraphic = knobImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = min;
        slider.maxValue = max;
        knob.sizeDelta = new Vector2(24f, -4f); // the slider stretches the knob over the area's height: 28 - 4 = 24
        knob.anchoredPosition = Vector2.zero;
        slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(v => Safe(() => changed(v))));
        return slider;
    }

    static void Section(Transform parent, string text, float y)
    {
        var label = Label(parent, text, Overlay.semiBold, 15f, new Color(1f, 1f, 1f, 0.55f), Pad, y, Inner, 22f);
        label.characterSpacing = 6f;
    }

    static void SetGroup(CanvasGroup group, bool on)
    {
        group.alpha = on ? 1f : 0.4f;
        group.interactable = on;
        group.blocksRaycasts = on;
    }

    // ---------------------------------------------------------------- plumbing

    /// A child laid out from the parent's top-left corner, y going down (like the mockup).
    internal static RectTransform At(Transform parent, string name, float x, float y, float w, float h)
    {
        var rt = Overlay.Node(parent, name);
        Overlay.Anchor(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
        return rt;
    }

    internal static Image Fill(RectTransform rt, Sprite sprite, Color color, bool raycast = false)
    {
        var image = rt.gameObject.AddComponent<Image>();
        if (sprite != null) { image.sprite = sprite; image.type = Image.Type.Sliced; }
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    static TextMeshProUGUI Label(Transform parent, string text, TMP_FontAsset font, float size, Color color, float x, float y, float w, float h,
                                 TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var rt = At(parent, "Text", x, y, w, h);
        var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = align;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    static void Click(GameObject go, Graphic target, Action click)
    {
        var button = go.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.targetGraphic = target;
        target.raycastTarget = true;
        button.onClick.AddListener((UnityAction)new Action(() => Safe(click)));
    }

    static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("TripodCam Events");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        Plugin.Logger.LogInfo("No EventSystem in this scene: added one for the Tripod Cam panel");
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"panel: {e}"); }
    }
}
