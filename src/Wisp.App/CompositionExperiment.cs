using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wisp.App;

// Private diagnostic build only; not for release. Polls a local experiment file so a timed A/B can
// change how the HUD asks Windows to compose it while Forza stays focused. Without the file Wisp
// keeps its normal behavior: passive updates requested and no compositor clock boost.
internal static class CompositionExperiment
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp");
    private static readonly string ExperimentPath = Path.Combine(Folder, "composition-experiment.json");
    private static readonly string LogPath = Path.Combine(Folder, "composition-experiment.log");
    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static volatile bool _passive = true;
    private static bool _boost;
    private static string? _last;

    internal static bool PassiveUpdatesRequested => _passive;

    internal static void Start() =>
        _timer ??= new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

    private static void Poll()
    {
        lock (Gate)
        {
            string text;
            try { text = File.Exists(ExperimentPath) ? File.ReadAllText(ExperimentPath) : ""; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            if (text == _last) return;
            _last = text;
            bool passive = true, boost = false;
            if (text.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.TryGetProperty("passive", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        passive = p.GetBoolean();
                    if (document.RootElement.TryGetProperty("boost", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        boost = b.GetBoolean();
                }
                catch (JsonException) { return; }
            }
            var windows = "";
            if (passive != _passive)
            {
                _passive = passive;
                windows = OverlayPassiveUpdate.ReapplyAll();
            }
            var boostResult = 0;
            if (boost != _boost)
            {
                _boost = boost;
                try { boostResult = DCompositionBoostCompositorClock(boost); }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { boostResult = error.HResult; }
            }
            try
            {
                File.AppendAllText(LogPath, "{\"qpc\":" + Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"passive\":" + (passive ? "true" : "false") + ",\"boost\":" + (boost ? "true" : "false") +
                    ",\"boostHr\":" + boostResult.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ",\"windows\":\"" + windows + "\"}" + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("dcomp.dll")]
    private static extern int DCompositionBoostCompositorClock([MarshalAs(UnmanagedType.Bool)] bool enable);
}
