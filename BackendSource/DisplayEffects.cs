using System;
using System.Globalization;
using System.IO;
using System.Linq;

internal sealed class DisplayEffects
{
    public readonly string Style;
    public readonly int Temperature;
    public bool Active { get { return Style != "off" || Temperature != 0; } }

    private static readonly double[][] ReadingMatrices = {
        new[] { .270,.631,.099, .235,.562,.086, .194,.482,.071 },
        new[] { .333,.333,.189, .333,.333,.183, .333,.333,.161 },
        new[] { .333,.333,.130, .333,.333,.120, .333,.333,.100 },
        new[] { .310,.531,.099, .285,.462,.086, .244,.382,.071 }
    };

    public DisplayEffects(string style, int temperature)
    {
        if (style != "off" && style != "eye" && style != "reading")
            throw new ArgumentException("Unknown display effect.");
        if (temperature < -100 || temperature > 100)
            throw new ArgumentOutOfRangeException("temperature");
        Style = style;
        Temperature = temperature;
    }

    private static string SettingsPath(string directory) { return Path.Combine(directory, "display-effects.txt"); }

    public static DisplayEffects Read(string directory)
    {
        string file = SettingsPath(directory);
        if (!File.Exists(file)) return new DisplayEffects("off", 0);
        string[] settings = File.ReadAllText(file).Trim().Split(' ');
        int value;
        if (settings.Length != 2 || !Int32.TryParse(settings[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new InvalidDataException("Display effect settings are invalid.");
        return new DisplayEffects(settings[0], value);
    }

    public void Save(string directory)
    {
        string path = SettingsPath(directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Style + " " + Temperature.ToString(CultureInfo.InvariantCulture));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    public double[] Apply(double[] calibration, int samples, int localHour)
    {
        if (!Active) return calibration;
        double[] matrix = Style == "reading" ? ReadingMatrices[localHour / 6] : null;
        double warmth = Temperature / 100.0;
        double redGain = warmth < 0 ? 1 + warmth * .13 : 1;
        double greenGain = warmth < 0 ? 1 + warmth * .04 : 1 - warmth * .05;
        double blueGain = (Style == "eye" ? .91 : 1) * (warmth > 0 ? 1 - warmth * .20 : 1);
        int stride = samples - 1;
        var output = new double[samples * samples * samples * 3];
        for (int offset = 0; offset < output.Length; offset += 3)
        {
            int sample = offset / 3;
            double r = calibration == null ? sample / (samples * samples) / (double)stride : calibration[offset];
            double g = calibration == null ? sample / samples % samples / (double)stride : calibration[offset + 1];
            double b = calibration == null ? sample % samples / (double)stride : calibration[offset + 2];
            output[offset] = Clamp(redGain * (matrix == null ? r : matrix[0] * r + matrix[1] * g + matrix[2] * b));
            output[offset + 1] = Clamp(greenGain * (matrix == null ? g : matrix[3] * r + matrix[4] * g + matrix[5] * b));
            output[offset + 2] = Clamp(blueGain * (matrix == null ? b : matrix[6] * r + matrix[7] * g + matrix[8] * b));
        }
        return output;
    }

    private static double Clamp(double value) { return Math.Min(1, Math.Max(0, value)); }
}
