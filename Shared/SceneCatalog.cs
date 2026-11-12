namespace HonorControl.Shared;

internal static class SceneCatalog
{
    private static readonly Lazy<Dictionary<string, int>> ExecutableScenes = new(Read);

    public static int Classify(string? executable) => executable is null ? -1 :
        ExecutableScenes.Value.TryGetValue(executable, out int scene) ? scene : executable.ToLowerInvariant() switch
        {
            "zoom.exe" or "teams.exe" or "ms-teams.exe" => 0,
            "msedge.exe" or "firefox.exe" => 9,
            "vlc.exe" or "mpv.exe" or "potplayer64.exe" => 4,
            _ => -1
        };

    public static bool IsForegroundPriority(string? executable) => Classify(executable) is 0 or 1 or 5 or 13 or 16 or 17 or 20;

    private static Dictionary<string, int> Read()
    {
        var scenes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var resource = typeof(SceneCatalog).Assembly.GetManifestResourceStream("DarwinAScenes.tsv")
            ?? throw new InvalidOperationException("本机的场景分类表缺失。");
        using var reader = new StreamReader(resource);
        while (reader.ReadLine() is string line)
        {
            int tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line.AsSpan(tab + 1), out int scene)) scenes.Add(line[..tab], scene);
        }
        return scenes;
    }
}
