using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LocalAppManager.Controls;

/// <summary>Thin vertical strip that resizes a column by dragging (with a resize cursor).</summary>
public sealed partial class ColumnSplitter : Grid
{
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.Register(nameof(Target), typeof(ColumnDefinition), typeof(ColumnSplitter), new PropertyMetadata(null));

    private double _startWidth;
    private double _startX;
    private bool _dragging;

    public ColumnSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    public ColumnDefinition? Target
    {
        get => (ColumnDefinition?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Raised when a drag finishes, with the new width.</summary>
    public event Action<double>? Resized;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Target is null) return;
        _dragging = CapturePointer(e.Pointer);
        _startWidth = Target.ActualWidth;
        _startX = e.GetCurrentPoint(XamlRoot.Content).Position.X;
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Target is null) return;
        double x = e.GetCurrentPoint(XamlRoot.Content).Position.X;
        double width = Math.Clamp(_startWidth + (x - _startX), Target.MinWidth, Target.MaxWidth);
        Target.Width = new GridLength(width);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (Target is not null) Resized?.Invoke(Target.ActualWidth);
    }
}

/// <summary>Lets x:Bind two-way bind object-typed properties (SelectedItem) to typed view-model properties.</summary>
public sealed partial class PassThroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value;
}
