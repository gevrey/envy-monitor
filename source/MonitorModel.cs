using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EnvyMonitor
{
    public sealed class Sample
    {
        public DateTime TimeUtc;
        public double? CpuTemperature;
        public double? FanRpm;
        public double? FanRaw;
        public double? Fan2Raw;
        public double? CpuLoad;
        public string Source;
        public string FanStatus;
        public bool FanRpmVerified;

        public Sample CleanCopy()
        {
            return new Sample { TimeUtc = TimeUtc.ToUniversalTime(),
                CpuTemperature = Valid(CpuTemperature, 1, 125),
                FanRpm = FanRpmVerified ? Valid(FanRpm, 0, 20000) : null,
                FanRaw = Valid(FanRaw, 0, 65535), Fan2Raw = Valid(Fan2Raw, 0, 65535), CpuLoad = Valid(CpuLoad, 0, 100),
                Source = Source ?? "", FanStatus = FanStatus ?? "", FanRpmVerified = FanRpmVerified };
        }

        public static double? Valid(double? value, double min, double max)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value)
                || value.Value < min || value.Value > max) return null;
            return value;
        }
    }

    public sealed class HistoryStore
    {
        private readonly List<Sample> items = new List<Sample>();
        private readonly int capacity;
        public HistoryStore(int capacity) { if (capacity < 1) throw new ArgumentOutOfRangeException("capacity"); this.capacity = capacity; }
        public IList<Sample> Samples { get { return items.AsReadOnly(); } }
        public int Count { get { return items.Count; } }
        public void Add(Sample value)
        {
            if (value == null) throw new ArgumentNullException("value");
            Sample copy = value.CleanCopy();
            if (items.Count > 0 && copy.TimeUtc <= items[items.Count - 1].TimeUtc) return;
            items.Add(copy);
            if (items.Count > capacity) items.RemoveRange(0, items.Count - capacity);
        }
        public List<Sample> GetRange(DateTime endUtc, TimeSpan duration)
        {
            DateTime start = endUtc.ToUniversalTime() - duration;
            DateTime end = endUtc.ToUniversalTime();
            return items.Where(x => x.TimeUtc >= start && x.TimeUtc <= end).ToList();
        }
        public static string CsvCell(string text)
        {
            text = text ?? "";
            if (text.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0) return "\"" + text.Replace("\"", "\"\"") + "\"";
            return text;
        }
        private static string Number(double? value) { return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : ""; }
        public string ToCsv()
        {
            StringBuilder b = new StringBuilder("TimestampUTC,CpuTemperatureC,FanRPM,FanRawCounter,Fan2RawCounter,CpuLoadPercent,FanRpmVerified,Source,FanStatus\r\n");
            foreach (Sample s in items) b.Append(s.TimeUtc.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                .Append(Number(s.CpuTemperature)).Append(',').Append(Number(s.FanRpm)).Append(',')
                .Append(Number(s.FanRaw)).Append(',').Append(Number(s.Fan2Raw)).Append(',').Append(Number(s.CpuLoad)).Append(',')
                .Append(s.FanRpmVerified ? "true" : "false").Append(',').Append(CsvCell(s.Source)).Append(',')
                .Append(CsvCell(s.FanStatus)).Append("\r\n");
            return b.ToString();
        }
    }

    public sealed class PlotPoint
    {
        public DateTime TimeUtc;
        public double Value;
    }
    public static class PlotSegments
    {
        public static List<List<PlotPoint>> Build(IEnumerable<Sample> samples, Func<Sample, double?> metric, TimeSpan maxGap)
        {
            List<List<PlotPoint>> result = new List<List<PlotPoint>>();
            List<PlotPoint> current = null;
            DateTime previous = DateTime.MinValue;
            foreach (Sample s in samples)
            {
                double? value = metric(s);
                if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value)) { current = null; continue; }
                if (current == null || (previous != DateTime.MinValue && s.TimeUtc - previous > maxGap))
                { current = new List<PlotPoint>(); result.Add(current); }
                current.Add(new PlotPoint { TimeUtc = s.TimeUtc, Value = value.Value }); previous = s.TimeUtc;
            }
            return result;
        }
    }

    public sealed class CpuUsage
    {
        [StructLayout(LayoutKind.Sequential)] private struct TimeParts { public uint Low; public uint High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out TimeParts idle, out TimeParts kernel, out TimeParts user);
        private ulong lastIdle, lastTotal;
        private bool ready;
        public double? Read()
        {
            TimeParts idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return null;
            ulong total = kernel.Value + user.Value;
            double? result = null;
            if (ready && total > lastTotal && idle.Value >= lastIdle)
                result = Math.Max(0, Math.Min(100, 100.0 * (1.0 - (double)(idle.Value - lastIdle) / (total - lastTotal))));
            ready = true; lastIdle = idle.Value; lastTotal = total; return result;
        }
    }
}
