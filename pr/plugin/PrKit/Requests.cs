using System;
using System.Collections.Generic;
using System.IO;

namespace PrKit
{
    // Test-run request files: BepInEx/<name>.request, key=value lines (or ';'-separated; a bare word
    // is a switch, "=1"). The next launch reads and deletes it before doing anything else, so a
    // request never outlives the run it was written for. tools/kit.py run writes the kit's own
    // (prkit_test.request); BNW's and WC's runs have theirs (bnw_probe, wc_test).
    public static class RequestFile
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var raw in text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) d[line] = "1";
                else if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        // The request's settings, or null when there is none. Deleted as it's read (one run only,
        // even if this one fails).
        public static Dictionary<string, string> Take(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception) { return null; }
            try { File.Delete(path); }
            catch (Exception) { }
            return Parse(text);
        }

        public static int Int(IDictionary<string, string> d, string key, int fallback) =>
            d != null && d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

        public static bool Flag(IDictionary<string, string> d, string key, bool fallback = false)
        {
            if (d == null || !d.TryGetValue(key, out var v)) return fallback;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "on" || v == "yes";
        }
    }
}
