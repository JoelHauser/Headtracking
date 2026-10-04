using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App
{
    public static class AppInfo
    {
        /// <summary>Must match the csproj's Version.</summary>
        public const string Version = "0.7.0";
        public const string Name = "Head Tracking";
    }

    /// <summary>
    /// Where things live. HeadTracking.exe is the only file in the SPT root; everything else it
    /// needs, and everything it writes, is in HeadTrackingApp\ beside it:
    ///   HeadTrackingApp\lib\     the libraries (found by <see cref="LibraryResolver"/>)
    ///   HeadTrackingApp\models\  the face models
    ///   HeadTrackingApp\logs\    logs;  HeadTrackingApp\settings.json, README.md
    /// The plugin is in BepInEx\plugins\HeadTracking\.
    /// </summary>
    public static class AppPaths
    {
        public const string DataFolderName = "HeadTrackingApp";
        public const string LibraryFolderName = "lib";

        public static readonly string ExeDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? AppDomain.CurrentDomain.BaseDirectory);
        public static readonly string DataDirectory = Path.Combine(ExeDirectory, DataFolderName);
        public static readonly string LibraryDirectory = Path.Combine(DataDirectory, LibraryFolderName);
        public static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
        public static readonly string SettingsFile = Path.Combine(DataDirectory, "settings.json");

        public static readonly string PluginFile = Path.Combine(ExeDirectory, "BepInEx", "plugins", "HeadTracking", "HeadTracking.Plugin.dll");

        /// <summary>Where 0.4.0 and earlier put the plugin.</summary>
        public static readonly string LegacyPluginFile = Path.Combine(ExeDirectory, "BepInEx", "plugins", "HeadTracking.Plugin.dll");

        /// <summary>True when the exe is in an SPT install (EscapeFromTarkov.exe beside it).</summary>
        public static bool InSptFolder => File.Exists(Path.Combine(ExeDirectory, "EscapeFromTarkov.exe"));

        public static bool PluginInstalled => File.Exists(PluginFile) || File.Exists(LegacyPluginFile);

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
        }
    }

    /// <summary>
    /// ONNX Runtime is a native DLL. Windows looks for it beside the exe, not in HeadTrackingApp\lib\,
    /// so add that folder to the search path and load it explicitly before the first use; the
    /// managed wrapper's DllImport then finds the loaded module by name. HeadTrackingApp\ itself
    /// (0.4.0's layout) and the exe's folder (a developer build) are tried after.
    /// </summary>
    public static class NativeLibraries
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public static string LoadedFrom { get; private set; }

        /// <summary>
        /// The Windows build of ONNX Runtime reports usage events (process info, model session
        /// creation) through a Microsoft trace provider, which Windows can upload with its own
        /// diagnostic data. Nothing of the camera is in them, but nothing should leave at all:
        /// switched off once, before any model is loaded.
        /// </summary>
        public static string DisableTelemetry()
        {
            try
            {
                OrtEnv.Instance().DisableTelemetryEvents();
                return "ONNX Runtime telemetry switched off.";
            }
            catch (Exception e)
            {
                return "Could not switch off ONNX Runtime telemetry: " + e.Message;
            }
        }

        public static string Error { get; private set; }

        public static void Prepare()
        {
            foreach (string dir in new[] { AppPaths.LibraryDirectory, AppPaths.DataDirectory, AppPaths.ExeDirectory })
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

            Error = "onnxruntime.dll not found in " + AppPaths.LibraryDirectory + ". The webcam tracker needs it; reinstall Head Tracking.";
        }
    }
}
