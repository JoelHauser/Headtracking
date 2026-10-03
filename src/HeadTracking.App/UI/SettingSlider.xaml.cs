using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace HeadTracking.App.UI
{
    /// <summary>A labelled slider with its value and unit on the right and a hint underneath.</summary>
    public partial class SettingSlider : UserControl
    {
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingSlider));
        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SettingSlider),
            new PropertyMetadata(null, (d, e) => ((SettingSlider)d).OnHintChanged()));
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SettingSlider), new PropertyMetadata(0.0));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SettingSlider), new PropertyMetadata(1.0));
        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(SettingSlider), new PropertyMetadata(0.1));
        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(SettingSlider),
            new PropertyMetadata("0.0", (d, e) => ((SettingSlider)d).UpdateText()));
        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SettingSlider),
            new PropertyMetadata("", (d, e) => ((SettingSlider)d).UpdateText()));
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SettingSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SettingSlider)d).UpdateText()));

        private static readonly DependencyPropertyKey ValueTextKey = DependencyProperty.RegisterReadOnly(nameof(ValueText), typeof(string), typeof(SettingSlider), new PropertyMetadata(""));
        public static readonly DependencyProperty ValueTextProperty = ValueTextKey.DependencyProperty;
        private static readonly DependencyPropertyKey HintVisibilityKey = DependencyProperty.RegisterReadOnly(nameof(HintVisibility), typeof(Visibility), typeof(SettingSlider), new PropertyMetadata(Visibility.Collapsed));
        public static readonly DependencyProperty HintVisibilityProperty = HintVisibilityKey.DependencyProperty;

        public SettingSlider()
        {
            InitializeComponent();
            UpdateText();
        }

        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
        public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public string ValueText => (string)GetValue(ValueTextProperty);
        public Visibility HintVisibility => (Visibility)GetValue(HintVisibilityProperty);

        private void UpdateText()
        {
            string unit = Unit ?? "";
            string separator = unit.Length == 0 || unit.StartsWith("°") ? "" : " ";
            SetValue(ValueTextKey, Value.ToString(Format ?? "0.0", CultureInfo.CurrentCulture) + separator + unit);
        }

        private void OnHintChanged()
        {
            SetValue(HintVisibilityKey, string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible);
        }
    }
}
