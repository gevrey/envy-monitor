using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace EnvyMonitor
{
    public sealed class SensorReading
    {
        public DateTime TimeUtc { get; set; }
        public double? CpuTemperatureC { get; set; }
        public double? FanRaw { get; set; }
        public double? Fan2Raw { get; set; }
        public double? FanRpm { get; set; }
        public string FanState { get; set; }
        public string Fan2State { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public bool AccessDenied { get; set; }
        public bool FanRpmVerified { get; set; }
        public string CpuPayloadHex { get; set; }
        public string FanPayloadHex { get; set; }
    }

    /// <summary>
    /// Reads HP's existing firmware thermal provider. No driver installation,
    /// fan control, firmware writes, power policy changes or WMI Put calls.
    /// Query layout verified against installed signed HP Diagnostics 3.3.2.0:
    /// HPI.System.dll, HPThermal.HpBiosThermal and HpqBDataIn/Command classes.
    /// Command 1 / type 0x28: selector 0 CPU status, selector 7 fan reading.
    /// Requires an administrator token on the validated HP Envy 17-da0xxx.
    /// </summary>
    public sealed class SensorReader : IDisposable
    {
        private ManagementScope scope;
        private ManagementObject device;
        private ManagementClass inputClass;
        private bool disposed;
        private readonly object sync = new object();
        private const string Provider = "HP firmware thermal sensor";

        public SensorReading Read()
        {
            lock (sync)
            {
                var result = new SensorReading {
                    TimeUtc = DateTime.UtcNow, Source = Provider,
                    FanState = "Unavailable", Fan2State = "Unavailable", FanRpmVerified = false
                };
                var errors = new List<string>();
                bool reconnectNeeded = false;
                try
                {
                    if (disposed) throw new ObjectDisposedException("SensorReader");
                    EnsureConnected();
                }
                catch (Exception ex)
                {
                    ResetConnection();
                    SetFailure(result, ex, "Sensor access");
                    return result;
                }
                try
                {
                    byte[] data = ReadThermal(0);
                    result.CpuPayloadHex = HexPrefix(data);
                    string parseError;
                    result.CpuTemperatureC = ParseCpuTemperature(data, out parseError);
                    if (parseError != null) errors.Add(parseError);
                }
                catch (Exception ex)
                {
                    reconnectNeeded = true;
                    SetFailure(result, ex, "CPU sensor");
                    errors.Add(result.Error);
                }
                try
                {
                    byte[] data = ReadThermal(7);
                    result.FanPayloadHex = HexPrefix(data);
                    string state;
                    string parseError;
                    result.FanRaw = ParseFanRaw(data, out state, out parseError);
                    result.FanState = state;
                    if (parseError != null) errors.Add(parseError);
                    result.Fan2Raw = ParseFanRaw(data, 1, out state, out parseError);
                    result.Fan2State = state;
                    if (parseError != null && !errors.Contains(parseError)) errors.Add(parseError);
                    // HP's conversion varies across firmware providers. The current
                    // machine has shown contradictory RPM labels, so no unverified
                    // conversion or guessed percentage is exposed as an RPM value.
                    result.FanRpm = null;
                }
                catch (Exception ex)
                {
                    reconnectNeeded = true;
                    SetFailure(result, ex, "Fan sensor");
                    errors.Add(result.Error);
                }
                result.Error = errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
                if (reconnectNeeded) ResetConnection();
                result.TimeUtc = DateTime.UtcNow;
                return result;
            }
        }

        private void EnsureConnected()
        {
            if (device != null && inputClass != null) return;
            ResetConnection();
            scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions {
                EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate,
                Timeout = TimeSpan.FromSeconds(3)
            });
            scope.Connect();
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM hpqBIntM"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false }))
            using (ManagementObjectCollection found = searcher.Get())
            {
                foreach (ManagementObject item in found)
                {
                    device = new ManagementObject(scope, item.Path, null);
                    item.Dispose();
                    break;
                }
            }
            if (device == null) throw new InvalidOperationException("HP firmware sensor provider was not found.");
            inputClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
        }

        // This is intentionally private and restricted to two verified read selectors.
        private byte[] ReadThermal(byte selector)
        {
            if (selector != 0 && selector != 7) throw new ArgumentOutOfRangeException("selector");
            using (ManagementBaseObject input = inputClass.CreateInstance())
            using (ManagementBaseObject args = device.GetMethodParameters("hpqBIOSInt128"))
            {
                input["Active"] = true;
                input["Sign"] = new byte[] { 83, 69, 67, 85 };
                input["Command"] = 1U;
                input["CommandType"] = 0x28U;
                input["Size"] = 4U;
                input["hpqBData"] = new byte[] { selector, 0, 0, 0 };
                args["InData"] = input;
                using (ManagementBaseObject output = device.InvokeMethod("hpqBIOSInt128", args,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) }))
                {
                    if (output == null) throw new InvalidOperationException("HP sensor returned no response.");
                    using (ManagementBaseObject body = output["OutData"] as ManagementBaseObject)
                    {
                        if (body == null) throw new InvalidOperationException("HP sensor returned no data object.");
                        object statusValue = body["rwReturnCode"];
                        if (statusValue == null) throw new InvalidOperationException("HP sensor returned no status code.");
                        uint status = Convert.ToUInt32(statusValue);
                        if (status != 0) throw new InvalidOperationException("HP sensor status " + status + ".");
                        byte[] data = body["Data"] as byte[];
                        return data == null ? null : (byte[])data.Clone();
                    }
                }
            }
        }

        public static double? ParseCpuTemperature(byte[] data, out string error)
        {
            error = null;
            if (data == null || data.Length < 2) { error = "CPU sensor data is missing or truncated."; return null; }
            int count = data[0];
            if (count == 0) { error = "CPU temperature is unavailable."; return null; }
            if (count > 8 || data.Length < count + 1) { error = "CPU sensor data has an invalid layout."; return null; }
            int value = data[1];
            if (value < 1 || value > 125) { error = "CPU temperature reading is invalid."; return null; }
            return value;
        }

        public static double? ParseFanRaw(byte[] data, out string state, out string error)
        {
            return ParseFanRaw(data, 0, out state, out error);
        }

        // fanIndex is zero based. No-data and invalid-index cases remain gaps.
        public static double? ParseFanRaw(byte[] data, int fanIndex, out string state, out string error)
        {
            error = null;
            state = "Unavailable";
            if (fanIndex < 0 || fanIndex > 3) { error = "Fan index is outside the supported range."; return null; }
            if (data == null || data.Length < 2) { error = "Fan sensor data is missing or truncated."; return null; }
            int count = data[0] | (data[1] << 8);
            if (count == 0) { state = "No fan reading"; return null; }
            if (count > 4 || data.Length < 2 + 2 * count) { error = "Fan sensor data has an invalid layout."; return null; }
            if (fanIndex >= count) { state = "Fan not reported"; return null; }
            int offset = 2 + 2 * fanIndex;
            int raw = data[offset] | (data[offset + 1] << 8);
            if (raw == 0 || raw == 65535) { state = "No valid tachometer reading"; return null; }
            state = "Firmware fan value";
            return raw;
        }

        private static void SetFailure(SensorReading result, Exception ex, string context)
        {
            var me = ex as ManagementException;
            var ce = ex as COMException;
            bool denied = ex is UnauthorizedAccessException ||
                (me != null && me.ErrorCode == ManagementStatus.AccessDenied) ||
                (ce != null && (ce.ErrorCode == unchecked((int)0x80070005) || ce.ErrorCode == unchecked((int)0x80041003)));
            result.AccessDenied = result.AccessDenied || denied;
            result.Error = denied ? "Administrator access is required for the HP live sensors." : context + ": " + ex.Message;
        }

        private static string HexPrefix(byte[] data)
        {
            return data == null ? null : BitConverter.ToString(data, 0, Math.Min(16, data.Length));
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                ResetConnection();
            }
        }

        private void ResetConnection()
        {
            if (device != null) device.Dispose();
            if (inputClass != null) inputClass.Dispose();
            device = null;
            inputClass = null;
        }
    }
}
