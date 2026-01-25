using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Layout;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace VtNetCore.Avalonia
{
    public class VirtualTerminalControl : TemplatedControl
    {
        private const double ScrollSpeedMultiplier = 2;

        private static readonly Color[] AttributeColors =
        {
            Color.FromArgb(255, 0, 0, 0), // Black
            Color.FromArgb(255, 205, 0, 0), // Red
            Color.FromArgb(255, 0, 205, 0), // Green
            Color.FromArgb(255, 205, 205, 0), // Yellow
            Color.FromArgb(255, 0, 0, 205), // Blue
            Color.FromArgb(255, 205, 0, 205), // Magenta
            Color.FromArgb(255, 0, 205, 205), // Cyan
            Color.FromArgb(255, 205, 205, 205), // White
            Color.FromArgb(255, 127, 127, 127), // Bright black
            Color.FromArgb(255, 255, 0, 0), // Bright red
            Color.FromArgb(255, 0, 255, 0), // Bright green
            Color.FromArgb(255, 255, 255, 0), // Bright yellow
            Color.FromArgb(255, 92, 92, 255), // Bright blue
            Color.FromArgb(255, 255, 0, 255), // Bright Magenta
            Color.FromArgb(255, 0, 255, 255), // Bright cyan
            Color.FromArgb(255, 255, 255, 255) // Bright white
        };

        private static readonly SolidColorBrush[] AttributeBrushes =
        {
            new SolidColorBrush(AttributeColors[0]),
            new SolidColorBrush(AttributeColors[1]),
            new SolidColorBrush(AttributeColors[2]),
            new SolidColorBrush(AttributeColors[3]),
            new SolidColorBrush(AttributeColors[4]),
            new SolidColorBrush(AttributeColors[5]),
            new SolidColorBrush(AttributeColors[6]),
            new SolidColorBrush(AttributeColors[7]),
            new SolidColorBrush(AttributeColors[8]),
            new SolidColorBrush(AttributeColors[9]),
            new SolidColorBrush(AttributeColors[10]),
            new SolidColorBrush(AttributeColors[11]),
            new SolidColorBrush(AttributeColors[12]),
            new SolidColorBrush(AttributeColors[13]),
            new SolidColorBrush(AttributeColors[14]),
            new SolidColorBrush(AttributeColors[15])
        };

        public static readonly StyledProperty<IConnection> ConnectionProperty =
            AvaloniaProperty.Register<VirtualTerminalControl, IConnection>(nameof(Connection));

        public static readonly StyledProperty<VirtualTerminalController> TerminalProperty =
            AvaloniaProperty.Register<VirtualTerminalControl, VirtualTerminalController>(nameof(Terminal));

        public static readonly AvaloniaProperty<Thickness> TextPaddingProperty =
            AvaloniaProperty.Register<VirtualTerminalControl, Thickness>(nameof(TextPadding));

        private readonly DispatcherTimer _blinkDispatcher;
        private CompositeDisposable _disposables;

        private double _realScroll;

        private ScrollBar _scrollBar;
        private bool _selecting;
        private CompositeDisposable _terminalDisposables;

        private int _viewTop;
        public DateTime TerminalIdleSince = DateTime.Now;

        static VirtualTerminalControl()
        {
            AffectsRender<VirtualTerminalControl>(ConnectionProperty);
        }

        public VirtualTerminalControl()
        {
            _blinkDispatcher = new DispatcherTimer();
            _blinkDispatcher.Tick += (sender, e) => InvalidateVisual();
            _blinkDispatcher.Interval = TimeSpan.FromMilliseconds(Gcd(BlinkShowMs, BlinkHideMs));
            //blinkDispatcher.Start();

            this.GetObservable(TerminalProperty)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(terminal =>
                {
                    if (_terminalDisposables != null)
                    {
                        _terminalDisposables.Dispose();
                        _terminalDisposables = null;
                        Consumer = null;
                    }

                    Columns = -1;
                    Rows = -1;
                    TerminalIdleSince = DateTime.Now;
                    ViewTop = 0;
                    CharacterHeight = -1;
                    CharacterWidth = -1;

                    if (terminal != null)
                    {
                        _terminalDisposables = new CompositeDisposable();
                        Consumer = new DataConsumer(terminal);

                        _terminalDisposables.Add(
                            Observable.FromEventPattern<SendDataEventArgs>(terminal, nameof(terminal.SendData))
                                .Subscribe(e => OnSendData(e.EventArgs)));

                        _terminalDisposables.Add(
                            Observable.FromEventPattern<TextEventArgs>(terminal, nameof(terminal.WindowTitleChanged))
                                .ObserveOn(AvaloniaScheduler.Instance)
                                .Subscribe(e => WindowTitle = e.EventArgs.Text));

                        terminal.StoreRawText = true;
                    }
                });

            this.GetObservable(ConnectionProperty)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(connection =>
                {
                    if (_disposables != null)
                    {
                        _disposables.Dispose();
                        _disposables = null;
                    }

                    _disposables = new CompositeDisposable();

                    if (connection != null)
                    {
                        _disposables.Add(Observable
                            .FromEventPattern<DataReceivedEventArgs>(connection, nameof(connection.DataReceived))
                            .ObserveOn(AvaloniaScheduler.Instance)
                            .Subscribe(args => OnDataReceived(args.EventArgs)));
                    }
                });
        }

        private int BlinkShowMs { get; } = 600;
        private int BlinkHideMs { get; } = 300;

        public double CharacterWidth { get; private set; } = -1;
        public double CharacterHeight { get; private set; } = -1;
        public int Columns { get; private set; } = -1;
        public int Rows { get; private set; } = -1;
        public DataConsumer Consumer { get; set; }

        public int ViewTop
        {
            get => _viewTop;
            set
            {
                _viewTop = value;
                if (_scrollBar != null) _scrollBar.Value = ViewTop;
            }
        }

        public string WindowTitle { get; set; } = "Session";
        public bool ViewDebugging { get; set; }
        public bool DebugMouse { get; set; }
        public bool DebugSelect { get; set; }

        public IConnection Connection
        {
            get => GetValue(ConnectionProperty);
            set => SetValue(ConnectionProperty, value);
        }

        public VirtualTerminalController Terminal
        {
            get => GetValue(TerminalProperty);
            set => SetValue(TerminalProperty, value);
        }

        public Thickness TextPadding
        {
            get => (Thickness)GetValue(TextPaddingProperty);
            set => SetValue(TextPaddingProperty, value);
        }

        public bool Connected => Connection != null && Connection.IsConnected;

        private TextPosition MouseOver { get; set; } = new TextPosition();
        private TextRange TextSelection { get; set; }

        private TextPosition MousePressedAt { get; set; }

        // Use Euclid's algorithm to calculate the
        // greatest common divisor (GCD) of two numbers.
        private long Gcd(long a, long b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);

            // Pull out remainders.
            for (;;)
            {
                var remainder = a % b;
                if (remainder == 0) return b;
                a = b;
                b = remainder;
            }
        }

        protected override void OnMeasureInvalidated()
        {
            base.OnMeasureInvalidated();
            SetScrollWindow();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

            if (_scrollBar == null) throw new NullReferenceException(nameof(_scrollBar));

            _scrollBar.Scroll += (o, i) => { SetScroll((int)i.NewValue); };
        }

        private void SetScrollWindow()
        {
            if (Terminal != null && _scrollBar != null)
            {
                _scrollBar.Minimum = 0;
                _scrollBar.Maximum = Terminal.ViewPort.Parent.BottomRow - Rows;
                _scrollBar.ViewportSize = Rows;
                SetScroll(Terminal.ViewPort.TopRow);
            }
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);

            Terminal?.FocusIn();

            InvalidateVisual();
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);

            Terminal?.FocusOut();

            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _terminalDisposables?.Dispose();
            _disposables?.Dispose();
            _terminalDisposables = null;
            _disposables = null;

            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            var ch = e.Text;

            // Since I get the same key twice in TerminalKeyDown and in CoreWindow_CharacterReceived
            // I lookup whether KeyPressed should handle the key here or there.
            var code = Terminal.GetKeySequence(ch, false, false);
            if (code == null)
                e.Handled = Terminal.KeyPressed(ch, false, false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!Connected)
                return;

            var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (e.Key is Key.LeftCtrl || e.Key is Key.LeftShift) return;

            if (controlPressed)
                switch (e.Key)
                {
                    case Key.F10:
                        //Consumer.SequenceDebugging = !Consumer.SequenceDebugging;
                        return;

                    case Key.F11:
                        //ViewDebugging = !ViewDebugging;
                        InvalidateVisual();
                        return;

                    case Key.F12:
                        //Terminal.Debugging = !Terminal.Debugging;
                        return;

                    case Key.V when shiftPressed:
                        PasteClipboard();
                        e.Handled = true;
                        return;

                    case Key.C when shiftPressed && TextSelection != null:
                        var captured = Terminal.GetText(TextSelection.Start.Column, TextSelection.Start.Row,
                            TextSelection.End.Column, TextSelection.End.Row);
                        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(captured);
                        e.Handled = true;
                        return;
                }

            // Since I get the same key twice in TerminalKeyDown and in CoreWindow_CharacterReceived
            // I lookup whether KeyPressed should handle the key here or there.
            var code = Terminal.GetKeySequence(e.Key.ToString(), controlPressed, shiftPressed);
            if (code != null)
                e.Handled = Terminal.KeyPressed(e.Key.ToString(), controlPressed, shiftPressed);

            if (ViewTop != Terminal.ViewPort.TopRow)
            {
                ViewTop = Terminal.ViewPort.TopRow;
                InvalidateVisual();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            if (controlPressed)
            {
                var scale = 0.9 * e.Delta.Y;

                var newFontSize = FontSize;
                if (scale < 0)
                    newFontSize *= Math.Abs(scale);
                else
                    newFontSize /= scale;

                if (newFontSize < 2)
                    newFontSize = 2;
                if (newFontSize > 20)
                    newFontSize = 20;

                if (newFontSize != FontSize)
                {
                    FontSize = newFontSize;

                    InvalidateVisual();
                }
            }
            else
            {
                _realScroll += e.Delta.Y * ScrollSpeedMultiplier;

                if (Math.Abs(_realScroll) > 1)
                {
                    SetScroll((int)(ViewTop - _realScroll));
                    _realScroll = 0;
                }
            }
        }

        private void SetScroll(int value)
        {
            var oldViewTop = ViewTop;
            
            ViewTop = value;

            if (ViewTop < 0)
                ViewTop = 0;
            else if (ViewTop > Terminal.ViewPort.TopRow)
                ViewTop = Terminal.ViewPort.TopRow;

            if (oldViewTop != ViewTop)
                InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (!(e.Source is VirtualTerminalControl)) return;

            var pointer = e.GetPosition(this);
            var hasPosition = TryGetCellPosition(pointer, out var position, clampToBounds: false);
            var hasSelectionPosition = TryGetCellPosition(pointer, out var selectionPosition, clampToBounds: true);

            if (Connected && hasPosition && (Terminal.UseAllMouseTracking || Terminal.CellMotionMouseTracking))
            {
                var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                var shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                var props = e.GetCurrentPoint(null).Properties;

                var button =
                    props.IsLeftButtonPressed ? 0 :
                    props.IsRightButtonPressed ? 1 :
                    props.IsMiddleButtonPressed ? 2 :
                    3; // No button

                Terminal.MouseMove(position.Column, position.Row, button, controlPressed, shiftPressed);

                if (button == 3 && !Terminal.UseAllMouseTracking)
                    return;
            }

            if (!hasSelectionPosition)
                return;

            var textPosition = selectionPosition.OffsetBy(0, ViewTop);

            if (MouseOver != null && MouseOver == selectionPosition)
                return;

            MouseOver = selectionPosition;

            if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                if (MousePressedAt != null && MousePressedAt != textPosition)
                {
                    TextRange newSelection;
                    if (MousePressedAt <= textPosition)
                        newSelection = new TextRange
                        {
                            Start = MousePressedAt,
                            End = textPosition
                        };
                    else
                        newSelection = new TextRange
                        {
                            Start = textPosition,
                            End = MousePressedAt
                        };

                    _selecting = true;

                    if (TextSelection != newSelection)
                    {
                        TextSelection = newSelection;

                        if (DebugSelect)
                            Debug.WriteLine("Selection: " + TextSelection);

                        InvalidateVisual();
                    }
                }

            if (DebugMouse)
                Debug.WriteLine("Pointer Moved " + position);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            MouseOver = null;

            if (DebugMouse)
                Debug.WriteLine("TerminalPointerExited()");

            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();

            var pointer = e.GetPosition(this);
            if (!TryGetCellPosition(pointer, out var position, clampToBounds: true))
                return;

            var textPosition = position.OffsetBy(0, ViewTop);
            _selecting = false;

            if (!Connected || (Connected && !Terminal.X10SendMouseXYOnButton && !Terminal.X11SendMouseXYOnButton &&
                               !Terminal.SgrMouseMode && !Terminal.CellMotionMouseTracking &&
                               !Terminal.UseAllMouseTracking))
            {
                if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                {
                    TextSelection = null;
                    MousePressedAt = textPosition;
                    e.Pointer.Capture(this);
                }
                else if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
                    PasteClipboard();
            }

            if (Connected)
            {
                var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                var shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                var props = e.GetCurrentPoint(null).Properties;

                var button =
                    props.IsLeftButtonPressed ? 0 :
                    props.IsRightButtonPressed ? 1 :
                    props.IsMiddleButtonPressed ? 2 :
                    3; // No button

                Terminal.MousePress(position.Column, position.Row, button, controlPressed, shiftPressed);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            var pointer = e.GetPosition(this);
            if (!TryGetCellPosition(pointer, out var position, clampToBounds: true))
                return;

            var textPosition = position.OffsetBy(0, ViewTop);

            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                e.Pointer.Capture(null);

                if (_selecting && TextSelection != null)
                {
                    MousePressedAt = null;
                    _selecting = false;

                    if (DebugSelect)
                        Debug.WriteLine("Captured : " + Terminal.GetText(TextSelection.Start.Column,
                            TextSelection.Start.Row, TextSelection.End.Column, TextSelection.End.Row));

                    var captured = Terminal.GetText(TextSelection.Start.Column, TextSelection.Start.Row,
                        TextSelection.End.Column, TextSelection.End.Row);

                    _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(captured);
                }
                else
                {
                    TextSelection = null;
                    InvalidateVisual();
                }
            }

            if (Connected)
            {
                var controlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                var shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                Terminal.MouseRelease(position.Column, position.Row, controlPressed, shiftPressed);
            }
        }

        private void OnSendData(SendDataEventArgs e)
        {
            if (!Connected)
                return;

            var connection = Connection;

            Task.Run(() => { connection.SendData(e.Data); });
        }

        private void OnDataReceived(DataReceivedEventArgs e)
        {
            lock (Terminal)
            {
                var oldTopRow = Terminal.ViewPort.TopRow;

                try
                {
                    Consumer.Push(e.Data);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }

                if (Terminal.Changed)
                {
                    Terminal.ClearChanges();

                    if (oldTopRow != Terminal.ViewPort.TopRow && oldTopRow >= ViewTop)
                        ViewTop = Terminal.ViewPort.TopRow;

                    SetScrollWindow();
                    InvalidateVisual();
                }

                TerminalIdleSince = DateTime.Now;
            }
        }

        private bool BlinkVisible()
        {
            var blinkCycle = BlinkShowMs + BlinkHideMs;

            return DateTime.Now.Subtract(DateTime.MinValue).TotalMilliseconds % blinkCycle < BlinkHideMs;
        }

        public IBrush GetSolidColorBrush(string hex)
        {
            if (hex == "#0C0C0C") return Background;
            if (hex == "#CCCCCC" || hex == "#FFFFFF") return Foreground;

            var lightMode = false;
            if (Foreground is SolidColorBrush foregroundBrush)
                if (foregroundBrush.Color.R < 100 && foregroundBrush.Color.G < 100 && foregroundBrush.Color.B < 100)
                    lightMode = true;

            byte a = 255;
            var r = (byte)Convert.ToUInt32(hex.Substring(1, 2), 16);
            var g = (byte)Convert.ToUInt32(hex.Substring(3, 2), 16);
            var b = (byte)Convert.ToUInt32(hex.Substring(5, 2), 16);

            if (lightMode)
            {
                const byte colorMax = 150;
                if (r > colorMax) r = colorMax;
                if (g > colorMax) g = colorMax;
                if (b > colorMax) b = colorMax;
            }
            else
            {
                const byte colorMin = 100;
                if (r < colorMin) r = colorMin;
                if (g < colorMin) g = colorMin;
                if (b < colorMin) b = colorMin;
            }

            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        private void PaintBackgroundLayer(DrawingContext context, List<LayoutRow> spans)
        {
            if (spans == null) return;

            double lineY = 0;
            foreach (var textRow in spans)
                using (context.PushTransform(Matrix.CreateScale(
                           textRow.DoubleWidth ? 2.0 : 1.0, // Scale double width
                           textRow.DoubleHeightBottom | textRow.DoubleHeightTop ? 2.0 : 1.0 // Scale double high
                       )))
                {
                    var drawY =
                        (lineY - (textRow.DoubleHeightBottom
                            ? CharacterHeight
                            : 0)) * // Offset position upwards for bottom of double high char
                        (textRow.DoubleHeightBottom | textRow.DoubleHeightTop
                            ? 0.5
                            : 1.0); // Scale position for double height

                    var drawX = TextPadding.Left;
                    drawY += TextPadding.Top;
                    foreach (var textSpan in textRow.Spans)
                    {
                        var bounds =
                            new Rect(
                                drawX,
                                drawY,
                                CharacterWidth * textSpan.Text.Length + 0.9,
                                CharacterHeight + 0.9
                            );

                        context.FillRectangle(GetSolidColorBrush(textSpan.BackgroundColor), bounds);

                        drawX += CharacterWidth * textSpan.Text.Length;
                    }

                    lineY += CharacterHeight;
                }
        }

        private void PaintTextLayer(DrawingContext context, List<LayoutRow> spans, Typeface textFormat, bool showBlink)
        {
            if (spans == null) return;
            var dipToDpiRatio = 96 / 96; // TODO read screen dpi.

            double lineY = 0;
            foreach (var textRow in spans)
                using (context.PushTransform(Matrix.CreateScale(
                           textRow.DoubleWidth ? 2.0 : 1.0, // Scale double width
                           textRow.DoubleHeightBottom | textRow.DoubleHeightTop ? 2.0 : 1.0 // Scale double high
                       )))
                {
                    var drawY =
                        (lineY - (textRow.DoubleHeightBottom
                            ? CharacterHeight
                            : 0)) * // Offset position upwards for bottom of double high char
                        (textRow.DoubleHeightBottom | textRow.DoubleHeightTop
                            ? 0.5
                            : 1.0); // Scale position for double height

                    var drawX = TextPadding.Left;
                    drawY += TextPadding.Top;
                    foreach (var textSpan in textRow.Spans)
                    {
                        var runWidth = CharacterWidth * textSpan.Text.Length;

                        if (textSpan.Hidden || (textSpan.Blink && !showBlink))
                        {
                            drawX += runWidth;
                            continue;
                        }

                        var color = GetSolidColorBrush(textSpan.ForgroundColor);

                        var typeface = new Typeface(textFormat.FontFamily, FontStyle.Normal,
                            textSpan.Bold ? FontWeight.Bold : FontWeight.Light);

                        var textLayout = new FormattedText(textSpan.Text, CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, typeface, FontSize, color);

                        context.DrawText(textLayout, new Point(drawX, drawY));

                        // TODO : Come up with a better means of identifying line weight and offset
                        var underlineOffset = dipToDpiRatio * 1.07;

                        if (textSpan.Underline)
                            context.DrawLine(new Pen(color), new Point(drawX, drawY + underlineOffset),
                                new Point(drawX + runWidth, drawY + underlineOffset));

                        drawX += CharacterWidth * textSpan.Text.Length;
                    }

                    lineY += CharacterHeight;
                }
        }

        private void PaintCursor(DrawingContext context, List<LayoutRow> spans, Typeface textFormat,
            TextPosition cursorPosition, IBrush cursorColor)
        {
            var cursorY = cursorPosition.Row;

            if (cursorY >= 0 && spans != null && cursorY < spans.Count)
            {
                var textRow = spans[cursorY];

                using (context.PushTransform(Matrix.CreateTranslation(
                                                 1.0f,
                                                 textRow.DoubleHeightBottom ? -CharacterHeight : 0
                                             ) *
                                             Matrix.CreateScale(
                                                 textRow.DoubleWidth ? 2.0 : 1.0,
                                                 textRow.DoubleHeightBottom | textRow.DoubleHeightTop ? 2.0 : 1.0
                                             )))
                {
                    var drawX = cursorPosition.Column * CharacterWidth;
                    var drawY = cursorY * CharacterHeight *
                                (textRow.DoubleHeightBottom | textRow.DoubleHeightTop ? 0.5 : 1.0);

                    var cursorRect = new Rect(
                        drawX + TextPadding.Left,
                        drawY + TextPadding.Top,
                        CharacterWidth,
                        CharacterHeight + 0.9
                    );

                    if (IsFocused)
                        context.FillRectangle(cursorColor, cursorRect);
                    else
                        context.DrawRectangle(new Pen(cursorColor), cursorRect);
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            var textFormat = new Typeface(FontFamily, FontStyle, FontWeight);

            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

            ProcessTextFormat(context, textFormat);

            var showBlink = BlinkVisible();

            List<LayoutRow> spans = null;
            TextPosition cursorPosition = null;
            var showCursor = false;
            IBrush cursorColor = Brushes.Green;

            if (Terminal != null)
                lock (Terminal)
                {
                    spans = Terminal.ViewPort.GetPageSpans(ViewTop, Rows, Columns, TextSelection);
                    showCursor = Terminal.CursorState.ShowCursor;
                    cursorPosition = new TextPosition(Terminal.ViewPort.CursorPosition.Column,
                        Terminal.ViewPort.CursorPosition.Row - ViewTop + Terminal.ViewPort.TopRow);
                    cursorColor = GetSolidColorBrush(Terminal.CursorState.Attributes.WebColor);
                }

            PaintBackgroundLayer(context, spans);

            PaintTextLayer(context, spans, textFormat, showBlink);

            if (showCursor) PaintCursor(context, spans, textFormat, cursorPosition, cursorColor);

            if (ViewDebugging) AnnotateView(context);
        }

        private void AnnotateView(DrawingContext context)
        {
            var lineNumberFormat = new Typeface(FontFamily, FontStyle, FontWeight);

            for (var i = 0; i < Rows; i++)
            {
                var s = i.ToString();
                var textLayout = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    lineNumberFormat, FontSize, Brushes.Yellow);

                var y = i * CharacterHeight;
                context.DrawLine(new Pen(Brushes.Beige), new Point(0, y), new Point(Bounds.Size.Width, y));

                context.DrawText(textLayout, new Point(Bounds.Size.Width - CharacterWidth / 2 * s.Length, y));


                s = (i + 1).ToString();

                textLayout = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    lineNumberFormat, FontSize, Brushes.Green);

                context.DrawText(textLayout, new Point(Bounds.Size.Width - CharacterWidth / 2 * (s.Length + 3), y));
            }

            var bigText = Terminal.DebugText;
            var bigTextLayout = new FormattedText(bigText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                lineNumberFormat, FontSize, Brushes.Yellow);

            context.DrawText(bigTextLayout, new Point(Bounds.Size.Width - bigTextLayout.Width - 100, 0));
        }

        private IBrush GetBackgroundBrush(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
            {
                if (attribute.ForegroundRgb == null)
                {
                    if (attribute.Bright)
                        return AttributeBrushes[(int)attribute.ForegroundColor + 8];

                    return AttributeBrushes[(int)attribute.ForegroundColor];
                }

                return new SolidColorBrush(Color.FromArgb(255, (byte)attribute.ForegroundRgb.Red,
                    (byte)attribute.ForegroundRgb.Green, (byte)attribute.ForegroundRgb.Blue));
            }

            if (attribute.BackgroundRgb == null)
                return AttributeBrushes[(int)attribute.BackgroundColor];
            return new SolidColorBrush(Color.FromArgb(255, (byte)attribute.BackgroundRgb.Red,
                (byte)attribute.BackgroundRgb.Green, (byte)attribute.BackgroundRgb.Blue));
        }

        private IBrush GetForegroundBrush(TerminalAttribute attribute, bool invert)
        {
            var flip = Terminal.CursorState.ReverseVideoMode ^ attribute.Reverse ^ invert;

            if (flip)
            {
                if (attribute.BackgroundRgb == null)
                {
                    if (attribute.Bright) return AttributeBrushes[(int)attribute.BackgroundColor + 8];

                    return AttributeBrushes[(int)attribute.BackgroundColor];
                }

                return new SolidColorBrush(Color.FromArgb(255, (byte)attribute.BackgroundRgb.Red,
                    (byte)attribute.BackgroundRgb.Green, (byte)attribute.BackgroundRgb.Blue));
            }

            if (attribute.ForegroundRgb == null)
            {
                if (attribute.Bright) return AttributeBrushes[(int)attribute.ForegroundColor + 8];

                return AttributeBrushes[(int)attribute.ForegroundColor];
            }

            return new SolidColorBrush(Color.FromArgb(255, (byte)attribute.ForegroundRgb.Red,
                (byte)attribute.ForegroundRgb.Green, (byte)attribute.ForegroundRgb.Blue));
        }

        private void ProcessTextFormat(DrawingContext drawingSession, Typeface format)
        {
            var textLayout = new FormattedText("\u2560", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, format,
                FontSize, Brushes.White);

            if (CharacterWidth != textLayout.Width || CharacterHeight != textLayout.Height)
            {
                CharacterWidth = textLayout.Width;
                CharacterHeight = textLayout.Height;
            }

            var columns = Convert.ToInt32(Math.Floor((Bounds.Size.Width - TextPadding.Left - TextPadding.Right) /
                                                     CharacterWidth));
            var rows = Convert.ToInt32(Math.Floor((Bounds.Size.Height - TextPadding.Top - TextPadding.Bottom) /
                                                  CharacterHeight));
            
            if (Columns != columns || Rows != rows)
            {
                Columns = columns;
                Rows = rows;
                ResizeTerminal();

                if (Connection != null)
                {
                    Connection.SetTerminalWindowSize(columns, rows);
                }
                
                Dispatcher.UIThread.Post(InvalidateMeasure);
            }
        }

        private void ResizeTerminal()
        {
            Terminal?.ResizeView(Columns, Rows);
        }

        private TextPosition ToPosition(Point point)
        {
            TryGetCellPosition(point, out var position, clampToBounds: true);
            return position;
        }

        private bool TryGetCellPosition(Point point, out TextPosition position, bool clampToBounds)
        {
            position = new TextPosition(-1, -1);

            if (Columns <= 0 || Rows <= 0 || CharacterWidth <= 0 || CharacterHeight <= 0)
                return false;

            var x = point.X - TextPadding.Left;
            var y = point.Y - TextPadding.Top;

            if (!clampToBounds &&
                (x < 0 || y < 0 || x >= Columns * CharacterWidth || y >= Rows * CharacterHeight))
                return false;

            var overColumn = (int)Math.Floor(x / CharacterWidth);
            var overRow = (int)Math.Floor(y / CharacterHeight);

            if (overColumn < 0) overColumn = 0;
            if (overColumn >= Columns) overColumn = Columns - 1;

            if (overRow < 0) overRow = 0;
            if (overRow >= Rows) overRow = Rows - 1;

            position = new TextPosition { Column = overColumn, Row = overRow };
            return true;
        }

        private void PasteText(string text)
        {
            if (Connection == null)
                return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                text = text.Replace("\r\n", "\r").Replace("\n", "\r");

            var buffer = Encoding.UTF8.GetBytes(text);

            var connection = Connection;

            Task.Run(() => { connection.SendData(buffer); });
        }

        private async void PasteClipboard()
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard clipboard)
            {
                var text = await clipboard.GetTextAsync();

                if (!string.IsNullOrEmpty(text)) PasteText(text);
            }
        }
    }
}
