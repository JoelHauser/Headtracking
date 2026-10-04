using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace HeadTracking.App.UI
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private const uint WdaNone = 0, WdaMonitor = 1, WdaExcludeFromCapture = 0x11;
        private readonly MainViewModel _viewModel;
        private string _captureState;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Title = "Head Tracking " + AppInfo.Version;
            _viewModel = viewModel;
            SourceInitialized += (s, e) =>
            {
                UseDarkTitleBar();
                ApplyCaptureProtection();
            };
            viewModel.Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.HideFromCapture))
                {
                    ApplyCaptureProtection();
                }
            };
            viewModel.OwnerHandle = () => new WindowInteropHelper(this).Handle;
            StateChanged += (s, e) => viewModel.Minimized = WindowState == WindowState.Minimized;
            Activated += (s, e) => viewModel.WindowActive = true;
            Deactivated += (s, e) => viewModel.WindowActive = false;
        }

        /// <summary>
        /// Leaves this window out of every screen capture (OBS, Discord or Teams screen share, the
        /// Snipping Tool, PrintScreen) so the camera preview cannot end up on a stream. You still see
        /// it normally. Windows 10 2004 and later remove it from the capture; older Windows show it
        /// black there instead.
        /// </summary>
        private void ApplyCaptureProtection()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            string state;
            if (!_viewModel.Settings.HideFromCapture)
            {
                SetWindowDisplayAffinity(hwnd, WdaNone);
                state = "Screen capture of this window allowed (Hide from capture is off).";
            }
            else if (SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                state = "This window is hidden from screen capture and streams.";
            }
            else if (SetWindowDisplayAffinity(hwnd, WdaMonitor))
            {
                state = "This window shows black in screen captures (this Windows cannot hide it entirely).";
            }
            else
            {
                state = "Could not protect this window from screen capture (error " + Marshal.GetLastWin32Error() + ").";
            }

            if (state != _captureState)
            {
                _captureState = state;
                _viewModel.Note(state);
            }
        }

        /// <summary>A dark title bar to match (Windows 10 2004 and later; harmless elsewhere).</summary>
        private void UseDarkTitleBar()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
                }
            }
            catch (Exception)
            {
                // Cosmetic only.
            }
        }

        /// <summary>For --snapshot: show a given page.</summary>
        public void ShowPage(int index)
        {
            Nav.SelectedIndex = index;
        }
    }

    /// <summary>Keeps a ListBox scrolled to its newest item while items are added.</summary>
    public static class AutoScroll
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListBox list) || !(bool)e.NewValue)
            {
                return;
            }

            ((INotifyCollectionChanged)list.Items).CollectionChanged += (s, args) =>
            {
                if (list.Items.Count > 0)
                {
                    list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                }
            };
        }
    }
}
