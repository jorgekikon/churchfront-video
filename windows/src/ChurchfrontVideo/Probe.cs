using System.IO;
using System.Text;
using System.Windows;
using CueATEM;

namespace CueVideo.App;

/// <summary>
/// CUEVIDEO_PROBE=1, a port of SelfTest.probe() on the Mac: connect to the switcher, wait for its
/// whole state, write it all down, save a picture of the window, and quit. Safe on a real
/// switcher, even during a service: the switcher connection is read-only (ATEMClient.ReadOnly
/// refuses every command before it can go out, and the report counts them), cameras aren't
/// connected (a self-test never applies the setup file's cameras), Claude's control server
/// isn't started and nothing is written to the setup file or the window preferences.
///
/// The switcher is CUEVIDEO_HOST (and CUEVIDEO_PORT), else the setup file's atemHost. The report
/// and picture go to CUEVIDEO_PROBE_OUT (a folder), else %APPDATA%\CueVideo\Logs\probe:
/// probe.txt and probe.png. Exit code 0 when the switcher answered, 1 when it didn't in 20 s.
/// windows\probe.ps1 runs it and prints the report.
/// </summary>
public static class Probe
{
    public static bool On => Environment.GetEnvironmentVariable("CUEVIDEO_PROBE") is string p && p.Length > 0 && p != "0";

    private static string OutDir =>
        Environment.GetEnvironmentVariable("CUEVIDEO_PROBE_OUT") is string o && o.Length > 0 ? o : Path.Combine(ConfigStore.Folder, "Logs", "probe");

    public static void Run(Window win)
    {
        var atem = Booth.Shared.Atem;
        var started = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime? onlineAt = null;
        timer.Tick += (_, _) =>
        {
            if (onlineAt == null && atem.Link.Kind == LinkKind.Online && atem.State.Inputs.Count > 0) onlineAt = DateTime.UtcNow;
            // Give the rest of the state dump a moment to arrive.
            if (onlineAt is DateTime t && (DateTime.UtcNow - t).TotalSeconds >= 1.5) { timer.Stop(); Finish(win, ok: true); }
            else if (onlineAt == null && (DateTime.UtcNow - started).TotalSeconds > 20) { timer.Stop(); Finish(win, ok: false); }
        };
        timer.Start();
    }

    private static void Finish(Window win, bool ok)
    {
        var atem = Booth.Shared.Atem;
        var s = atem.State;
        var r = new StringBuilder();
        void L(string line) => r.AppendLine(line);
        L($"Churchfront Video switcher probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"switcher address: {(atem.Host.Length > 0 ? atem.Host : "none")}");
        if (!ok)
        {
            L($"no switcher: {ControlTools.LinkText(atem.Link)} after 20 seconds. Check the address and that this computer is on the switcher's network.");
        }
        else
        {
            L($"switcher: {s.Product}, protocol {s.Version}, M/Es {s.MeCount}, keyers {s.KeyerCount}, DSKs {s.DskCount}, aux {s.AuxCount}");
            foreach (var i in s.Inputs.Values.OrderBy(i => i.Id))
                L($"input {i.Id} {i.Short} | {i.Long}{(i.IsBusSource ? "" : " (not on bus)")}");
            for (int me = 0; me < s.MeCount; me++)
            {
                var keys = s.KeyOnAir.TryGetValue(me, out var k) ? string.Join(", ", k.OrderBy(x => x.Key).Select(x => $"{x.Key + 1}={(x.Value ? "on" : "off")}")) : "";
                L($"M/E {me + 1}: program {s.Name(s.ProgramOf(me))}, preview {s.Name(s.PreviewOf(me))}, style {(s.Style.TryGetValue(me, out var st) ? st : -1)}, " +
                  $"next {(s.NextSelection.TryGetValue(me, out var n) ? n : -1)}, mix rate {(s.MixRate.TryGetValue(me, out var mr) ? mr : -1)}, " +
                  $"in transition {s.Get(s.InTransition, me)}, keys on air [{keys}], FTB {s.Get(s.FtbBlack, me)}");
            }
            L($"DSK on air [{string.Join(", ", s.DskOnAir.OrderBy(x => x.Key).Select(x => $"{x.Key + 1}={(x.Value ? "on" : "off")}"))}], " +
              $"tie [{string.Join(", ", s.DskTie.OrderBy(x => x.Key).Select(x => $"{x.Key + 1}={(x.Value ? "on" : "off")}"))}]");
            L($"aux [{string.Join(", ", s.Aux.OrderBy(x => x.Key).Select(x => $"{x.Key + 1}={s.Name(x.Value)}"))}]");
            L($"macros [{string.Join(", ", s.Macros.OrderBy(x => x.Key).Select(x => $"{x.Key + 1}={x.Value}"))}]");
            L($"stream status {s.StreamStatus}{(s.HasStreaming ? "" : " (none)")}, record status {s.RecordStatus}{(s.HasRecording ? "" : " (none)")}, tally entries {s.Tally.Count}");
        }
        L($"commands sent to the switcher: {atem.CommandsSent}, refused by read-only: {atem.CommandsBlocked}");
        try
        {
            Directory.CreateDirectory(OutDir);
            var png = Path.Combine(OutDir, "probe.png");
            if (Program.SaveWindow(win, png)) L($"picture of the window: {png}");
            var txt = Path.Combine(OutDir, "probe.txt");
            L($"this report: {txt}");
            File.WriteAllText(txt, r.ToString());
        }
        catch (Exception e) { r.AppendLine("couldn't save: " + e.Message); }
        Console.Out.Write(r.ToString());
        Application.Current.Shutdown(ok ? 0 : 1);
    }
}
