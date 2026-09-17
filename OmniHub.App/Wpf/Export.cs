using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// This project sets both UseWindowsForms and UseWPF, so these names exist in both stacks and a
// bare reference is ambiguous. Aliased per file, as the views here already do.
using Brushes = System.Windows.Media.Brushes;

namespace OmniHub.App.Wpf;

/// <summary>
/// Saving a measurement where somebody else can see it.
///
/// Two formats, because they answer different questions. The CSV is what gets opened in a
/// spreadsheet or diffed against next week's; the PNG is what gets pasted into a message, and on
/// a forum thread about a laptop running hot it is the one that actually gets looked at.
///
/// The destination is always a save dialog. These files contain a record of when somebody's
/// machine was running and how hot it got, and a file like that should land where they said and
/// nowhere else.
/// </summary>
internal static class Export
{
    /// <summary>
    /// Writes text to a path the user chooses. Returns the path, or null if they cancelled.
    ///
    /// UTF-8 without a byte-order mark, matching what the logs themselves are written as -- and
    /// deliberately not the BOM-carrying variant, because the reader has to strip one already and
    /// adding another source of them helps nobody.
    /// </summary>
    public static string? SaveText(string suggestedName, string filter, string contents)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog() != true) return null;

        File.WriteAllText(dialog.FileName, contents, new UTF8Encoding(false));
        return dialog.FileName;
    }

    /// <summary>
    /// Renders a control to a PNG at a path the user chooses.
    ///
    /// Rendered at twice the layout size. A chart captured at screen resolution is legible until
    /// somebody views it on a high-density display or scales it up to read an axis label, which
    /// is the first thing anybody does with a chart of somebody else's machine.
    ///
    /// Returns null if the user cancelled or if the control has not been laid out -- an element
    /// with no size renders to nothing, and a zero-byte PNG reported as a success is worse than
    /// an honest refusal.
    /// </summary>
    public static string? SavePng(FrameworkElement element, string suggestedName)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return null;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog() != true) return null;

        const double Scale = 2.0;

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * Scale),
            (int)Math.Ceiling(element.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);

        // Painted onto the theme's own background first. A control whose panels are translucent
        // over the window renders with a transparent ground otherwise, which looks like nothing
        // at all against the white of a message client.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = element.TryFindResource("BackgroundBrush") as Brush ?? Brushes.Black;

            context.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            context.DrawRectangle(new VisualBrush(element), null,
                                  new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);

        return dialog.FileName;
    }
}
