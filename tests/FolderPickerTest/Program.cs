using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using TripodCam;

// Runs FolderPicker for real, outside the game: opens Windows' folder window, then presses its OK button (expects the
// start folder back) and closes it (expects a cancel), and checks the write test on a writable and a locked folder.
// Each window is on screen for about a second. dotnet FolderPickerTest.dll <a writable test folder>
static class Program
{
    const string Title = "Where should Tripod Cam save videos?";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    static int failures;

    static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(folder);

        // OK with nothing selected picks the folder it's showing
        Run(folder, (hwnd) =>
        {
            Expect(FindWindowEx(hwnd, IntPtr.Zero, "Button", "Save videos here") != IntPtr.Zero, "OK button says \"Save videos here\"");
            PostMessage(hwnd, 0x0111, (IntPtr)1, IntPtr.Zero); // WM_COMMAND IDOK
        }, (picked, error) =>
        {
            Expect(error == null, $"no error (got {error})");
            Expect(picked != null && string.Equals(Path.TrimEndingDirectorySeparator(picked), folder, StringComparison.OrdinalIgnoreCase),
                $"picked the start folder (got {picked})");
        });

        // closing it is a cancel: no folder, no error
        Run(Path.Combine(folder, "does", "not", "exist"), (hwnd) => PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero), // WM_CLOSE
            (picked, error) => Expect(picked == null && error == null, $"cancel gives nothing (got {picked} / {error})"));

        var cantWrite = typeof(FolderPicker).GetMethod("CantWrite", BindingFlags.NonPublic | BindingFlags.Static);
        Expect(cantWrite.Invoke(null, new object[] { folder }) == null, "the test folder is writable");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var locked = (string)cantWrite.Invoke(null, new object[] { system });
        Expect(locked != null, $"{system} is refused: {locked}");
        Expect(Directory.GetFiles(folder, ".tripodcam-*").Length == 0, "the write test leaves no file behind");

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
        return failures;
    }

    static void Run(string start, Action<IntPtr> act, Action<string, string> check)
    {
        Expect(FolderPicker.Show(start), "Show starts the window");
        Expect(FolderPicker.Busy && !FolderPicker.Show(start), "a second Show is refused while it's open");
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(100); hwnd = FindWindow("#32770", Title); }
        Expect(hwnd != IntPtr.Zero, "the window opened with Tripod Cam's title");
        if (hwnd == IntPtr.Zero) Environment.Exit(1);
        Thread.Sleep(1000); // let it finish showing the folder
        act(hwnd);
        string picked = null, error = null;
        bool done = false;
        for (int i = 0; i < 100 && !done; i++) { Thread.Sleep(100); done = FolderPicker.TryTake(out picked, out error); }
        Expect(done, "TryTake reports the window closed");
        Expect(!FolderPicker.Busy, "Busy is off afterwards");
        Expect(!FolderPicker.TryTake(out _, out _), "the answer is handed out once");
        check(picked, error);
    }

    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) failures++;
    }
}
