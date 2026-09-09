using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// One tunable knob on the Tuning tab: a label, a range, a value, and whether it is to be sent.
///
/// This exists so the row has an object to bind to. The tab built every row by hand -- a
/// four-column grid, a checkbox, a label, a slider and a readout, constructed in C# for each of
/// roughly fourteen knobs -- so its appearance could only be changed by editing imperative code,
/// and the view's entire state model was two dictionaries of live controls. Reading a value meant
/// reaching into a Slider: the layout and the data were the same object.
///
/// With a model, the row's appearance is a DataTemplate in the markup and its state lives here.
/// </summary>
public sealed class KnobRow : INotifyPropertyChanged
{
    public KnobRow(string key, string label, double minimum, double maximum, double value,
                   string unit, string tip, bool enabled, bool showEnable)
    {
        Key = key;
        Label = label;
        Minimum = minimum;
        Maximum = maximum;
        _value = Math.Clamp(value, minimum, maximum);
        Unit = unit;
        Tip = tip;
        _enabled = enabled;
        ShowEnable = showEnable;
    }

    public string Key { get; }
    public string Label { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public string Unit { get; }
    public string Tip { get; }
    public bool ShowEnable { get; }

    /// <summary>
    /// Whether the checkbox column is shown. Exposed as a Visibility rather than a bool so the
    /// template needs no converter, which keeps the whole row bindable from markup alone.
    /// </summary>
    public Visibility EnableVisibility => ShowEnable ? Visibility.Visible : Visibility.Hidden;

    private double _value;

    public double Value
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(clamped - _value) < 0.0001) return;

            _value = clamped;
            Raise();
            Raise(nameof(Readout));

            // Touching a slider is itself the intent to set that knob, so it ticks its own box.
            // Requiring both actions separately meant silently dropped changes.
            if (ShowEnable) Enabled = true;

            ValueChanged?.Invoke();
        }
    }

    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise();
        }
    }

    /// <summary>The value as it appears beside the slider.</summary>
    public string Readout => $"{Value:0} {Unit}";

    /// <summary>
    /// Raised after <see cref="Value"/> changes. Replaces subscribing to a Slider's own
    /// ValueChanged, which is how the adaptive sliders and the thermal limit observed each other.
    /// </summary>
    public event Action? ValueChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// The value to send, or null when this knob is not selected.
    ///
    /// The null is load-bearing: a profile leaves out what it does not set, so "not selected" and
    /// "selected at zero" have to stay distinguishable all the way to the SMU.
    /// </summary>
    public int? Selected => Enabled ? (int)Math.Round(Value) : null;
}
