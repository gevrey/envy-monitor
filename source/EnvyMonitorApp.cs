using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Envy Monitor")]
[assembly: AssemblyDescription("Read-only HP CPU temperature and fan activity monitor")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace EnvyMonitor
{
    internal static class Palette
    {
        public static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        public static readonly Brush Background = Brush("#10151E"), Card = Brush("#19212E"), Border = Brush("#2B384A");
        public static readonly Brush Text = Brush("#F0F4FA"), Muted = Brush("#A7B5C9"), Teal = Brush("#53DBC5"), Purple = Brush("#B3A1FF"), Amber = Brush("#FFC875");
    }

    public sealed class TrendChart : FrameworkElement
    {
        public List<Sample> Data = new List<Sample>();
        public DateTime EndUtc = DateTime.UtcNow;
        public TimeSpan Duration = TimeSpan.FromMinutes(5);
        public bool IsFan;
        private double cursor = -1;
        private double top = 125;
        private Rect plot;
        public TrendChart()
        {
            MinHeight = 130;
            MouseMove += delegate(object sender, MouseEventArgs e) { cursor = e.GetPosition(this).X; InvalidateVisual(); };
            MouseLeave += delegate { cursor = -1; ToolTip = null; InvalidateVisual(); };
        }
        private FormattedText Text(string value, double size, Brush brush)
        { return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip); }
        private Point Position(DateTime time, double value)
        {
            double x = plot.Left + (time - (EndUtc - Duration)).TotalSeconds / Duration.TotalSeconds * plot.Width;
            return new Point(x, plot.Bottom - Math.Max(0, Math.Min(top, value)) / top * plot.Height);
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            plot = new Rect(52, 12, Math.Max(1, ActualWidth - 68), Math.Max(1, ActualHeight - 42));
            top = IsFan ? Math.Max(1000, Math.Ceiling(Data.SelectMany(s => new double?[] { s.FanRaw, s.Fan2Raw }).Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(3000).Max() / 500.0) * 500) : 125;
            var gridPen = new Pen(Palette.Border, 1);
            for (int i = 0; i <= (IsFan ? 4 : 5); i++)
            {
                double value = IsFan ? top * i / 4 : 25 * i;
                double y = Position(EndUtc, value).Y;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var label = Text(value.ToString("0", CultureInfo.CurrentCulture), 11, Palette.Muted);
                dc.DrawText(label, new Point(plot.Left - label.Width - 10, y - 7));
            }
            for (int i = 0; i <= 4; i++)
            {
                DateTime t = EndUtc - TimeSpan.FromTicks(Duration.Ticks * (4 - i) / 4);
                double x = plot.Left + plot.Width * i / 4;
                var label = Text(t.ToLocalTime().ToString("HH:mm:ss"), 11, Palette.Muted);
                dc.DrawText(label, new Point(Math.Max(0, Math.Min(ActualWidth - label.Width, x - label.Width / 2)), plot.Bottom + 12));
            }
            dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 3, plot.Top - 3, plot.Width + 6, plot.Height + 6)));
            DrawSeries(dc, IsFan ? (Func<Sample, double?>)(s => s.FanRaw) : s => s.CpuTemperature, Palette.Teal);
            if (IsFan) DrawSeries(dc, s => s.Fan2Raw, Palette.Purple);
            dc.Pop();
            bool any = Data.Any(s => IsFan ? s.FanRaw.HasValue || s.Fan2Raw.HasValue : s.CpuTemperature.HasValue);
            if (!any)
            {
                var empty = Text("Waiting for live sensor readings", 14, Palette.Muted);
                dc.DrawText(empty, new Point(plot.Left + Math.Max(0, (plot.Width - empty.Width) / 2), plot.Top + plot.Height / 2 - 10));
            }
            if (cursor >= plot.Left && cursor <= plot.Right && Data.Count > 0)
            {
                DateTime target = EndUtc - Duration + TimeSpan.FromSeconds(Duration.TotalSeconds * (cursor - plot.Left) / plot.Width);
                Sample nearest = Data.OrderBy(s => Math.Abs((s.TimeUtc - target).TotalSeconds)).First();
                if (Math.Abs((nearest.TimeUtc - target).TotalSeconds) <= Math.Max(3, Duration.TotalSeconds / 60))
                {
                    double x = Position(nearest.TimeUtc, 0).X;
                    dc.DrawLine(new Pen(Palette.Muted, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
                    string detail = nearest.TimeUtc.ToLocalTime().ToString("HH:mm:ss") + "   " + (IsFan
                        ? "Fan 1: " + Format(nearest.FanRaw) + "  |  Fan 2: " + Format(nearest.Fan2Raw) + " (raw)"
                        : Format(nearest.CpuTemperature) + " °C   |   CPU load: " + Format(nearest.CpuLoad) + "%");
                    var tip = Text(detail, 12, Palette.Text);
                    double tipX = Math.Min(Math.Max(plot.Left, x - tip.Width / 2), Math.Max(plot.Left, plot.Right - tip.Width - 16));
                    dc.DrawRoundedRectangle(Palette.Background, new Pen(Palette.Border, 1), new Rect(tipX, plot.Top + 3, tip.Width + 16, 28), 5, 5);
                    dc.DrawText(tip, new Point(tipX + 8, plot.Top + 9));
                }
            }
        }
        private static string Format(double? value) { return value.HasValue ? value.Value.ToString("0.#") : "unavailable"; }
        private void DrawSeries(DrawingContext dc, Func<Sample, double?> metric, Brush color)
        {
            foreach (var segment in PlotSegments.Build(Data, metric, TimeSpan.FromSeconds(8)))
            {
                if (segment.Count == 1) { dc.DrawEllipse(color, null, Position(segment[0].TimeUtc, segment[0].Value), 3, 3); continue; }
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(Position(segment[0].TimeUtc, segment[0].Value), false, false);
                    for (int i = 1; i < segment.Count; i++) ctx.LineTo(Position(segment[i].TimeUtc, segment[i].Value), true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, new Pen(color, 2.3) { LineJoin = PenLineJoin.Round }, geometry);
            }
        }
    }

    public sealed class MonitorWindow : Window
    {
        private readonly HistoryStore history = new HistoryStore(21600);
        private readonly SensorReader sensors = new SensorReader();
        private readonly CpuUsage cpuUsage = new CpuUsage();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly bool preview;
        private readonly bool administrator;
        private bool busy, paused, closing, denied;
        private int rangeMinutes = 5;
        private int previewTick;
        private DateTime latest = DateTime.UtcNow;
        private TextBlock temperature, fanValue, cpuLoad, status, footer, tempDetail, fanDetail;
        private Button adminButton, pauseButton;
        private TrendChart cpuChart, fanChart;
        private readonly List<Button> rangeButtons = new List<Button>();

        public MonitorWindow(bool simulated)
        {
            preview = simulated;
            administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Title = preview ? "Envy Monitor | Preview" : "Envy Monitor";
            Width = 1080; Height = 850; MinWidth = 960; MinHeight = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.Background; Foreground = Palette.Text; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            BuildLayout();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async delegate { await SampleNow(); };
            Loaded += async delegate { if (preview) SeedPreview(); await SampleNow(); timer.Start(); };
            Closed += delegate { closing = true; timer.Stop(); Task.Run(() => sensors.Dispose()); };
        }
        private static TextBlock Label(string text, double size, Brush color)
        { return new TextBlock { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap }; }
        private static Button Button(string text, RoutedEventHandler action)
        {
            var button = new Button { Content = text, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Foreground = Palette.Text, Background = Palette.Card, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), FontSize = 12 };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, Palette.Teal)); template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, Palette.Teal)); template.Triggers.Add(focus);
            button.Template = template; button.Click += action; return button;
        }
        private static Border Panel(UIElement child)
        { return new Border { Child = child, Background = Palette.Card, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18) }; }
        private void BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(26, 20, 26, 18) };
            for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 4 || i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Content = root;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
            var right = Label(preview ? "PREVIEW / SIMULATED DATA" : "HP ENVY  /  LIVE SENSORS", 11, preview ? Palette.Amber : Palette.Teal);
            right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); header.Children.Add(right);
            var title = new StackPanel(); title.Children.Add(Label("Envy Monitor", 27, Palette.Text));
            title.Children.Add(Label("CPU temperature and fan activity, together over time", 13, Palette.Muted)); header.Children.Add(title);
            root.Children.Add(header);

            var banner = new DockPanel { LastChildFill = true };
            adminButton = Button("Enable live sensors", RestartElevated); adminButton.Margin = new Thickness(16, 0, 0, 0);
            adminButton.Visibility = preview || administrator ? Visibility.Collapsed : Visibility.Visible;
            DockPanel.SetDock(adminButton, Dock.Right); banner.Children.Add(adminButton);
            status = Label(preview ? "Preview only. All readings in this window are simulated." : administrator ? "Connecting to HP firmware sensors..." : "Administrator access is required to read HP temperature and fan sensors.", 12, preview ? Palette.Amber : Palette.Muted);
            status.VerticalAlignment = VerticalAlignment.Center; banner.Children.Add(status);
            var bannerBorder = Panel(banner); bannerBorder.Padding = new Thickness(14, 10, 14, 10); bannerBorder.Margin = new Thickness(0, 0, 0, 14);
            Grid.SetRow(bannerBorder, 1); root.Children.Add(bannerBorder);

            var cards = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
            var a = MetricCard("CPU TEMPERATURE", out temperature, out tempDetail);
            var b = MetricCard("FAN 1  /  FAN 2", out fanValue, out fanDetail);
            TextBlock loadDetail; var c = MetricCard("CPU LOAD", out cpuLoad, out loadDetail); loadDetail.Text = "Across all logical processors";
            a.Margin = new Thickness(0, 0, 10, 0); b.Margin = new Thickness(0, 0, 10, 0);
            cards.Children.Add(a); Grid.SetColumn(b, 1); cards.Children.Add(b); Grid.SetColumn(c, 2); cards.Children.Add(c);
            Grid.SetRow(cards, 2); root.Children.Add(cards);

            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            pauseButton = Button("Pause", delegate { paused = !paused; pauseButton.Content = paused ? "Resume" : "Pause"; if (!paused) { denied = false; } RefreshCharts(); });
            actions.Children.Add(pauseButton); actions.Children.Add(Button("Export CSV", Export));
            var pin = new CheckBox { Content = "Keep on top", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            pin.Checked += delegate { Topmost = true; }; pin.Unchecked += delegate { Topmost = false; }; actions.Children.Add(pin);
            DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
            var ranges = new StackPanel { Orientation = Orientation.Horizontal };
            ranges.Children.Add(new TextBlock { Text = "SHOW", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11 });
            foreach (int value in new int[] { 5, 15, 60 })
            {
                int minutes = value;
                var button = Button(minutes == 60 ? "1 hour" : minutes + " min", delegate { rangeMinutes = minutes; UpdateRangeButtons(); RefreshCharts(); });
                button.Tag = minutes; rangeButtons.Add(button); ranges.Children.Add(button);
            }
            toolbar.Children.Add(ranges); Grid.SetRow(toolbar, 3); root.Children.Add(toolbar); UpdateRangeButtons();
            cpuChart = new TrendChart(); fanChart = new TrendChart { IsFan = true };
            var cpuPanel = ChartPanel("CPU temperature", "Degrees Celsius  /  HP firmware sensor", cpuChart);
            var fanPanel = ChartPanel("Fan activity", "Fan 1: teal   /   Fan 2: purple   /   Raw firmware units", fanChart);
            cpuPanel.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(cpuPanel, 4); Grid.SetRow(fanPanel, 5); root.Children.Add(cpuPanel); root.Children.Add(fanPanel);
            var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            bottom.Children.Add(Label("Fan units are not verified as RPM. Missing readings appear as gaps, not stopped fans.", 11, Palette.Amber));
            footer = Label("2-second sampling  |  Session history: up to 12 hours  |  Export before closing", 11, Palette.Muted);
            footer.Margin = new Thickness(0, 5, 0, 0); bottom.Children.Add(footer); Grid.SetRow(bottom, 6); root.Children.Add(bottom);
        }
        private Border MetricCard(string title, out TextBlock value, out TextBlock detail)
        {
            var stack = new StackPanel(); stack.Children.Add(Label(title, 11, Palette.Muted));
            value = Label("Waiting", 29, Palette.Text); value.Margin = new Thickness(0, 5, 0, 4); stack.Children.Add(value);
            detail = Label("Waiting for a reading", 11, Palette.Muted); stack.Children.Add(detail); return Panel(stack);
        }
        private Border ChartPanel(string title, string subtitle, TrendChart chart)
        {
            var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
            var header = new DockPanel(); var sub = Label(subtitle, 11, Palette.Muted); DockPanel.SetDock(sub, Dock.Right); sub.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(sub); header.Children.Add(Label(title, 16, Palette.Text));
            grid.Children.Add(header); Grid.SetRow(chart, 1); grid.Children.Add(chart); var panel = Panel(grid); panel.Padding = new Thickness(18, 12, 18, 10); return panel;
        }
        private void UpdateRangeButtons()
        { foreach (var b in rangeButtons) { bool selected = (int)b.Tag == rangeMinutes; b.Background = selected ? Palette.Teal : Palette.Card; b.Foreground = selected ? Palette.Background : Palette.Text; } }
        private static string Format(double? value, string suffix)
        { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.CurrentCulture) + suffix : "Unavailable"; }
        private async Task SampleNow()
        {
            if (busy || paused || closing) return;
            busy = true;
            try
            {
                SensorReading result = null;
                Sample sample;
                if (preview) sample = PreviewSample(DateTime.UtcNow, previewTick++);
                else
                {
                    bool skipSensors = denied;
                    result = await Task.Run(() => skipSensors ? new SensorReading { TimeUtc = DateTime.UtcNow, AccessDenied = true, Error = "Administrator access is required for the HP live sensors.", FanState = "Unavailable", Source = "HP firmware thermal sensor" } : sensors.Read());
                    if (closing || paused) return;
                    denied = result.AccessDenied;
                    sample = new Sample { TimeUtc = result.TimeUtc, CpuTemperature = result.CpuTemperatureC,
                        FanRaw = result.FanRaw, Fan2Raw = result.Fan2Raw, FanRpm = result.FanRpm, FanRpmVerified = result.FanRpmVerified,
                        CpuLoad = cpuUsage.Read(), Source = result.Source, FanStatus = result.FanState };
                }
                sample = sample.CleanCopy(); history.Add(sample); latest = sample.TimeUtc;
                temperature.Text = Format(sample.CpuTemperature, " °C");
                fanValue.Text = (sample.FanRaw.HasValue ? sample.FanRaw.Value.ToString("0") : "?") + " / " + (sample.Fan2Raw.HasValue ? sample.Fan2Raw.Value.ToString("0") : "?");
                cpuLoad.Text = Format(sample.CpuLoad, "%");
                fanDetail.Text = sample.FanRaw.HasValue || sample.Fan2Raw.HasValue ? "Raw firmware values, RPM unverified" : "No valid fan reading available";
                if (!preview)
                {
                    status.Text = result.Error ?? (sample.CpuTemperature.HasValue ? "Live HP firmware readings. Monitoring only; your power and fan settings are unchanged." : "Connected, but the CPU sensor has no valid reading.");
                    status.Foreground = result.Error == null ? Palette.Muted : Palette.Amber;
                }
                RefreshCharts();
            }
            catch (Exception ex)
            {
                if (!closing) { status.Text = "Reading interrupted: " + ex.Message; status.Foreground = Palette.Amber; }
            }
            finally { busy = false; }
        }
        private void RefreshCharts()
        {
            DateTime end = paused ? latest : DateTime.UtcNow;
            var selected = history.GetRange(end, TimeSpan.FromMinutes(rangeMinutes));
            var temperatures = selected.Where(s => s.CpuTemperature.HasValue).Select(s => s.CpuTemperature.Value).ToList();
            tempDetail.Text = temperatures.Count == 0 ? "HP firmware CPU sensor" : "Selected view: " + temperatures.Min().ToString("0") + " to " + temperatures.Max().ToString("0") + " °C";
            foreach (var chart in new TrendChart[] { cpuChart, fanChart }) { chart.Data = selected; chart.EndUtc = end; chart.Duration = TimeSpan.FromMinutes(rangeMinutes); chart.InvalidateVisual(); }
            footer.Text = (paused ? "Paused" : preview ? "Simulated readings" : "Sampling every 2 seconds") + "  |  " + history.Count.ToString("N0") + " samples  |  Latest: " + latest.ToLocalTime().ToString("HH:mm:ss") + "  |  Export before closing";
        }
        private void Export(object sender, RoutedEventArgs e)
        {
            if (history.Count == 0) { MessageBox.Show(this, "There are no readings to export yet.", "Envy Monitor"); return; }
            var dialog = new SaveFileDialog { Title = "Export this monitoring session", Filter = "CSV data (*.csv)|*.csv", FileName = "EnvyMonitor-" + (preview ? "PREVIEW-" : "") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, history.ToCsv(), new UTF8Encoding(true)); status.Text = "Saved " + history.Count + " readings to " + dialog.FileName; }
            catch (Exception ex) { MessageBox.Show(this, "Could not save the CSV: " + ex.Message, "Envy Monitor", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void RestartElevated(object sender, RoutedEventArgs e)
        {
            try
            {
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
                Process.Start(start); Close();
            }
            catch (System.ComponentModel.Win32Exception ex)
            { status.Text = ex.NativeErrorCode == 1223 ? "Administrator request cancelled. Click Enable live sensors to try again." : "Could not enable live sensors: " + ex.Message; }
        }
        private Sample PreviewSample(DateTime time, int tick)
        {
            double wave = Math.Sin(tick / 21.0), small = Math.Sin(tick / 3.4);
            bool missing = tick % 157 >= 125 && tick % 157 <= 128;
            return new Sample { TimeUtc = time, CpuTemperature = 58 + 12 * wave + 3 * small,
                FanRaw = missing ? (double?)null : 2500 + Math.Round((wave + 1) * 500 / 50) * 50,
                Fan2Raw = missing ? (double?)null : 2200 + Math.Round((wave + 1) * 430 / 50) * 50,
                CpuLoad = 8 + (wave + 1) * 11 + Math.Abs(small) * 7, Source = "SIMULATED PREVIEW", FanStatus = missing ? "Simulated gap" : "Simulated firmware values" };
        }
        private void SeedPreview()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < 150; i++) history.Add(PreviewSample(now.AddSeconds((i - 150) * 2), i));
            previewTick = 150;
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var app = new Application();
                app.Run(new MonitorWindow(args.Any(a => string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase))));
                return 0;
            }
            catch (Exception ex)
            { MessageBox.Show("Envy Monitor could not start.\n\n" + ex.Message, "Envy Monitor", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }
    }
}
