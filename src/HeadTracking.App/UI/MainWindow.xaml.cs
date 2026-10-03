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

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Title = "Head Tracking " + AppInfo.Version;
            SourceInitialized += (s, e) => UseDarkTitleBar();
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
