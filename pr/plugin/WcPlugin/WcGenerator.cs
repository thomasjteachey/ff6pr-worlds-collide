using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WorldsCollide.PR
{
    // Runs the seed generator (tools/wc_seed.py: Worlds Collide + our porter) in a background
    // process with no window, and reports its "[wc] ..." progress lines. The game keeps
    // running; WcSeeds applies the finished seed on the main thread.
    //  Python: config [Generator] Python, else the bundled generator/python/python.exe next to
    //  this plugin (tools/wc_bundle.py), else the project's pr/.venv (development PC).
    static class WcGenerator
    {
        internal static string PythonSetting = "", ScriptSetting = "", ProjectDir = "";

        static Process proc;
        static readonly object gate = new object();
        static readonly List<string> lines = new List<string>();
        static string status = "", outDir;
        static int exitCode = int.MinValue;
        static DateTime started;

        public static bool Running { get { lock (gate) return proc != null && exitCode == int.MinValue; } }
        public static string Status { get { lock (gate) return status; } }
        public static double Seconds => (DateTime.Now - started).TotalSeconds;
        public static string OutDir => outDir;

        // finished since the last call: (ok, error message, output folder)
        public static bool TakeResult(out bool ok, out string error, out string dir)
        {
            lock (gate)
            {
                ok = false; error = null; dir = outDir;
                if (proc == null || exitCode == int.MinValue) return false;
                ok = exitCode == 0 && File.Exists(Path.Combine(outDir, "wc_seed.json"));
                if (!ok)
                {
                    var err = lines.LastOrDefault(l => l.StartsWith("[wc] error"));
                    error = err != null ? err.Substring(5) : $"the generator stopped (exit {exitCode}): " + (lines.LastOrDefault() ?? "no output");
                }
                proc.Dispose();
                proc = null;
                exitCode = int.MinValue;
                return true;
            }
        }

        public static string Log { get { lock (gate) return string.Join("\n", lines); } }

        // (python.exe, wc_seed.py), or an error
        public static (string Python, string Script, string Error) Resolve()
        {
            var here = Plugin.Folder;
            var py = PythonSetting;
            var script = ScriptSetting;
            var bundle = Path.Combine(here, "generator");
            if (string.IsNullOrEmpty(py) && File.Exists(Path.Combine(bundle, "python", "python.exe")))
                py = Path.Combine(bundle, "python", "python.exe");
            if (string.IsNullOrEmpty(script) && File.Exists(Path.Combine(bundle, "tools", "wc_seed.py")))
                script = Path.Combine(bundle, "tools", "wc_seed.py");
            if (!string.IsNullOrEmpty(ProjectDir))
            {
                if (string.IsNullOrEmpty(py)) py = Path.Combine(ProjectDir, "pr", ".venv", "Scripts", "python.exe");
                if (string.IsNullOrEmpty(script)) script = Path.Combine(ProjectDir, "pr", "tools", "wc_seed.py");
            }
            if (string.IsNullOrEmpty(py) || !File.Exists(py)) return (py, script, "the seed generator isn't installed (no Python found)");
            if (string.IsNullOrEmpty(script) || !File.Exists(script)) return (py, script, "the seed generator isn't installed (no wc_seed.py)");
            return (py, script, null);
        }

        public static string Start(string seed, string flags, string dir)
        {
            lock (gate)
            {
                if (proc != null) return "a seed is already being generated";
                var (py, script, error) = Resolve();
                if (error != null) return error;
                lines.Clear();
                status = "starting";
                outDir = dir;
                exitCode = int.MinValue;
                started = DateTime.Now;
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
                psi.ArgumentList.Add("--seed");
                psi.ArgumentList.Add(seed);
                psi.ArgumentList.Add("--flags=" + flags);  // "=" so a value starting with "-" isn't taken for an option
                psi.ArgumentList.Add("--out");
                psi.ArgumentList.Add(dir);
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONNOUSERSITE"] = "1";
                psi.Environment.Remove("PYTHONHOME");
                psi.Environment.Remove("PYTHONPATH");
                try
                {
                    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    p.OutputDataReceived += (_, e) => OnLine(e.Data);
                    p.ErrorDataReceived += (_, e) => OnLine(e.Data);
                    p.Exited += (_, __) => { lock (gate) { try { exitCode = p.ExitCode; } catch { exitCode = -1; } } };
                    if (!p.Start()) return "couldn't start the generator";
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    proc = p;
                    Plugin.L.LogInfo($"[wc] generating seed '{seed}' with {py} {script}");
                    return null;
                }
                catch (Exception e)
                {
                    return "couldn't start the generator: " + e.Message;
                }
            }
        }

        static void OnLine(string line)
        {
            if (line == null) return;
            lock (gate)
            {
                lines.Add(line);
                if (line.StartsWith("[wc] ")) status = line.Substring(5);
            }
            Plugin.L.LogInfo("[wc gen] " + line);
        }

        public static void Cancel()
        {
            lock (gate)
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
            }
        }
    }
}
