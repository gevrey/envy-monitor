using System;
using EnvyMonitor;

internal static class SensorReaderTests
{
    private static int passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Test(string name, Action action)
    {
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    private static byte[] Fan(int count, int raw)
    {
        byte[] data = new byte[2 + 2 * count];
        data[0] = (byte)count;
        data[1] = (byte)(count >> 8);
        if (count > 0) { data[2] = (byte)raw; data[3] = (byte)(raw >> 8); }
        return data;
    }
    public static int Main()
    {
        try
        {
            Test("CPU parser rejects null and truncated responses", delegate
            {
                string error;
                foreach (byte[] data in new byte[][] { null, new byte[0], new byte[] { 1 }, new byte[] { 2, 55 } })
                {
                    Check(!SensorReader.ParseCpuTemperature(data, out error).HasValue, "Truncated temperature was fabricated");
                    Check(!String.IsNullOrEmpty(error), "Invalid CPU response has no explanation");
                }
            });
            Test("CPU parser rejects missing and implausible temperatures", delegate
            {
                string error;
                foreach (byte[] data in new byte[][] { new byte[] { 0, 55 }, new byte[] { 1, 0 }, new byte[] { 1, 126 }, new byte[] { 1, 255 }, new byte[] { 9, 55, 55, 55, 55, 55, 55, 55, 55, 55 } })
                    Check(!SensorReader.ParseCpuTemperature(data, out error).HasValue, "Invalid CPU response accepted");
            });
            Test("CPU parser decodes valid temperatures without guessing", delegate
            {
                string error;
                foreach (byte value in new byte[] { 1, 55, 70, 94, 125 })
                {
                    Check(SensorReader.ParseCpuTemperature(new byte[] { 1, value }, out error) == value, "CPU temperature decoded incorrectly");
                    Check(error == null, "Valid temperature reported error");
                }
            });
            Test("fan parser rejects null and truncated layouts", delegate
            {
                string state, error;
                foreach (byte[] data in new byte[][] { null, new byte[0], new byte[] { 1 }, new byte[] { 1, 0 }, new byte[] { 1, 0, 65 }, new byte[] { 2, 0, 65, 0 } })
                {
                    Check(!SensorReader.ParseFanRaw(data, out state, out error).HasValue, "Truncated fan value accepted");
                    Check(!String.IsNullOrEmpty(error), "Invalid fan layout has no explanation");
                }
            });
            Test("fan unavailable sentinels remain missing and do not assert stopped", delegate
            {
                string state, error;
                foreach (byte[] data in new byte[][] { Fan(0, 0), Fan(1, 0), Fan(1, 65535) })
                {
                    Check(!SensorReader.ParseFanRaw(data, out state, out error).HasValue, "Missing fan data became zero");
                    Check(!String.IsNullOrEmpty(state), "Missing fan data has no status");
                    Check(state.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) < 0, "Unavailable reading asserted fan stopped");
                }
            });
            Test("fan raw values preserve little endian bytes and neutral units", delegate
            {
                string state, error;
                foreach (int raw in new[] { 1, 65, 103, 255, 256, 2386, 3781, 65534 })
                {
                    Check(SensorReader.ParseFanRaw(Fan(1, raw), out state, out error) == raw, "Fan raw value was transformed");
                    Check(error == null, "Valid raw value reported error");
                    Check(state.IndexOf("RPM", StringComparison.OrdinalIgnoreCase) < 0, "Unverified raw sensor labeled RPM");
                }
            });
            Test("fan parser bounds provider counts", delegate
            {
                string state, error;
                foreach (int count in new[] { 1, 2, 3, 4 })
                    Check(SensorReader.ParseFanRaw(Fan(count, 103), out state, out error) == 103, "Supported count rejected");
                foreach (int count in new[] { 5, 8, 255 })
                    Check(!SensorReader.ParseFanRaw(Fan(count, 103), out state, out error).HasValue, "Implausible count accepted");
                Check(!SensorReader.ParseFanRaw(new byte[] { 0, 1, 65, 0 }, out state, out error).HasValue, "16-bit count decoded as 8-bit");
            });
            Test("live two-fan payload decodes independently without conversion", delegate
            {
                byte[] livePayload = { 0x02, 0x00, 0x60, 0x09, 0x34, 0x08 };
                string state, error;
                Check(SensorReader.ParseFanRaw(livePayload, 0, out state, out error) == 2400 && error == null, "First fan live payload decoded incorrectly");
                Check(SensorReader.ParseFanRaw(livePayload, 1, out state, out error) == 2100 && error == null, "Second fan live payload decoded incorrectly");
                Check(SensorReader.ParseFanRaw(livePayload, out state, out error) == 2400, "Original overload no longer reads first fan");
            });
            Test("invalid and unreported fan indexes cannot invent a sensor", delegate
            {
                string state, error;
                foreach (int index in new[] { Int32.MinValue, -1, 4, Int32.MaxValue })
                {
                    Check(!SensorReader.ParseFanRaw(Fan(2, 2400), index, out state, out error).HasValue, "Invalid index accepted");
                    Check(!String.IsNullOrEmpty(error), "Invalid index lacks explanation");
                }
                Check(!SensorReader.ParseFanRaw(Fan(1, 2400), 1, out state, out error).HasValue, "Second fan invented on one-fan payload");
                Check(error == null && state == "Fan not reported", "Unreported fan treated as hardware failure");
            });
            Test("second fan sentinels and truncation do not change first fan", delegate
            {
                string state, error;
                foreach (byte[] data in new byte[][] { new byte[] { 2, 0, 96, 9, 0, 0 }, new byte[] { 2, 0, 96, 9, 255, 255 } })
                {
                    Check(SensorReader.ParseFanRaw(data, 0, out state, out error) == 2400, "Unavailable second fan changed first fan");
                    Check(!SensorReader.ParseFanRaw(data, 1, out state, out error).HasValue, "Missing second fan became a value");
                }
                Check(!SensorReader.ParseFanRaw(new byte[] { 2, 0, 96, 9, 52 }, 1, out state, out error).HasValue, "Truncated second fan accepted");
                Check(!String.IsNullOrEmpty(error), "Truncated second fan lacks error");
            });
            Test("corrupt random sensor payloads do not throw or produce out-of-range numbers", delegate
            {
                Random random = new Random(170930);
                for (int i = 0; i < 10000; i++)
                {
                    byte[] data = new byte[random.Next(0, 129)]; random.NextBytes(data);
                    string state, error;
                    double? cpu = SensorReader.ParseCpuTemperature(data, out error);
                    double? fan = SensorReader.ParseFanRaw(data, out state, out error);
                    double? fan2 = SensorReader.ParseFanRaw(data, 1, out state, out error);
                    Check(!cpu.HasValue || (cpu >= 1 && cpu <= 125), "Fuzz payload produced invalid CPU temperature");
                    Check(!fan.HasValue || (fan >= 1 && fan <= 65534), "Fuzz payload produced invalid fan raw value");
                    Check(!fan2.HasValue || (fan2 >= 1 && fan2 <= 65534), "Fuzz payload produced invalid second fan raw value");
                }
            });
            Test("disposed reader reports unavailable data without trying hardware", delegate
            {
                SensorReader reader = new SensorReader(); reader.Dispose();
                SensorReading reading = reader.Read();
                Check(!reading.CpuTemperatureC.HasValue && !reading.FanRaw.HasValue && !reading.Fan2Raw.HasValue && !reading.FanRpm.HasValue, "Disposed reader fabricated a sensor reading");
                Check(!reading.FanRpmVerified && !String.IsNullOrEmpty(reading.Error), "Disposed reader omitted failure status");
                reader.Dispose();
            });
            Console.WriteLine("RESULT: " + passed + " tests passed."); return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL after " + passed + " passing tests: " + ex.Message); return 1;
        }
    }
}
