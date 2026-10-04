using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeadTracking.App.UI;

namespace HeadTracking.App
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private AppLog _log;
        private AppSettings _settings;
        private TrackingEngine _engine;
        private MainViewModel _viewModel;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppPaths.EnsureDirectories();
            _log = new AppLog(AppPaths.LogDirectory);
            _log.Info(AppInfo.Name + " " + AppInfo.Version + " starting on " + Environment.OSVersion + ", .NET " + Environment.Version + ".");
            _log.Info("Folder: " + AppPaths.ExeDirectory + (AppPaths.InSptFolder ? " (SPT install)" : " (not an SPT install)")
                      + "; plugin " + (AppPaths.PluginInstalled ? "found" : "NOT found") + " in BepInEx\\plugins\\HeadTracking.");
            LegacyFiles.Clean(_log);

            DispatcherUnhandledException += (s, args) =>
            {
                _log.Error("Unhandled UI error: " + args.Exception);
                MessageBox.Show("Head Tracking hit an error and logged it:\n\n" + args.Exception.Message + "\n\nLog: " + _log.FilePath,
                    "Head Tracking", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => _log.Error("Unhandled error: " + args.ExceptionObject);

            string[] args = e.Args;
            NativeLibraries.Prepare();
            _log.Info(NativeLibraries.Error == null ? "ONNX Runtime loaded from " + NativeLibraries.LoadedFrom + "." : NativeLibraries.Error);

            if (args.Contains("--test-image"))
            {
                int code = Dev.StillImageTest.Run(args, _log);
                Shutdown(code);
                return;
            }

            if (args.Contains("--jitter-test"))
            {
                Shutdown(Dev.JitterTest.Run(args, _log));
                return;
            }

            if (args.Contains("--benchmark"))
            {
                Shutdown(Dev.Benchmark.Run(args, _log));
                return;
            }

            if (args.Contains("--replay"))
            {
                Shutdown(Dev.Replay.Run(args, _log));
                return;
            }

            if (args.Contains("--live-jitter"))
            {
                Shutdown(System.Threading.Tasks.Task.Run(() => Dev.LiveJitter.Run(args, _log)).GetAwaiter().GetResult());
                return;
            }

            if (args.Contains("--camera-test"))
            {
                // Off the UI thread: FlashCap's open/start continue on the caller's context, and
                // blocking the dispatcher on them deadlocks.
                Shutdown(System.Threading.Tasks.Task.Run(() => Dev.CameraTest.Run(args, _log)).GetAwaiter().GetResult());
                return;
            }

            _singleInstance = new Mutex(true, "Local\\HeadTracking.App.SingleInstance", out bool first);
            if (!first)
            {
                _log.Warn("Another HeadTracking.exe is already running; this one exits.");
                MessageBox.Show("Head Tracking is already running. Look for it in the taskbar.", "Head Tracking", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            _settings = AppSettings.Load(AppPaths.SettingsFile, _log);
            if (_settings.Migrate(_log))
            {
                _settings.Save(AppPaths.SettingsFile, _log);
            }
            _engine = new TrackingEngine(_settings, _log);
            _engine.Start();

            _viewModel = new MainViewModel(_settings, _engine, _log);
            MainWindow window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();

            int snapshot = Array.IndexOf(args, "--snapshot");
            if (snapshot >= 0 && snapshot + 1 < args.Length)
            {
                TakeSnapshots(window, args[snapshot + 1]);
            }
        }

        /// <summary>Developer aid: renders each page to a PNG, then exits.</summary>
        private void TakeSnapshots(MainWindow window, string directory)
        {
            Directory.CreateDirectory(directory);
            // Tall enough that each page renders whole, not just what fits on screen.
            window.Height = 1450;
            int page = 0;
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (s, e) =>
            {
                string path = Path.Combine(directory, "page" + page + ".png");
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render((Visual)window.Content);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                _log.Info("Snapshot written: " + path);
                page++;
                if (page > 4)
                {
                    timer.Stop();
                    window.Close();
                    return;
                }

                window.ShowPage(page);
                timer.Interval = TimeSpan.FromSeconds(1);
            };
            timer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _viewModel?.SaveNow();
            _engine?.Dispose();
            _log?.Info("Exiting.");
            _log?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
