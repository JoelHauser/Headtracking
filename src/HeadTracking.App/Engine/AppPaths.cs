using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HeadTracking.App
{
    public static class AppInfo
    {
        /// <summary>Must match the csproj's Version.</summary>
        public const string Version = "0.5.0";
        public const string Name = "Head Tracking";
    }

    /// <summary>
    /// Where things live. HeadTracking.exe sits in the SPT root; everything else it needs, and
    /// everything it writes, is in HeadTrackingApp\ beside it.
    /// </summary>
    public static class AppPaths
    {
        public static readonly string ExeDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? AppDomain.CurrentDomain.BaseDirectory);
        public static readonly string DataDirectory = Path.Combine(ExeDirectory, "HeadTrackingApp");
        public static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
        public static readonly string SettingsFile = Path.Combine(DataDirectory, "settings.json");

        /// <summary>True when the exe is in an SPT install (EscapeFromTarkov.exe beside it).</summary>
        public static bool InSptFolder => File.Exists(Path.Combine(ExeDirectory, "EscapeFromTarkov.exe"));

        public static bool PluginInstalled => File.Exists(Path.Combine(ExeDirectory, "BepInEx", "plugins", "HeadTracking.Plugin.dll"));

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
        }
    }

    /// <summary>
    /// ONNX Runtime is a native DLL. Windows looks for it beside the exe, not in HeadTrackingApp\,
    /// so add that folder to the search path and load it explicitly before the first use; the
    /// managed wrapper's DllImport then finds the loaded module by name.
    /// </summary>
    public static class NativeLibraries
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public static string LoadedFrom { get; private set; }

        public static string Error { get; private set; }

        public static void Prepare()
        {
            foreach (string dir in new[] { AppPaths.DataDirectory, AppPaths.ExeDirectory })
            {
                string path = Path.Combine(dir, "onnxruntime.dll");
                if (!File.Exists(path))
                {
                    continue;
                }

                SetDllDirectory(dir);
                if (LoadLibrary(path) != IntPtr.Zero)
                {
                    LoadedFrom = path;
                    return;
                }

                int code = Marshal.GetLastWin32Error();
                Error = "onnxruntime.dll found at " + path + " but Windows could not load it (error " + code + ")"
                        + (code == 126 ? ": a dependency is missing; install the Microsoft Visual C++ 2015-2022 Redistributable (x64)." : ".");
                return;
            }

            Error = "onnxruntime.dll not found in " + AppPaths.DataDirectory + ". The webcam tracker needs it; reinstall Head Tracking.";
        }
    }
}
