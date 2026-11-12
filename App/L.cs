using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HonorControl;

internal static class L
{
    public static string Language { get; private set; } = "zh-CN";
    public static bool English => Language == "en-US";
    private static readonly Lazy<Dictionary<string, string>> Strings = new(Read);
    private static readonly Lazy<(Regex Pattern, string Target)[]> Formats = new(() => Strings.Value
        .Where(pair => Regex.IsMatch(pair.Key, @"\{\d+(?:[^}]*)\}"))
        .OrderByDescending(pair => pair.Key.Length)
        .Select(pair => (Pattern: FormatPattern(pair.Key), Target: pair.Value)).ToArray());

    public static void Initialize()
    {
        try
        {
            string path = Path.Combine(AppPaths.UserState, "language.txt");
            string choice = File.Exists(path) ? File.ReadAllText(path).Trim() : CultureInfo.CurrentUICulture.Name;
            Language = choice.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        }
        catch { Language = "zh-CN"; }
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Language;
    }

    public static string T(string text)
    {
        if (!English || string.IsNullOrEmpty(text)) return text;
        if (Strings.Value.TryGetValue(text, out string? result)) return result;
        if (!Regex.IsMatch(text, "[\\u3400-\\u9fff]")) return text;
        foreach (var format in Formats.Value)
        {
            var match = format.Pattern.Match(text);
            if (!match.Success) continue;
            return Regex.Replace(format.Target, @"\{(\d+)(?:[^}]*)\}", placeholder =>
            {
                string captured = match.Groups["p" + placeholder.Groups[1].Value].Value;
                return Strings.Value.GetValueOrDefault(captured, captured);
            });
        }
        // Status messages may append a driver error or an executable name.
        foreach (var pair in Strings.Value.Where(pair => pair.Key.Length >= 4 && !pair.Key.Contains('{')).OrderByDescending(pair => pair.Key.Length))
            if (text.StartsWith(pair.Key, StringComparison.Ordinal)) return pair.Value + text[pair.Key.Length..];
        return text;
    }

    public static string F(FormattableString text) => English && Strings.Value.TryGetValue(text.Format, out var translation)
        ? string.Format(CultureInfo.CurrentCulture, translation, text.GetArguments().Select(value => value is string s ? T(s) : value).ToArray())
        : text.ToString(CultureInfo.CurrentCulture);

    private static Regex FormatPattern(string format)
    {
        int end = 0;
        var pattern = new System.Text.StringBuilder("\\A");
        foreach (Match part in Regex.Matches(format, @"\{(\d+)(?:[^}]*)\}"))
        {
            pattern.Append(Regex.Escape(format[end..part.Index]));
            pattern.Append("(?<p").Append(part.Groups[1].Value).Append(">[\\s\\S]*?)");
            end = part.Index + part.Length;
        }
        pattern.Append(Regex.Escape(format[end..])).Append("\\z");
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
    }

    private static Dictionary<string, string> Read()
    {
        using var resource = typeof(L).Assembly.GetManifestResourceStream("Translations.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? [];
    }
}
