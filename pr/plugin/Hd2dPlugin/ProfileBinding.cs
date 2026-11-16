using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace Hd2d
{
    // Binds the public fields of Profile that apply to one section as config keys
    // (bool, float, or Rgb written as "r, g, b"). No Unity types: tested offline.
    public sealed class ProfileBinding
    {
        public static Action<string> Warn = _ => { };

        readonly string section;
        readonly Profile defaults;
        readonly List<(FieldInfo field, ConfigEntryBase entry)> entries = new List<(FieldInfo, ConfigEntryBase)>();

        public int Count => entries.Count;
        public IEnumerable<string> Keys => entries.Select(e => e.field.Name);

        public static IEnumerable<FieldInfo> FieldsFor(Mode mode) =>
            typeof(Profile).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<OnlyAttribute>()?.Allows(mode) ?? true);

        public ProfileBinding(ConfigFile cfg, string section, Mode mode, Profile defaults)
        {
            this.section = section;
            this.defaults = defaults;
            foreach (var f in FieldsFor(mode))
            {
                var desc = f.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                ConfigEntryBase e = f.GetValue(defaults) switch
                {
                    bool b => cfg.Bind(section, f.Name, b, desc),
                    float x => cfg.Bind(section, f.Name, x, desc),
                    Rgb c => cfg.Bind(section, f.Name, c.ToString(), desc + " Three numbers."),
                    _ => null,
                };
                if (e == null) throw new NotSupportedException($"Profile.{f.Name}: unsupported type {f.FieldType}");
                entries.Add((f, e));
            }
        }

        public Profile Read()
        {
            var p = defaults.Clone();
            foreach (var (f, e) in entries)
            {
                if (f.FieldType == typeof(Rgb))
                {
                    if (Rgb.TryParse(e.BoxedValue as string, out var c)) f.SetValue(p, c);
                    else Warn($"[{section}] {f.Name}: '{e.BoxedValue}' is not 'r, g, b'; using {f.GetValue(defaults)}");
                }
                else f.SetValue(p, e.BoxedValue);
            }
            return p;
        }

        // Values still at an older version's default move to the current default; values
        // the player changed stay. Returns how many moved.
        public int Upgrade(IDictionary<string, object> oldDefaults)
        {
            int moved = 0;
            foreach (var (f, e) in entries)
            {
                if (!oldDefaults.TryGetValue(f.Name, out var old) || !Same(e.BoxedValue, old)) continue;
                object now = f.GetValue(defaults);
                object value = now is Rgb c ? c.ToString() : now;
                if (Same(e.BoxedValue, now)) continue;
                e.BoxedValue = value;
                moved++;
            }
            return moved;
        }

        static bool Same(object stored, object value)
        {
            switch (value)
            {
                case Rgb c: return Rgb.TryParse(stored as string, out var s) && s.Near(c);
                case float x: return stored is float y && Math.Abs(x - y) < 1e-5f;
                default: return Equals(stored, value);
            }
        }
    }

    public static class ConfigUpgrade
    {
        // Brings a config file written by an older plugin up to date: untouched values follow
        // the new defaults, keys a profile section no longer has go (those sections belong
        // to the plugin), and the file remembers the defaults version.
        public static (int moved, int retired) Run(ConfigFile cfg, IEnumerable<(Mode mode, ProfileBinding binding)> bindings,
                                                   ConfigEntry<int> version)
        {
            int from = version.Value;
            if (from >= Defaults.Version) return (0, 0);
            int moved = 0, retired = 0;
            var sections = new HashSet<string>();
            foreach (var (mode, binding) in bindings)
            {
                moved += binding.Upgrade(Defaults.History(mode, from));
                sections.Add(mode.ToString());
            }
            // ConfigFile keeps unknown keys (OrphanedEntries, not public) and writes them back
            if (typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphans)
                foreach (var key in orphans.Keys.ToList())
                    if (sections.Contains(key.Section)) { orphans.Remove(key); retired++; }
            version.Value = Defaults.Version;
            cfg.Save();
            return (moved, retired);
        }
    }
}
