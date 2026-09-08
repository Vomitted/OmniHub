using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OmniHub.App.Wpf;

/// <summary>
/// Eases a numeric readout to its new value instead of snapping to it.
///
/// This is the single biggest difference between a telemetry UI that feels considered and one
/// that reads as a debug printout. The gauge here already sweeps its arc smoothly while the
/// number in the middle of it jumped -- two things animating to different standards, which
/// looks unfinished even to someone who could not say why.
///
/// Implemented as an attached property because WPF cannot animate TextBlock.Text directly: the
/// animation drives a double, and the property-changed callback formats it into the text. One
/// helper, reused by every readout, so the timing is identical everywhere rather than being
/// re-chosen per call site.
///
/// Deliberately NOT for everything. Values that are already smooth -- a filtered temperature --
/// benefit. Values that are genuinely discrete, like a fan RAW level, look worse rolling
/// through numbers the hardware never reported.
/// </summary>
public static class Animate
{
    /// <summary>
    /// How long a value takes to travel to its new reading.
    ///
    /// 220ms, down from 650. The longer figure looked better in isolation and made the whole
    /// app feel slow in use: this is an instrument, and a reading that takes two thirds of a
    /// second to arrive is a reading you wait for. Polish bought with immediacy is a bad trade
    /// on a gauge, however well the motion reads on its own.
    ///
    /// Short enough to register as a response, long enough to still be a movement rather than
    /// a jump, which was the point of easing these at all.
    /// </summary>
    private static readonly Duration Travel = new(TimeSpan.FromMilliseconds(220));

    /// <summary>
    /// Eased rather than linear, and EaseOut rather than EaseInOut: a readout should leave the
    /// old value immediately when the hardware changes and settle gently, not creep away from
    /// it. CubicEase is the mildest curve that still reads as deliberate.
    /// </summary>
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(Animate),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(string), typeof(Animate), new PropertyMetadata("0"));

    public static double GetValue(DependencyObject o) => (double)o.GetValue(ValueProperty);
    public static void SetValue(DependencyObject o, double v) => o.SetValue(ValueProperty, v);
    public static string GetFormat(DependencyObject o) => (string)o.GetValue(FormatProperty);
    public static void SetFormat(DependencyObject o, string v) => o.SetValue(FormatProperty, v);

    private static void OnValueChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock text) return;
        double v = (double)e.NewValue;
        if (double.IsNaN(v)) return;

        // InvariantCulture: these are instrument readings sitting next to a unit, and a decimal
        // comma beside "°C" reads as a thousands separator to half the world.
        text.Text = v.ToString(GetFormat(o), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Moves <paramref name="target"/> to <paramref name="to"/>, formatted with
    /// <paramref name="format"/>.
    ///
    /// The first value on a control arrives instantly rather than counting up from zero: a
    /// readout sweeping 0 -> 84 on launch is theatre, and on the way it displays temperatures
    /// the machine is not at.
    /// </summary>
    public static void To(TextBlock target, double to, string format = "0")
    {
        SetFormat(target, format);

        double from = GetValue(target);
        if (double.IsNaN(from) || Math.Abs(to - from) < 0.05)
        {
            target.BeginAnimation(ValueProperty, null);
            SetValue(target, to);
            return;
        }

        target.BeginAnimation(ValueProperty, new DoubleAnimation(from, to, Travel) { EasingFunction = Ease });
    }

    /// <summary>
    /// How long a colour takes to cross a threshold. 240ms, down from 450, for the same reason
    /// as Travel: it is still slower than the number roll, because a colour is a judgement
    /// about severity and should not flicker, but it no longer lags the value it describes.
    /// </summary>
    private static readonly Duration Tint = new(TimeSpan.FromMilliseconds(240));

    /// <summary>
    /// Eases a brush property to a new colour instead of switching it.
    ///
    /// Thermal colours were assigned outright, so crossing 60 C or 80 C flipped a whole card
    /// from grey to amber to red between one poll and the next. On a value that is itself
    /// smooth that hard cut is the most jarring thing on screen, and it invites reading a
    /// one-degree wobble around a threshold as an event.
    ///
    /// Resource brushes are frozen and shared, so animating one in place would recolour every
    /// element using it. This gives the element its own SolidColorBrush, seeded from whatever
    /// it is currently showing, and animates that -- the shared resource is left alone.
    ///
    /// Slower than the number roll on purpose. A colour is a judgement about severity; it
    /// should drift rather than snap to attention.
    /// </summary>
    public static void BrushTo(DependencyObject target, DependencyProperty property, Brush to)
    {
        if (to is not SolidColorBrush wanted)
        {
            target.SetValue(property, to);
            return;
        }

        var existing = target.GetValue(property) as SolidColorBrush;
        SolidColorBrush current;

        if (existing is null || existing.IsFrozen)
        {
            // Seeded from the colour already on screen, so the first transition starts where
            // the eye is rather than at the destination.
            current = new SolidColorBrush(existing?.Color ?? wanted.Color);
            target.SetValue(property, current);
        }
        else current = existing;

        if (current.Color == wanted.Color) return;
        current.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(wanted.Color, Tint) { EasingFunction = Ease });
    }

    /// <summary>
    /// Puts a non-numeric state on the readout -- "--" for a reading that is unavailable.
    ///
    /// Cancels any animation in flight and clears the stored value, so the NEXT real reading
    /// arrives instantly instead of sweeping up from whatever was last shown. Animating out of
    /// an unavailable state would be inventing every number in between.
    /// </summary>
    public static void Clear(TextBlock target, string placeholder = "--")
    {
        target.BeginAnimation(ValueProperty, null);
        target.SetValue(ValueProperty, double.NaN);
        target.Text = placeholder;
    }

    /// <summary>
    /// The one way to put a reading on a numeric readout: the number when there is one, the
    /// unavailable placeholder when there is not.
    ///
    /// This exists to make the project's strictest rule structural rather than conventional. "A
    /// value the hardware did not report is never shown as a number" was held up by roughly forty
    /// hand-written "--" literals spread across eight files, plus whoever was reviewing at the
    /// time. Only two sites went through Clear, which is the path that also cancels the animation
    /// in flight and resets the stored value -- so every site that forgot it left the next real
    /// reading sweeping up from a stale number, displaying temperatures the machine was never at
    /// on the way.
    ///
    /// NaN and infinity route to the placeholder rather than to the formatter. They arrive from
    /// arithmetic on a missing input -- a percentage of a zero total, most often -- and "NaN"
    /// beside a unit reads as an instrument fault rather than as an honest absence.
    /// </summary>
    public static void SetReading(TextBlock target, double? value, string format = "0",
                                  string placeholder = "--")
    {
        if (value is double v && !double.IsNaN(v) && !double.IsInfinity(v)) To(target, v, format);
        else Clear(target, placeholder);
    }
}
