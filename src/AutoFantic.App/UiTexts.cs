using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AutoFantic.Core;

namespace AutoFantic.App;

/// <summary>
/// Translates the texts written in a window's XAML (TextBlocks, buttons, check boxes, tooltips …)
/// once, right after it's loaded, so the XAML itself stays in English. Texts the code sets later
/// go through <see cref="Texts.T(string)"/> themselves.
/// </summary>
internal static class UiTexts
{
    public static void Translate(DependencyObject root)
    {
        if (!Texts.German)
            return;
        Walk(root);
    }

    private static void Walk(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock { Inlines.Count: > 1 } block:
                foreach (var run in block.Inlines.OfType<Run>())
                    run.Text = Texts.T(run.Text);
                break;
            case TextBlock block when !string.IsNullOrEmpty(block.Text):
                block.Text = Texts.T(block.Text);
                break;
            case HeaderedContentControl { Header: string header } headered:
                headered.Header = Texts.T(header);
                break;
        }
        if (element is ContentControl { Content: string content } control)
            control.Content = Texts.T(content);
        if (element is FrameworkElement { ToolTip: string tip } withTip)
            withTip.ToolTip = Texts.T(tip);

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Walk(child);
    }
}
