using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class BrightnessRhythm
{
    private const int Minimum = 53;
    private const int Maximum = 93;
    private const int MaxRise = 5;
    private const int MinBattery = 60;
    private const int CycleSeconds = 60;
    private readonly string journal;
    private readonly Stopwatch cycle = new Stopwatch();
    private string instance;
    private byte baseline;
    private byte expected;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte Ac, Flags, Percent, Saver;
        public uint Lifetime, FullLifetime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    public BrightnessRhythm(string state)
    {
        journal = Path.Combine(state, "brightness-rhythm-journal.txt");
        if (!File.Exists(journal)) return;
        var fields = File.ReadAllLines(journal);
        if (fields.Length == 3 && Byte.TryParse(fields[1], out baseline) && Byte.TryParse(fields[2], out expected))
            instance = fields[0];
        Restore();
    }

    private static ManagementObject ReadMonitor()
    {
        using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
        using (var monitors = search.Get())
        {
            if (monitors.Count != 1) return null;
            foreach (ManagementObject monitor in monitors) return monitor;
        }
        return null;
    }

    private static void Set(string name, byte level)
    {
        using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
        using (var monitors = search.Get())
        {
            foreach (ManagementObject monitor in monitors)
            using (monitor)
            {
                if (!String.Equals((string)monitor["InstanceName"], name, StringComparison.OrdinalIgnoreCase)) continue;
                using (var input = monitor.GetMethodParameters("WmiSetBrightness"))
                {
                    input["Timeout"] = 0u;
                    input["Brightness"] = level;
                    using (var result = monitor.InvokeMethod("WmiSetBrightness", input, null)) { }
                }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    using (var readback = ReadMonitor())
                        if (readback != null && (string)readback["InstanceName"] == name && (byte)readback["CurrentBrightness"] == level) return;
                    Thread.Sleep(75);
                }
                throw new InvalidOperationException("The display brightness did not match the requested level.");
            }
        }
        throw new InvalidOperationException("The brightness controller is unavailable.");
    }

    public void Update(bool enabled)
    {
        PowerStatus power;
        if (!enabled || !GetSystemPowerStatus(out power) || power.Saver != 0 ||
            (power.Ac != 1 && (power.Percent < MinBattery || power.Percent == 255)))
        {
            Restore();
            return;
        }

        using (var monitor = ReadMonitor())
        {
            if (monitor == null) return;
            string name = (string)monitor["InstanceName"];
            byte current = (byte)monitor["CurrentBrightness"];
            if (name != instance || current != expected)
            {
                File.Delete(journal);
                instance = name;
                baseline = expected = current;
                cycle.Restart();
            }
            if (baseline < Minimum || baseline >= Maximum) return;

            double time = cycle.Elapsed.TotalSeconds % CycleSeconds;
            double phase = time < 30 ? 0 : time < 40 ? (time - 30) / 10 : (60 - time) / 20;
            double rise = (1 - Math.Cos(phase * Math.PI)) / 2;
            int target = baseline + (int)Math.Round(Math.Min(MaxRise, Maximum - baseline) * rise);
            byte next = baseline;
            foreach (byte level in (byte[])monitor["Level"])
                if (level <= target && level > next) next = level;
            if (next == expected) return;

            WriteJournal(name, baseline, next);
            try { Set(name, next); }
            catch
            {
                WriteJournal(name, baseline, expected);
                throw;
            }
            expected = next;
            if (next == baseline) File.Delete(journal);
        }
    }

    private void WriteJournal(string name, byte original, byte written)
    {
        string temp = journal + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        File.WriteAllLines(temp, new[] { name, original.ToString(), written.ToString() });
        if (File.Exists(journal)) File.Replace(temp, journal, null);
        else File.Move(temp, journal);
    }

    public void Restore()
    {
        if (instance != null && baseline != expected)
        {
            using (var monitor = ReadMonitor())
            {
                if (monitor == null) return;
                if ((string)monitor["InstanceName"] == instance && (byte)monitor["CurrentBrightness"] == expected)
                    Set(instance, baseline);
            }
        }
        instance = null;
        cycle.Reset();
        File.Delete(journal);
    }
}
