using System;
using System.IO;

namespace HeadTracking.App
{
    /// <summary>
    /// Tidies what older versions left in the SPT folder after the 0.5.0 layout change: unzipping a
    /// new release over an old one adds files but cannot remove any. Only our own files, at the
    /// exact paths older versions used, and only once the new layout is in place.
    /// </summary>
    public static class LegacyFiles
    {
        public static void Clean(AppLog log)
        {
            if (!Directory.Exists(AppPaths.LibraryDirectory))
            {
                // A developer build or an incomplete install: nothing has moved, leave it be.
                return;
            }

            string root = AppPaths.ExeDirectory;
            int removed = 0, busy = 0;

            void Remove(string path)
            {
                if (!File.Exists(path))
                {
                    return;
                }

                try
                {
                    File.Delete(path);
                    removed++;
                    log.Info("Removed an old-layout file: " + path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    busy++;
                    log.Info("Could not remove the old-layout file " + path + " yet (" + e.Message.Trim() + "); will try again next start.");
                }
            }

            // Up to 0.4.0: a config and the README beside the exe, libraries loose in HeadTrackingApp\.
            Remove(Path.Combine(root, "HeadTracking.exe.config"));
            Remove(Path.Combine(root, "HeadTracking-README.md"));
            foreach (string dll in Directory.GetFiles(AppPaths.DataDirectory, "*.dll"))
            {
                if (File.Exists(Path.Combine(AppPaths.LibraryDirectory, Path.GetFileName(dll))))
                {
                    Remove(dll);
                }
            }

            // Up to 0.4.0 the plugin sat loose in BepInEx\plugins; two copies would both load.
            if (File.Exists(AppPaths.PluginFile))
            {
                Remove(AppPaths.LegacyPluginFile);
            }

            if (removed > 0 || busy > 0)
            {
                log.Info("Old-layout tidy-up: " + removed + " file(s) removed" + (busy > 0 ? ", " + busy + " still in use (the game may be running)" : "") + ".");
            }
        }
    }
}
