using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LocalAppManager.Controls;

/// <summary>Minimal left-to-right wrapping panel (for the script chips).</summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(6.0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > availableSize.Width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            x += s.Width + Spacing;
            width = Math.Max(width, x - Spacing);
            rowHeight = Math.Max(rowHeight, s.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > finalSize.Width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            child.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Spacing;
            rowHeight = Math.Max(rowHeight, s.Height);
        }
        return finalSize;
    }
}
