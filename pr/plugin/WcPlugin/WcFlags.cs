using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WorldsCollide.PR
{
    // Worlds Collide's flags as the options screen sees them: wc_flags.json from
    // tools/wc_flags_export.py (WC's own argparse definitions), plus the state the player
    // edits. No Unity here: Wc.Tests checks parsing/serializing against WC itself.
    public enum FlagType { Bool, Int, Float, Str }

    public sealed class FlagDef
    {
        public string Flag, Label, Help, Section;
        public FlagType Type;
        public int Nargs = 1;          // 0 switch, 1 value, 2 two values (MIN MAX), -1 optional value ('?')
        public string[] Metavar = Array.Empty<string>();
        public double? Min, Max;
        public string[] Choices;       // allowed values (strings as WC prints them)
        public string Default;
        public ChoiceRow Group;        // the dropdown row this flag is an option of, if any
        public bool Text => Type == FlagType.Str && Choices == null;
    }

    public abstract class Row { public string Label; }
    public sealed class FlagRow : Row { public FlagDef Flag; }
    public sealed class ChoiceRow : Row
    {
        public List<FlagDef> Options = new List<FlagDef>();
        public List<string> OptionLabels = new List<string>();
    }

    public sealed class Section
    {
        public string Title;
        public string Port = "unknown";  // does the PR port carry it: yes / partly / no
        public List<Row> Rows = new List<Row>();
    }

    public sealed class WcFlagSet
    {
        public readonly Dictionary<string, FlagDef> Flags = new Dictionary<string, FlagDef>();
        public readonly List<Section> Sections = new List<Section>();
        public readonly Dictionary<string, string> Presets = new Dictionary<string, string>();
        public string WcVersion = "";

        public static WcFlagSet Load(string json)
        {
            var set = new WcFlagSet();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("wc_version", out var v)) set.WcVersion = v.GetString();
            foreach (var p in root.GetProperty("flags").EnumerateObject())
            {
                var e = p.Value;
                var d = new FlagDef { Flag = p.Name, Label = Str(e, "label") ?? p.Name, Help = Str(e, "help") ?? "" };
                d.Type = Str(e, "type") switch { "bool" => FlagType.Bool, "int" => FlagType.Int, "float" => FlagType.Float, _ => FlagType.Str };
                if (d.Type == FlagType.Bool) d.Nargs = 0;
                if (e.TryGetProperty("nargs", out var n))
                    d.Nargs = n.ValueKind == JsonValueKind.Number ? n.GetInt32() : n.GetString() == "?" ? -1 : 1;
                if (e.TryGetProperty("metavar", out var mv)) d.Metavar = mv.EnumerateArray().Select(x => x.GetString()).ToArray();
                if (e.TryGetProperty("min", out var mn)) d.Min = mn.GetDouble();
                if (e.TryGetProperty("max", out var mx)) d.Max = mx.GetDouble();
                if (e.TryGetProperty("choices", out var ch)) d.Choices = ch.EnumerateArray().Select(Text).ToArray();
                if (e.TryGetProperty("default", out var df) && df.ValueKind != JsonValueKind.Array) d.Default = Text(df);
                set.Flags[d.Flag] = d;
            }
            foreach (var s in root.GetProperty("sections").EnumerateArray())
            {
                var sec = new Section { Title = s.GetProperty("title").GetString(), Port = Str(s, "port") ?? "unknown" };
                foreach (var r in s.GetProperty("rows").EnumerateArray())
                {
                    if (r.GetProperty("kind").GetString() == "choice")
                    {
                        var row = new ChoiceRow { Label = Str(r, "label") };
                        foreach (var o in r.GetProperty("options").EnumerateArray())
                        {
                            var f = set.Flags[o.GetProperty("flag").GetString()];
                            f.Group = row;
                            f.Section = sec.Title;
                            row.Options.Add(f);
                            row.OptionLabels.Add(Str(o, "label") ?? f.Label);
                        }
                        sec.Rows.Add(row);
                    }
                    else
                    {
                        var f = set.Flags[r.GetProperty("flag").GetString()];
                        f.Section = sec.Title;
                        sec.Rows.Add(new FlagRow { Label = f.Label, Flag = f });
                    }
                }
                set.Sections.Add(sec);
            }
            if (root.TryGetProperty("presets", out var pr))
                foreach (var p in pr.EnumerateObject()) set.Presets[p.Name] = p.Value.GetString();
            return set;
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // numbers as Python prints them (WC's flags string): 2 -> "2", 2.0 (float flag) -> "2.0"
        static string Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => e.GetRawText(),
        };
    }

    // The flags the player has set: flag -> its values (none for switches). Flags the screen
    // doesn't know (a newer WC, hand-typed extras) are kept as typed and passed through.
    public sealed class FlagValues
    {
        public readonly WcFlagSet Defs;
        readonly Dictionary<string, List<string>> on = new Dictionary<string, List<string>>();
        public readonly List<string> Unknown = new List<string>();

        public FlagValues(WcFlagSet set) { Defs = set; }

        public bool IsOn(string flag) => on.ContainsKey(flag);
        public IReadOnlyList<string> Values(string flag) => on.TryGetValue(flag, out var v) ? v : (IReadOnlyList<string>)Array.Empty<string>();
        public int Count => on.Count;

        public void Clear() { on.Clear(); Unknown.Clear(); }

        public void Off(string flag) => on.Remove(flag);

        // turn a flag on with the given values (or its defaults); a dropdown keeps one option
        public void Set(string flag, IEnumerable<string> values = null)
        {
            if (!Defs.Flags.TryGetValue(flag, out var d)) return;
            if (d.Group != null)
                foreach (var o in d.Group.Options)
                    if (o.Flag != flag) on.Remove(o.Flag);
            on[flag] = values?.ToList() ?? Defaults(d);
        }

        public static List<string> Defaults(FlagDef d)
        {
            // WC's default when it's a real value (-sc1 defaults to "", not one of its choices)
            var def = d.Default != null && d.Default != "" && (d.Choices == null || d.Choices.Contains(d.Default)) ? d.Default : null;
            string one = def ?? d.Choices?.FirstOrDefault(c => c != "") ?? Num(d, d.Min ?? 0);
            return d.Nargs switch
            {
                0 => new List<string>(),
                -1 => new List<string>(),
                2 => new List<string> { Num(d, d.Min ?? 0), Num(d, d.Max ?? d.Min ?? 0) },
                _ => new List<string> { one ?? "" },
            };
        }

        public void Parse(string text)
        {
            Clear();
            var tokens = Tokens.Split(text ?? "");
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (!Defs.Flags.TryGetValue(t, out var d))
                {
                    Unknown.Add(t);
                    continue;
                }
                var values = new List<string>();
                int want = d.Nargs == -1 ? 1 : d.Nargs;
                while (values.Count < want && i + 1 < tokens.Count && !IsFlagToken(tokens[i + 1]))
                    values.Add(tokens[++i]);
                Set(t, values);
            }
        }

        bool IsFlagToken(string t) => Defs.Flags.ContainsKey(t) || (t.StartsWith("-") && t.Length > 1 && !char.IsDigit(t[1]) && t[1] != '.');

        // in the screen's order (WC's sections), unknown tokens last
        public string Serialize()
        {
            var parts = new List<string>();
            foreach (var s in Defs.Sections)
                foreach (var r in s.Rows)
                    foreach (var d in r is ChoiceRow c ? c.Options : new List<FlagDef> { ((FlagRow)r).Flag })
                        if (on.TryGetValue(d.Flag, out var v))
                        {
                            parts.Add(d.Flag);
                            parts.AddRange(v.Select(Tokens.Quote));
                        }
            parts.AddRange(Unknown.Select(Tokens.Quote));
            return string.Join(" ", parts);
        }

        // --- editing (the screen's left/right) --------------------------------------------

        // The values a flag's value slot steps through; null for free text.
        public static List<string> Steps(FlagDef d)
        {
            if (d.Choices != null) return d.Choices.ToList();
            if (d.Type == FlagType.Int && d.Min.HasValue && d.Max.HasValue && d.Max - d.Min <= 100000)
            {
                var list = new List<string>();
                for (var x = d.Min.Value; x <= d.Max.Value; x++) list.Add(Num(d, x));
                return list;
            }
            return null;
        }

        // left/right on a single flag row: off <-> on (switches), or step the value, off below the first
        public void Step(FlagDef d, int slot, int dir, int amount = 1)
        {
            if (d.Nargs == 0)
            {
                if (IsOn(d.Flag)) Off(d.Flag); else Set(d.Flag);
                return;
            }
            if (!IsOn(d.Flag))
            {
                if (dir > 0) Set(d.Flag);
                return;
            }
            var values = on[d.Flag];
            if (d.Nargs == -1 && values.Count == 0)
            {
                // optional value: on (WC's default) -> first value, or off
                if (dir > 0) values.Add(Steps(d)?.FirstOrDefault() ?? Num(d, d.Min ?? 0));
                else Off(d.Flag);
                return;
            }
            slot = Math.Max(0, Math.Min(slot, values.Count - 1));
            var cur = values[slot];
            var steps = Steps(d);
            if (steps != null)
            {
                int i = steps.IndexOf(cur);
                if (i < 0) i = 0;
                int j = i + dir * amount;
                if (j < 0)
                {
                    if (i == 0 && slot == 0) { if (d.Nargs == -1) values.Clear(); else Off(d.Flag); }
                    else values[slot] = steps[0];
                    return;
                }
                values[slot] = steps[Math.Min(j, steps.Count - 1)];
            }
            else if (d.Type == FlagType.Float || d.Type == FlagType.Int)
            {
                double x = double.TryParse(cur, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
                double step = d.Type == FlagType.Float ? 0.5 : 1;
                x += dir * step * amount;
                if (d.Min.HasValue && x < d.Min) { if (slot == 0 && dir < 0 && cur == Num(d, d.Min.Value)) { Off(d.Flag); return; } x = d.Min.Value; }
                if (d.Max.HasValue && x > d.Max) x = d.Max.Value;
                values[slot] = Num(d, x);
            }
            // free text: left/right do nothing (edit it as text)
        }

        public void SetText(FlagDef d, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) Off(d.Flag);
            else Set(d.Flag, d.Nargs == 2 ? Tokens.Split(text).Take(2) : new[] { text });
        }

        // a dropdown row: which option is on (-1: none)
        public int Selected(ChoiceRow r) => r.Options.FindIndex(o => IsOn(o.Flag));

        public void Select(ChoiceRow r, int index)
        {
            foreach (var o in r.Options) Off(o.Flag);
            if (index >= 0 && index < r.Options.Count) Set(r.Options[index].Flag);
        }

        public string Describe(FlagDef d)
        {
            if (!IsOn(d.Flag)) return "Off";
            var v = on[d.Flag];
            if (d.Nargs == 0) return "On";
            if (v.Count == 0) return "On";
            if (d.Nargs == 2) return $"{v[0]} - {(v.Count > 1 ? v[1] : "?")}";
            var text = v[0] == "" ? "(none)" : v[0];
            if (d.Metavar.Length == 1 && d.Metavar[0] == "PERCENT") text += "%";
            return text;
        }

        public static string Num(FlagDef d, double x) =>
            d.Type == FlagType.Float ? x.ToString("0.0###", CultureInfo.InvariantCulture) : ((long)Math.Round(x)).ToString(CultureInfo.InvariantCulture);
    }

    // shlex-like splitting/quoting for flag strings (WC's own strings never need quotes, but a
    // value can be empty: -nm1 "")
    public static class Tokens
    {
        public static List<string> Split(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            bool inTok = false;
            char quote = '\0';
            foreach (var c in s)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else cur.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; inTok = true; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (inTok) { list.Add(cur.ToString()); cur.Clear(); inTok = false; }
                    continue;
                }
                cur.Append(c);
                inTok = true;
            }
            if (inTok) list.Add(cur.ToString());
            return list;
        }

        public static string Quote(string v) =>
            v.Length == 0 ? "\"\"" : v.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\'') ? "\"" + v.Replace("\"", "") + "\"" : v;
    }
}
