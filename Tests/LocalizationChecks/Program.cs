using System.Text.Json;
using System.Text.RegularExpressions;
using HonorControl;
void Require(bool test, string message) { if (!test) throw new Exception(message); }
Directory.CreateDirectory(AppPaths.UserState);
File.WriteAllText(Path.Combine(AppPaths.UserState, "language.txt"), "en-US");
L.Initialize();
Require(L.English && L.T("性能与散热") == "Performance and cooling", "English resources did not load.");
Require(L.F($" · 第 {5} 档") == " · Level 5", "HUNTER level formatting failed.");
using var json = JsonDocument.Parse("{\"Ssid\":\"会议\",\"Profile\":\"游戏\",\"Note\":\"自动性能模式已关闭。\",\"Null\":null,\"Number\":3}");
Require(json.RootElement.Text("Ssid") == "会议" && json.RootElement.Text("Profile") == "游戏", "Localization changed a network identifier.");
Require(json.RootElement.Text("Note") == "Automatic performance is off.", "Backend status was not localized.");
Require(json.RootElement.Number("Null") == -1 && json.RootElement.Number("Number") == 3, "Nullable status parsing failed.");
using var source = typeof(L).Assembly.GetManifestResourceStream("Translations.json")!;
var pairs = JsonSerializer.Deserialize<Dictionary<string,string>>(source)!;
foreach(var pair in pairs) {
    var original = Regex.Matches(pair.Key, @"\{(\d+)(?:[^}]*)\}").Select(m=>m.Groups[1].Value).Order().ToArray();
    var translated = Regex.Matches(pair.Value, @"\{(\d+)(?:[^}]*)\}").Select(m=>m.Groups[1].Value).Order().ToArray();
    Require(original.SequenceEqual(translated), "Translation changed placeholders: " + pair.Key);
}
string logs=Path.Combine(AppPaths.UserState,"logs");Directory.CreateDirectory(logs);
string log=Path.Combine(logs,"activity.log");
for(int i=0;i<4;i++){File.WriteAllText(log,new string('a',HonorControl.Diagnostics.DiagnosticLog.MaxBytes-1));HonorControl.Diagnostics.DiagnosticLog.Append(log,"next line\n");}
Require(File.Exists(log+".2") && !File.Exists(log+".3"),"Log rotation did not retain exactly three files.");
File.SetLastWriteTimeUtc(log+".1",DateTime.UtcNow.AddDays(-15));
string recovery=Path.Combine(logs,"restore.json");File.WriteAllText(recovery,"retain");File.SetLastWriteTimeUtc(recovery,DateTime.UtcNow.AddYears(-1));
HonorControl.Diagnostics.DiagnosticLog.Clean(logs);
Require(!File.Exists(log+".1") && File.ReadAllText(recovery)=="retain","Retention removed recovery data or kept stale logs.");
Console.WriteLine("Localization, formatting, nullable status and raw network identifiers verified.");
namespace HonorControl { internal static class AppPaths { public static string UserState => Path.Combine(AppContext.BaseDirectory,"test-state"); } }
namespace Microsoft.Windows.Globalization { internal static class ApplicationLanguages { public static string PrimaryLanguageOverride { get; set; } = ""; } }
