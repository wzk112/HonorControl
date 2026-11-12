using HonorControl.Shared;

namespace HonorControl;

internal sealed class GameSessionTracker
{
    private DateTimeOffset _lastSeen;
    public string Application { get; private set; } = "";

    public int Update(int foregroundScene, string foregroundApplication, IEnumerable<string> runningApplications, DateTimeOffset now)
    {
        if (foregroundScene is 1 or 17 && !IsGame(foregroundApplication)) foregroundScene = -1;
        if (foregroundScene is 1 or 17)
        {
            Application = foregroundApplication;
            _lastSeen = now;
            return foregroundScene;
        }
        string? runningGame = runningApplications.Where(IsGame).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(name => name.Equals(Application, StringComparison.OrdinalIgnoreCase)) ??
            runningApplications.FirstOrDefault(IsGame);
        if (runningGame is not null)
        {
            Application = runningGame;
            _lastSeen = now;
            return SceneCatalog.Classify(runningGame);
        }
        // Keep the mode through short gaps between a launcher and its game process.
        if (Application.Length > 0 && now - _lastSeen < TimeSpan.FromSeconds(45)) return 1;
        Application = "";
        return foregroundScene;
    }

    private static bool IsGame(string application) =>
        SceneCatalog.Classify(application) is 1 or 17 &&
        !application.Contains("launcher", StringComparison.OrdinalIgnoreCase) &&
        !IgnoredHelpers.Contains(application);

    private static readonly HashSet<string> IgnoredHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "eadesktop.exe", "eac.exe", "socialclubhelper.exe", "fcbrowser.exe", "fcbrowsermanager.exe",
        "halosearch.exe", "halotheme.exe", "halotray.exe", "halowp.exe", "tcgamer.exe", "t2gp.exe"
    };
}
