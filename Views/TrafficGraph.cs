using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Valve.Core;

namespace Valve.Views;

/// <summary>
/// Rolling two-series traffic graph with interactive pan, zoom, point inspection,
/// stream toggles, dual Y-axis scaling, and pause/resume (Sections 9.01 - 9.09 of NetLimiter feature set).
/// </summary>
public sealed class TrafficGraph : FrameworkElement
{
    private IReadOnlyList<GraphSample> _samples = Array.Empty<GraphSample>();
    private double _peak = 1024;
    private double _peakIn = 1024;
    private double _peakOut = 1024;

    private double _hoverX = -1;
    private int _hoverIndex = -1;
    private double _zoomFactor = 1.0; // 1.0 = normal 60s, 2.0 = 30s zoom, 0.5 = 120s
    private int _panOffsetSamples = 0;

    private bool _isPaused;
    private bool _dualYAxis = true;
    private bool _showDownload = true;
    private bool _showUpload = true;

    // Gesture tracking
    private bool _isPanning;
    private Point _panStart;
    private int _panStartOffset;

    private bool _isZoomDragging;
    private Point _zoomStart;
    private double _zoomStartFactor;

    private bool _isSelecting;
    private double _selectionStartX = -1;
    private double _selectionEndX = -1;

    public TrafficGraph()
    {
        Focusable = true;
    }

    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; InvalidateVisual(); }
    }

    public bool DualYAxis
    {
        get => _dualYAxis;
        set { _dualYAxis = value; InvalidateVisual(); }
    }

    public bool ShowDownload
    {
        get => _showDownload;
        set { _showDownload = value; InvalidateVisual(); }
    }

    public bool ShowUpload
    {
        get => _showUpload;
        set { _showUpload = value; InvalidateVisual(); }
    }

    public void TogglePause() => IsPaused = !IsPaused;

    public void SetData(IReadOnlyList<GraphSample> samples)
    {
        if (_isPaused) return;
        _samples = samples;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_samples.Count < 2) return;

        if (e.Key == Key.Left)
        {
            _hoverIndex = Math.Max(0, (_hoverIndex < 0 ? _samples.Count - 1 : _hoverIndex) - 1);
            UpdateHoverTooltip();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            _hoverIndex = Math.Min(_samples.Count - 1, (_hoverIndex < 0 ? 0 : _hoverIndex) + 1);
            UpdateHoverTooltip();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point pos = e.GetPosition(this);
        _panStart = pos;
        _panStartOffset = _panOffsetSamples;
        _isPanning = true;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Point pos = e.GetPosition(this);
        _zoomStart = pos;
        _zoomStartFactor = _zoomFactor;
        _isZoomDragging = true;
        CaptureMouse();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Point pos = e.GetPosition(this);
        if (_isZoomDragging)
        {
            _isZoomDragging = false;
            ReleaseMouseCapture();
            if (Math.Abs(pos.Y - _zoomStart.Y) < 4 && Math.Abs(pos.X - _zoomStart.X) < 4)
            {
                // Reset zoom on simple right click
                _zoomFactor = 1.0;
                _panOffsetSamples = 0;
                _selectionStartX = -1;
                _selectionEndX = -1;
                InvalidateVisual();
            }
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            Point pos = e.GetPosition(this);
            _selectionStartX = pos.X;
            _selectionEndX = pos.X;
            _isSelecting = true;
            CaptureMouse();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pos = e.GetPosition(this);
        _hoverX = pos.X;

        if (_isPanning && _samples.Count >= 2 && ActualWidth > 1)
        {
            double dx = pos.X - _panStart.X;
            double pixelsPerSample = ActualWidth / Math.Max(1, _samples.Count - 1);
            int sampleDelta = (int)Math.Round(dx / pixelsPerSample);
            _panOffsetSamples = Math.Clamp(_panStartOffset - sampleDelta, 0, Math.Max(0, _samples.Count - 10));
            InvalidateVisual();
            return;
        }

        if (_isZoomDragging)
        {
            double dy = _zoomStart.Y - pos.Y;
            _zoomFactor = Math.Clamp(_zoomStartFactor + (dy * 0.015), 0.5, 4.0);
            InvalidateVisual();
            return;
        }

        if (_isSelecting)
        {
            _selectionEndX = pos.X;
            InvalidateVisual();
            return;
        }

        if (_samples.Count >= 2 && ActualWidth > 1)
        {
            int idx = (int)Math.Round((pos.X / ActualWidth) * (_samples.Count - 1));
            _hoverIndex = Math.Clamp(idx, 0, _samples.Count - 1);
            UpdateHoverTooltip();
        }
        InvalidateVisual();
    }

    private void UpdateHoverTooltip()
    {
        if (_hoverIndex >= 0 && _hoverIndex < _samples.Count)
        {
            var sample = _samples[_hoverIndex];
            int secAgo = _samples.Count - 1 - _hoverIndex;
            ToolTip = $"T-{secAgo}s | DL: {Fmt.Rate(sample.In)} | UL: {Fmt.Rate(sample.Out)}";
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isPanning && !_isZoomDragging && !_isSelecting)
        {
            _hoverX = -1;
            _hoverIndex = -1;
            ToolTip = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta > 0)
        {
            _zoomFactor = Math.Min(3.5, _zoomFactor * 1.25);
        }
        else if (e.Delta < 0)
        {
            _zoomFactor = Math.Max(0.5, _zoomFactor * 0.8);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        // Get active samples subset based on zoom & pan
        IReadOnlyList<GraphSample> visibleSamples = _samples;
        if (_samples.Count > 10 && (_zoomFactor != 1.0 || _panOffsetSamples > 0))
        {
            int visibleCount = (int)Math.Clamp(Math.Round(_samples.Count / _zoomFactor), 5, _samples.Count);
            int startIdx = Math.Clamp(_samples.Count - visibleCount - _panOffsetSamples, 0, _samples.Count - visibleCount);
            var subset = new List<GraphSample>(visibleCount);
            for (int i = 0; i < visibleCount; i++)
            {
                subset.Add(_samples[startIdx + i]);
            }
            visibleSamples = subset;
        }

        double maxIn = 0;
        double maxOut = 0;
        foreach (GraphSample s in visibleSamples)
        {
            maxIn = Math.Max(maxIn, s.In);
            maxOut = Math.Max(maxOut, s.Out);
        }

        _peakIn = maxIn > 0 ? Math.Max(maxIn, _peakIn * 0.94) : 0;
        _peakOut = maxOut > 0 ? Math.Max(maxOut, _peakOut * 0.94) : 0;
        double sharedMax = Math.Max(1024, Math.Max(_peakIn, _peakOut));
        _peak = sharedMax;

        double currentPeakIn = _dualYAxis ? (_peakIn > 0 ? Math.Max(1024, _peakIn) : sharedMax) : sharedMax;
        double currentPeakOut = _dualYAxis ? (_peakOut > 0 ? Math.Max(1024, _peakOut) : sharedMax) : sharedMax;

        Color downColor = (Application.Current.TryFindResource("Down") as SolidColorBrush)?.Color ?? Color.FromRgb(53, 211, 154);
        Color upColor = (Application.Current.TryFindResource("Up") as SolidColorBrush)?.Color ?? Color.FromRgb(139, 157, 255);
        Color lineColor = (Application.Current.TryFindResource("Line") as SolidColorBrush)?.Color ?? Color.FromArgb(40, 255, 255, 255);
        Color labelColor = (Application.Current.TryFindResource("TextDim") as SolidColorBrush)?.Color ?? Color.FromRgb(90, 99, 115);

        Brush inFill = Frozen(Color.FromArgb(50, downColor.R, downColor.G, downColor.B));
        Pen inPen = FrozenPen(downColor, 2.2);
        Pen outPen = FrozenPen(upColor, 1.8);
        Pen gridPen = FrozenPen(Color.FromArgb(50, lineColor.R, lineColor.G, lineColor.B), 1.0);
        Pen cursorPen = FrozenPen(Color.FromArgb(180, 255, 255, 255), 1.0);
        Brush labelBrush = Frozen(labelColor);

        const double top = 8;
        double bottom = h - 16;

        for (int i = 1; i <= 3; i++)
        {
            double y = bottom - ((bottom - top) * i / 3.0);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (visibleSamples.Count >= 2)
        {
            double Step(int index) => w * index / (visibleSamples.Count - 1);
            double YIn(double value) => maxIn == 0 ? bottom : (bottom - ((bottom - top) * Math.Min(1, value / currentPeakIn)));
            double YOut(double value) => maxOut == 0 ? bottom : (bottom - ((bottom - top) * Math.Min(1, value / currentPeakOut)));

            // Download stream (Green)
            if (_showDownload && maxIn > 0)
            {
                var area = new StreamGeometry();
                using (StreamGeometryContext ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(0, bottom), isFilled: true, isClosed: true);
                    for (int i = 0; i < visibleSamples.Count; i++)
                    {
                        ctx.LineTo(new Point(Step(i), YIn(visibleSamples[i].In)), true, false);
                    }
                    ctx.LineTo(new Point(Step(visibleSamples.Count - 1), bottom), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(inFill, null, area);

                var inLine = new StreamGeometry();
                using (StreamGeometryContext ctx = inLine.Open())
                {
                    ctx.BeginFigure(new Point(0, YIn(visibleSamples[0].In)), isFilled: false, isClosed: false);
                    for (int i = 1; i < visibleSamples.Count; i++)
                    {
                        ctx.LineTo(new Point(Step(i), YIn(visibleSamples[i].In)), true, false);
                    }
                }
                inLine.Freeze();
                dc.DrawGeometry(null, inPen, inLine);
            }

            // Upload stream (Blue/Red)
            if (_showUpload && maxOut > 0)
            {
                Brush outFill = Frozen(Color.FromArgb(28, upColor.R, upColor.G, upColor.B));
                var outArea = new StreamGeometry();
                using (StreamGeometryContext ctx = outArea.Open())
                {
                    ctx.BeginFigure(new Point(0, bottom), isFilled: true, isClosed: true);
                    for (int i = 0; i < visibleSamples.Count; i++)
                    {
                        ctx.LineTo(new Point(Step(i), YOut(visibleSamples[i].Out)), true, false);
                    }
                    ctx.LineTo(new Point(Step(visibleSamples.Count - 1), bottom), true, false);
                }
                outArea.Freeze();
                dc.DrawGeometry(outFill, null, outArea);

                var outLine = new StreamGeometry();
                using (StreamGeometryContext ctx = outLine.Open())
                {
                    ctx.BeginFigure(new Point(0, YOut(visibleSamples[0].Out)), isFilled: false, isClosed: false);
                    for (int i = 1; i < visibleSamples.Count; i++)
                    {
                        ctx.LineTo(new Point(Step(i), YOut(visibleSamples[i].Out)), true, false);
                    }
                }
                outLine.Freeze();
                dc.DrawGeometry(null, outPen, outLine);
            }

            // Selection region overlay & totals (Section 9.04.c & 9.05)
            if (_selectionStartX >= 0 && _selectionEndX >= 0 && Math.Abs(_selectionStartX - _selectionEndX) > 4)
            {
                double selLeft = Math.Min(_selectionStartX, _selectionEndX);
                double selRight = Math.Max(_selectionStartX, _selectionEndX);
                var selBrush = Frozen(Color.FromArgb(50, 88, 166, 255));
                dc.DrawRectangle(selBrush, null, new Rect(selLeft, top, selRight - selLeft, bottom - top));

                int sIdx1 = Math.Clamp((int)Math.Round((selLeft / w) * (visibleSamples.Count - 1)), 0, visibleSamples.Count - 1);
                int sIdx2 = Math.Clamp((int)Math.Round((selRight / w) * (visibleSamples.Count - 1)), 0, visibleSamples.Count - 1);

                long selInBytes = 0;
                long selOutBytes = 0;
                for (int i = sIdx1; i <= sIdx2; i++)
                {
                    selInBytes += (long)visibleSamples[i].In;
                    selOutBytes += (long)visibleSamples[i].Out;
                }

                string selText = $"Selected: ↓ {Fmt.Bytes(selInBytes)} | ↑ {Fmt.Bytes(selOutBytes)}";
                var selLabel = new FormattedText(
                    selText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    10.5,
                    Frozen(Color.FromRgb(88, 166, 255)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(selLabel, new Point(8, bottom - 18));
            }

            // Hover indicator cursor & badges
            if (_hoverX >= 0 && _hoverIndex >= 0 && _hoverIndex < visibleSamples.Count)
            {
                double hx = Step(_hoverIndex);
                dc.DrawLine(cursorPen, new Point(hx, top), new Point(hx, bottom));

                var s = visibleSamples[_hoverIndex];
                if (_showDownload && maxIn > 0)
                {
                    dc.DrawEllipse(inPen.Brush, null, new Point(hx, YIn(s.In)), 3.5, 3.5);
                }
                if (_showUpload && maxOut > 0)
                {
                    dc.DrawEllipse(outPen.Brush, null, new Point(hx, YOut(s.Out)), 3.0, 3.0);
                }
            }
        }

        // Peak rate label
        string peakText = _dualYAxis
            ? $"DL: {Fmt.Rate(currentPeakIn)} | UL: {Fmt.Rate(currentPeakOut)}"
            : Fmt.Rate(_peak);

        var label = new FormattedText(
            peakText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            10,
            labelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(label, new Point(Math.Max(4, w - label.Width - 6), Math.Max(2, top - 6)));

        if (_isPaused)
        {
            var pauseLabel = new FormattedText(
                "⏸ PAUSED",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11,
                new SolidColorBrush(Color.FromRgb(210, 153, 34)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(pauseLabel, new Point(8, 6));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
