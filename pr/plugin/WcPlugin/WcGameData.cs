using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Ff6Mod.Shared;

namespace WorldsCollide.PR
{
    // The copies of the game's own files Worlds Collide works from (the generator's vanilla folder,
    // its cache, the baseline, the Magicite folder) are made on the player's machine from their own
    // game: the mod is distributed and its zip carries none of them (tools/wc_gamedata.py,
    // PORTING.md "WC without the game's files"). generator/gamedata.json lists them; the setup
    // writes generator/vanilla/.complete (that manifest's id) last. Run from Plugin.Load before
    // anything reads them: this plugin loads before Magicite (BepInEx orders independent plugins
    // by GUID), so Magicite finds its folder made on the same start, no restart.
    static class WcGameData
    {
        const int TimeoutMs = 15 * 60 * 1000;
        public static string Error;     // the last setup's failure, for the Worlds Collide screen

        public static void EnsureSetUp()
        {
            var gen = Path.Combine(Plugin.Folder, "generator");
            var manifest = Path.Combine(gen, "gamedata.json");
            if (!File.Exists(manifest)) return;     // a build from before the manifest: it carries the files
            string id;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                id = doc.RootElement.GetProperty("id").GetString() ?? "";
            }
            catch (Exception e)
            {
                Error = $"{manifest}: {e.Message}";
                Plugin.L.LogError("[wc] game data: " + Error);
                return;
            }
            var mark = Path.Combine(gen, "vanilla", ".complete");
            try
            {
                if (File.Exists(mark) && File.ReadAllText(mark).Trim() == id) return;
            }
            catch { }
            var (py, _, err) = WcGenerator.Resolve();
            var script = Path.Combine(gen, "tools", "wc_gamedata.py");
            if (err != null || !File.Exists(script))
            {
                Error = err ?? $"no {script}";
                Plugin.L.LogError("[wc] game data can't be made: " + Error);
                return;
            }
            Plugin.L.LogInfo("[wc] game data: making Worlds Collide's copies of the game's files from this game (once; up to a minute or two)");
            var psi = new ProcessStartInfo
            {
                FileName = py,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("setup");
            psi.ArgumentList.Add("--game");
            psi.ArgumentList.Add(BepInEx.Paths.GameRootPath);
            psi.ArgumentList.Add("--magicite");
            psi.ArgumentList.Add(MagiciteDir.FolderPath("WorldsCollide"));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment.Remove("PYTHONHOME");
            psi.Environment.Remove("PYTHONPATH");
            var output = new StringBuilder();
            var sw = Stopwatch.StartNew();
            try
            {
                using var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    Error = $"the setup took over {TimeoutMs / 60000} minutes";
                }
                else
                {
                    p.WaitForExit();    // the output's last lines
                    if (p.ExitCode != 0) Error = $"the setup failed (exit {p.ExitCode})";
                }
            }
            catch (Exception e)
            {
                Error = "couldn't start the setup: " + e.Message;
            }
            string text;
            lock (output) text = output.ToString().TrimEnd();
            if (Error == null && File.Exists(mark))
            {
                Plugin.L.LogInfo($"[wc] game data made in {sw.Elapsed.TotalSeconds:0} s: {Last(text)}");
                return;
            }
            Error ??= "the setup didn't finish";
            Plugin.L.LogError($"[wc] game data: {Error}; seeds stay off until it works (it runs again next start):\n{Tail(text, 1500)}");
        }

        static string Last(string text)
        {
            var i = text.LastIndexOf('\n');
            return i < 0 ? text : text.Substring(i + 1);
        }

        static string Tail(string text, int n) => text.Length <= n ? text : text.Substring(text.Length - n);
    }
}
