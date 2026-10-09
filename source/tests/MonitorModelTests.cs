using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using EnvyMonitor;

internal static class MonitorModelTests
{
    private static int passed;
    private static readonly DateTime Epoch = new DateTime(2026, 10, 9, 11, 0, 0, DateTimeKind.Utc);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static Sample Reading(int seconds, double? temperature)
    {
        return new Sample { TimeUtc = Epoch.AddSeconds(seconds), CpuTemperature = temperature };
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static int Main()
    {
        try
        {
            Test("temperature rejects absent, nonfinite and implausible readings", delegate
            {
                double?[] rejected = { null, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, -1, 0, 125.01, 255 };
                foreach (double? value in rejected)
                    Check(!Reading(0, value).CleanCopy().CpuTemperature.HasValue, "Invalid temperature retained: " + value);
                foreach (double value in new[] { 1.0, 55.0, 96.0, 125.0 })
                    Check(Reading(0, value).CleanCopy().CpuTemperature == value, "Valid temperature dropped");
            });
            Test("unverified raw fan numbers never become RPM", delegate
            {
                foreach (double raw in new[] { 65.0, 103.0 })
                {
                    Sample clean = new Sample { TimeUtc = Epoch, FanRaw = raw, FanRpm = raw, FanRpmVerified = false }.CleanCopy();
                    Check(clean.FanRaw == raw && !clean.FanRpm.HasValue, "Raw fan counter was treated as RPM");
                }
            });
            Test("verified fan RPM retains stopped and running readings", delegate
            {
                foreach (double rpm in new[] { 0.0, 2500.0, 20000.0 })
                    Check(new Sample { FanRpm = rpm, FanRpmVerified = true }.CleanCopy().FanRpm == rpm, "Verified RPM changed");
                foreach (double rpm in new[] { -1.0, 20001.0, Double.NaN, Double.PositiveInfinity })
                    Check(!new Sample { FanRpm = rpm, FanRpmVerified = true }.CleanCopy().FanRpm.HasValue, "Invalid RPM retained");
            });
            Test("raw fan and load ranges reject invalid values", delegate
            {
                Check(!new Sample { FanRaw = -1 }.CleanCopy().FanRaw.HasValue, "Negative raw counter retained");
                Check(!new Sample { FanRaw = 65536 }.CleanCopy().FanRaw.HasValue, "Oversize raw counter retained");
                foreach (double load in new[] { -0.01, 100.01, Double.NaN, Double.PositiveInfinity })
                    Check(!new Sample { CpuLoad = load }.CleanCopy().CpuLoad.HasValue, "Invalid load retained");
                Check(new Sample { CpuLoad = 0 }.CleanCopy().CpuLoad == 0, "Valid idle load lost");
                Check(new Sample { CpuLoad = 100 }.CleanCopy().CpuLoad == 100, "Valid full load lost");
            });
            Test("absent sensors stay absent in history and CSV", delegate
            {
                HistoryStore history = new HistoryStore(10);
                history.Add(Reading(0, null));
                Check(!history.Samples[0].CpuTemperature.HasValue && !history.Samples[0].FanRpm.HasValue && !history.Samples[0].FanRaw.HasValue && !history.Samples[0].Fan2Raw.HasValue, "Missing became zero");
                Check(history.ToCsv().Contains("2026-10-09T11:00:00.0000000Z,,,,,,false,,\r\n"), "Missing CSV sensors must be empty");
            });
            Test("second fan raw value is independent and sanitized", delegate
            {
                Sample sample = new Sample { TimeUtc = Epoch, FanRaw = 2400, Fan2Raw = 2100 }.CleanCopy();
                Check(sample.FanRaw == 2400 && sample.Fan2Raw == 2100 && !sample.FanRpm.HasValue, "Two fans were merged or labeled RPM");
                foreach (double value in new[] { -1.0, 65536.0, Double.NaN, Double.PositiveInfinity })
                    Check(!new Sample { Fan2Raw = value }.CleanCopy().Fan2Raw.HasValue, "Invalid second fan value retained");
                HistoryStore history = new HistoryStore(1); history.Add(sample);
                Check(history.Samples[0].Fan2Raw == 2100, "Second fan was omitted from recorded history");
            });
            Test("history capacity evicts exactly the oldest samples", delegate
            {
                HistoryStore history = new HistoryStore(3);
                for (int i = 0; i < 5; i++) history.Add(Reading(i, 50 + i));
                Check(history.Count == 3, "Capacity exceeded");
                Check(history.Samples.Select(x => x.CpuTemperature.Value).SequenceEqual(new[] { 52.0, 53.0, 54.0 }), "Incorrect retention order");
            });
            Test("duplicate and reversed timestamps do not corrupt history", delegate
            {
                HistoryStore history = new HistoryStore(3);
                history.Add(Reading(1, 51)); history.Add(Reading(1, 90)); history.Add(Reading(0, 99)); history.Add(Reading(2, 52));
                Check(history.Count == 2 && history.Samples[0].CpuTemperature == 51 && history.Samples[1].CpuTemperature == 52, "Duplicate or reversed samples retained");
            });
            Test("stored sample is isolated from later input mutation", delegate
            {
                HistoryStore history = new HistoryStore(3);
                Sample reading = Reading(0, 55);
                history.Add(reading); reading.CpuTemperature = 90;
                Check(history.Samples[0].CpuTemperature == 55, "Input mutation changed recorded history");
                Throws<NotSupportedException>(delegate { history.Samples.Add(Reading(1, 60)); });
            });
            Test("invalid capacity and null samples fail explicitly", delegate
            {
                Throws<ArgumentOutOfRangeException>(delegate { new HistoryStore(0); });
                Throws<ArgumentOutOfRangeException>(delegate { new HistoryStore(-1); });
                Throws<ArgumentNullException>(delegate { new HistoryStore(1).Add(null); });
            });
            Test("display time range includes both boundaries and excludes future samples", delegate
            {
                HistoryStore history = new HistoryStore(10);
                for (int i = 0; i < 6; i++) history.Add(Reading(i, 50 + i));
                List<Sample> range = history.GetRange(Epoch.AddSeconds(4), TimeSpan.FromSeconds(2));
                Check(range.Select(x => x.CpuTemperature.Value).SequenceEqual(new[] { 52.0, 53.0, 54.0 }), "Incorrect range boundaries");
            });
            Test("null sensor readings produce graph gaps", delegate
            {
                Sample[] samples = { Reading(0, 55), Reading(2, 56), Reading(4, null), Reading(6, 58), Reading(8, 59) };
                List<List<PlotPoint>> segments = PlotSegments.Build(samples, x => x.CpuTemperature, TimeSpan.FromSeconds(10));
                Check(segments.Count == 2 && segments[0].Count == 2 && segments[1].Count == 2, "Missing value was bridged");
                Check(segments.SelectMany(x => x).All(x => x.Value != 0), "Missing value became zero");
            });
            Test("nonfinite readings produce graph gaps", delegate
            {
                Sample[] samples = { Reading(0, 55), Reading(2, Double.NaN), Reading(4, 58), Reading(6, Double.PositiveInfinity), Reading(8, 59) };
                List<List<PlotPoint>> segments = PlotSegments.Build(samples, x => x.CpuTemperature, TimeSpan.FromSeconds(10));
                Check(segments.Count == 3 && segments.All(x => x.Count == 1), "Invalid reading was connected");
            });
            Test("second fan gaps do not remove valid first fan activity", delegate
            {
                Sample[] samples = {
                    new Sample { TimeUtc = Epoch, FanRaw = 2400, Fan2Raw = 2100 },
                    new Sample { TimeUtc = Epoch.AddSeconds(2), FanRaw = 2300, Fan2Raw = null },
                    new Sample { TimeUtc = Epoch.AddSeconds(4), FanRaw = 2200, Fan2Raw = 1900 }
                };
                List<List<PlotPoint>> first = PlotSegments.Build(samples, x => x.FanRaw, TimeSpan.FromSeconds(10));
                List<List<PlotPoint>> second = PlotSegments.Build(samples, x => x.Fan2Raw, TimeSpan.FromSeconds(10));
                Check(first.Count == 1 && first[0].Count == 3, "Valid first fan was lost");
                Check(second.Count == 2 && second[0].Count == 1 && second[1].Count == 1, "Second fan gap was bridged");
            });
            Test("resume and collection interruptions produce graph gaps", delegate
            {
                Sample[] samples = { Reading(0, 55), Reading(2, 56), Reading(20, 58), Reading(22, 59) };
                List<List<PlotPoint>> segments = PlotSegments.Build(samples, x => x.CpuTemperature, TimeSpan.FromSeconds(10));
                Check(segments.Count == 2 && segments.All(x => x.Count == 2), "Long time gap was bridged");
            });
            Test("empty and wholly unavailable graphs remain empty", delegate
            {
                Check(PlotSegments.Build(new Sample[0], x => x.CpuTemperature, TimeSpan.FromSeconds(10)).Count == 0, "Empty graph not empty");
                Check(PlotSegments.Build(new[] { Reading(0, null), Reading(2, null) }, x => x.CpuTemperature, TimeSpan.FromSeconds(10)).Count == 0, "Absent readings graphed");
            });
            Test("CSV preserves numeric units and invariant decimal separator", delegate
            {
                CultureInfo original = Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                    HistoryStore history = new HistoryStore(1);
                    history.Add(new Sample { TimeUtc = Epoch, CpuTemperature = 55.25, FanRaw = 2400, Fan2Raw = 2100, CpuLoad = 4.125, FanRpm = 103, FanRpmVerified = false, Source = "HP", FanStatus = "Raw counter" });
                    string csv = history.ToCsv();
                    Check(csv.StartsWith("TimestampUTC,CpuTemperatureC,FanRPM,FanRawCounter,Fan2RawCounter,CpuLoadPercent,FanRpmVerified,Source,FanStatus\r\n"), "CSV units ambiguous");
                    Check(csv.Contains("Z,55.25,,2400,2100,4.125,false,HP,Raw counter\r\n"), "CSV locale or fan semantics incorrect");
                }
                finally { Thread.CurrentThread.CurrentCulture = original; }
            });
            Test("CSV quotes commas, embedded quotes and line breaks", delegate
            {
                Check(HistoryStore.CsvCell("a,b") == "\"a,b\"", "Comma not quoted");
                Check(HistoryStore.CsvCell("a\"b") == "\"a\"\"b\"", "Quote not escaped");
                Check(HistoryStore.CsvCell("a\r\nb") == "\"a\r\nb\"", "Line break not quoted");
                Check(HistoryStore.CsvCell(null) == "", "Null CSV text not empty");
                Check(HistoryStore.CsvCell("HP sensor") == "HP sensor", "Simple text changed");
            });
            Console.WriteLine("RESULT: " + passed + " tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL after " + passed + " passing tests: " + ex.Message);
            return 1;
        }
    }
}
