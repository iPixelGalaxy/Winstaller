using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Winstaller.Gui;

internal sealed class LogScrollBar : Grid
{
    private const double ThumbThickness = 12;
    private const double MinimumThumbLength = 40;

    private readonly Orientation _orientation;
    private readonly Canvas _canvas = new();
    private readonly Thumb _thumb;
    private double _maximum;
    private double _viewportSize;
    private double _value;

    public event Action<double>? ValueChanged;

    public LogScrollBar(Orientation orientation, Brush trackBrush, Brush thumbBrush)
    {
        _orientation = orientation;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var track = new Border
        {
            Background = trackBrush,
            Opacity = 0.5,
            CornerRadius = new CornerRadius(2),
            Margin = orientation == Orientation.Vertical
                ? new Thickness(7, 2, 7, 2)
                : new Thickness(2, 7, 2, 7)
        };
        _thumb = new Thumb
        {
            Background = thumbBrush,
            Opacity = 0.82,
            CornerRadius = new CornerRadius(6),
            Width = orientation == Orientation.Vertical ? ThumbThickness : MinimumThumbLength,
            Height = orientation == Orientation.Vertical ? MinimumThumbLength : ThumbThickness
        };
        _thumb.PointerEntered += (_, _) => _thumb.Opacity = 1;
        _thumb.PointerExited += (_, _) => _thumb.Opacity = 0.82;
        _thumb.DragDelta += (_, args) =>
        {
            var delta = _orientation == Orientation.Vertical ? args.VerticalChange : args.HorizontalChange;
            var usableLength = GetUsableLength();
            if (usableLength > 0 && _maximum > 0)
                SetValue(_value + (delta / usableLength * _maximum), notify: true);
        };

        track.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(this).Position;
            var coordinate = _orientation == Orientation.Vertical ? point.Y : point.X;
            var usableLength = GetUsableLength();
            var thumbLength = GetThumbLength();
            if (usableLength > 0 && _maximum > 0)
                SetValue((coordinate - thumbLength / 2) / usableLength * _maximum, notify: true);
            args.Handled = true;
        };
        PointerWheelChanged += (_, args) =>
        {
            if (_orientation != Orientation.Vertical || _maximum <= 0)
                return;

            var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
            SetValue(_value - Math.Sign(delta) * 48, notify: true);
            args.Handled = true;
        };

        Children.Add(track);
        Children.Add(_canvas);
        _canvas.Children.Add(_thumb);
        SizeChanged += (_, _) => UpdateThumb();
    }

    public void SetMetrics(double maximum, double viewportSize, double value)
    {
        _maximum = Math.Max(0, maximum);
        _viewportSize = Math.Max(0, viewportSize);
        SetValue(value, notify: false);
    }

    private void SetValue(double value, bool notify)
    {
        var clamped = Math.Clamp(value, 0, _maximum);
        if (Math.Abs(clamped - _value) < 0.01)
        {
            UpdateThumb();
            return;
        }

        _value = clamped;
        UpdateThumb();
        if (notify)
            ValueChanged?.Invoke(_value);
    }

    private void UpdateThumb()
    {
        var length = GetLength();
        if (length <= 0)
            return;

        var thumbLength = GetThumbLength();
        var usableLength = Math.Max(0, length - thumbLength);
        var position = _maximum <= 0 ? 0 : usableLength * (_value / _maximum);

        if (_orientation == Orientation.Vertical)
        {
            _thumb.Width = Math.Min(ThumbThickness, ActualWidth);
            _thumb.Height = thumbLength;
            Canvas.SetLeft(_thumb, Math.Max(0, (ActualWidth - _thumb.Width) / 2));
            Canvas.SetTop(_thumb, position);
        }
        else
        {
            _thumb.Width = thumbLength;
            _thumb.Height = Math.Min(ThumbThickness, ActualHeight);
            Canvas.SetLeft(_thumb, position);
            Canvas.SetTop(_thumb, Math.Max(0, (ActualHeight - _thumb.Height) / 2));
        }
    }

    private double GetLength() => _orientation == Orientation.Vertical ? ActualHeight : ActualWidth;

    private double GetThumbLength()
    {
        var length = GetLength();
        if (length <= 0 || _maximum <= 0)
            return Math.Max(0, length);

        var extent = _maximum + _viewportSize;
        var proportionalLength = extent <= 0 ? length : length * (_viewportSize / extent);
        return Math.Min(length, Math.Max(MinimumThumbLength, proportionalLength));
    }

    private double GetUsableLength() => Math.Max(0, GetLength() - GetThumbLength());
}
