using System;
using System.Collections.Generic;
using System.Linq;
using Ff6Mod.Shared;
using Il2CppSystem.Input;
using UnityEngine;
using Device = Il2CppSystem.Input.KeyConfig.KeyValue.InputDeviceType;
using Object = UnityEngine.Object;

namespace WorldsCollide.PR
{
    // The in-game Worlds Collide screen: the website's options (every WC flag, presets, seed),
    // "Generate" makes the seed in the background and applies it to the running game, no
    // restart (WcGenerator, WcSeeds). Opens from its own entry in the title screen's menu
    // ("Worlds Collide", or "Mods" in the other modes: WcTitleMenu), or F7 anywhere.
    //  D-pad/arrows: move, change   A/Enter: toggle, edit, run   B/Esc: to the page list (left),
    //  and from there close   Left on a row without a value: the page list   L1/R1 (PageUp/
    //  PageDown): previous/next page   text: type, Ctrl+V pastes (Deck: Steam+X)
    // Drawn with IMGUI: the game's build keeps GUI.Box/Label/Button, not GUILayout's fields,
    // so the rows, cursor and text editing are our own.
    static partial class WcScreen
    {
        internal static bool Open;
        internal static WcFlagSet Defs;
        static FlagValues values;
        static string seedText = "", presetName = "";
        static int page, cursor, scroll, slot;
        static bool onPages;            // the cursor is on the page list (left) instead of the rows
        static string editing;          // what the text editor is editing ("seed", "flags", a flag)
        static string editBuffer = "";
        static string note = "";
        static float noteUntil;
        static bool onTitle;
        static int titleCheck;
        static bool releaseWait;        // after closing, keep the game's input off until keys are up
        static bool openWait;           // after opening, ignore the press that opened the screen
        static string deleteId;         // the earlier seed whose row offers Delete (Left/Right)
        static bool deleteSure;         // ... and A was pressed once: the next A deletes

        sealed class UiRow
        {
            public string Label, Value = "", Help = "", Key;   // Key: an earlier seed's id
            public bool Header, Disabled;
            public int Indent;
            public Action Activate;
            public Action<int, int> Step;  // dir, amount
        }

        public static void Init(WcFlagSet defs)
        {
            Defs = defs;
            values = new FlagValues(defs);
            var active = WcSeeds.Active;
            if (active != null)
            {
                values.Parse(active.Flags);
                seedText = active.SeedText;
                presetName = active.Preset ?? "";
            }
            else if (defs.Presets.Count > 0)
            {
                presetName = defs.Presets.Keys.First();
                values.Parse(defs.Presets[presetName]);
                seedText = RandomSeed();
            }
        }

        static string RandomSeed()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var rng = new System.Random();
            return new string(Enumerable.Range(0, 12).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        }

        static void Say(string text, float seconds = 6)
        {
            note = text;
            noteUntil = Time.unscaledTime + seconds;
            Plugin.L.LogInfo("[wc screen] " + text);
        }

        // the title scene is the current sub-scene (Last.Management.SceneManager): a field read, not
        // a search of every object in the scene (that ran 3 times a second, a hitch on the Deck)
        static bool OnTitle()
        {
            if (++titleCheck % 10 == 1) onTitle = GameScene.OnTitle();
            return onTitle;
        }

        public static void Show(bool show, bool track = false)
        {
            if (show == Open) return;
            Open = show;
            if (show && track != Track) { Track = track; page = 0; cursor = 0; scroll = 0; slot = 0; }
            editing = null;
            if (show) { WcInput.Blocking = true; releaseWait = false; openWait = true; onPages = false; }
            else releaseWait = true;
            PauseClicks(show);
        }

        // The title menu under the screen mustn't take mouse/touch clicks: its canvases' raycasters
        // are paused while the screen is open. (This used to switch the whole EventSystem off,
        // which can also stop the Input System's gamepad actions the game reads.)
        static readonly List<UnityEngine.UI.GraphicRaycaster> paused = new List<UnityEngine.UI.GraphicRaycaster>();

        static void PauseClicks(bool pause)
        {
            try
            {
                if (pause)
                {
                    paused.Clear();
                    foreach (var r in Object.FindObjectsOfType<UnityEngine.UI.GraphicRaycaster>())
                        if (r != null && r.enabled) { r.enabled = false; paused.Add(r); }
                }
                else
                {
                    foreach (var r in paused) if (r != null) r.enabled = true;
                    paused.Clear();
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("screen-clicks", e); }
        }

        // --- per frame: gamepad/keyboard through the game's input -------------------------

        public static void Update()
        {
            bool title = OnTitle();
            WcSeeds.UpdateNewGame(title);
            if (releaseWait)
            {
                bool held = WcInput.Held(Key.Cancel) || WcInput.Held(Key.Action) || WcInput.Held(Key.Select);
                if (!held) { releaseWait = false; WcInput.Blocking = false; }
            }
            if (!Open) return;
            if (openWait)
            {
                // the Confirm press that picked the title menu entry must not also pick a row here
                if (WcInput.Held(Key.Action) || WcInput.Held(Key.Cancel)) return;
                openWait = false;
            }
            if (editing != null)
            {
                // typing comes through OnGUI; the pad can still confirm or cancel
                if (WcInput.Down(Key.Action, Device.GamePad)) CommitEdit();
                else if (WcInput.Down(Key.Cancel, Device.GamePad)) editing = null;
                return;
            }
            int up = WcInput.Repeat(0, Key.Up), down = WcInput.Repeat(1, Key.Down);
            int left = WcInput.Repeat(2, Key.Left), right = WcInput.Repeat(3, Key.Right);
            Nav(up, down, left, right, WcInput.Down(Key.Left), WcInput.Down(Key.Action), WcInput.Down(Key.Cancel),
                WcInput.Down(Key.PageUp) || WcInput.Down(Key.SwitchLeft), WcInput.Down(Key.PageDown) || WcInput.Down(Key.SwitchRight));
        }

        // one frame of buttons (counts from key repeat, presses as bools)
        static void Nav(int up, int down, int left, int right, bool leftPress, bool a, bool b, bool prev, bool next)
        {
            var rows = Rows();
            if (prev) Page(-1);
            if (next) Page(+1);
            if (onPages)
            {
                // the page list: Up/Down pick a page (its rows show at once), A/Right go into it
                if (up > 0) Page(-1);
                if (down > 0) Page(+1);
                if (right > 0 || a) { onPages = false; rows = Rows(); if (rows.Count > 0 && rows[cursor].Header) Move(rows, +1); }
                else if (b) Show(false);
                return;
            }
            if (up > 0) Move(rows, -1);
            if (down > 0) Move(rows, +1);
            if (left > 0)
            {
                // Left changes a value; on a row without one it goes to the page list
                var r = cursor < rows.Count ? rows[cursor] : null;
                if (r != null && r.Step != null && !r.Disabled) StepRow(rows, -1, left);
                else if (leftPress) onPages = true;
            }
            if (right > 0) StepRow(rows, +1, right);
            if (a) ActivateRow(rows);
            if (b) onPages = true;  // B: back to the page list (B there closes)
        }

        // headless check of the controller flow: rows -B-> page list -Down-> next page -A-> its
        // rows -B-> page list -B-> closed; LB/RB step pages from the rows
        internal static string NavTest()
        {
            bool wasOpen = Open;
            int keepPage = page, keepCursor = cursor;
            var steps = new List<string>();
            try
            {
                Open = true; onPages = false; page = 0; cursor = 0;
                string State() => (Open ? "" : "closed ") + (onPages ? "list" : "rows") + $" p{page}";
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                Nav(0, 1, 0, 0, false, false, false, false, false); steps.Add("Down:" + State());
                Nav(0, 0, 0, 0, false, true, false, false, false); steps.Add("A:" + State());
                Nav(0, 0, 0, 0, false, false, false, false, true); steps.Add("RB:" + State());
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                Nav(0, 0, 0, 0, false, false, true, false, false); steps.Add("B:" + State());
                return string.Join(" ", steps);
            }
            finally
            {
                Open = wasOpen; onPages = false; page = keepPage; cursor = keepCursor; scroll = 0;
                WcInput.Blocking = Open; releaseWait = false;
            }
        }

        // headless check of Delete in the seed list (WcTest): on the seed's row Right offers
        // Delete, A asks, A deletes; the row's value after each press ("gone" once deleted)
        internal static string DeleteTest(string id)
        {
            bool wasOpen = Open, wasTitle = onTitle;
            int keepPage = page, keepCursor = cursor;
            var steps = new List<string>();
            try
            {
                Open = true; onPages = false; page = 0; onTitle = GameScene.OnTitle();
                int At() => MainRows().FindIndex(r => r.Key == id);
                string State() { int i = At(); return i < 0 ? "gone" : MainRows()[i].Value; }
                cursor = At();
                if (cursor < 0) return "no row for the seed";
                Nav(0, 0, 0, 1, false, false, false, false, false); steps.Add("Right: " + State());
                Nav(0, 0, 0, 0, false, true, false, false, false); steps.Add("A: " + State());
                Nav(0, 0, 0, 0, false, true, false, false, false); steps.Add("A: " + State());
                return string.Join(" | ", steps);
            }
            finally
            {
                Open = wasOpen; onTitle = wasTitle; onPages = false; page = keepPage; cursor = keepCursor; scroll = 0;
                deleteId = null; deleteSure = false;
                WcInput.Blocking = Open; releaseWait = false;
            }
        }

        static int PageCount => Track ? TrackPages.Length : Plugin.WcMode ? Defs.Sections.Count + 1 : 1;

        static void Move(List<UiRow> rows, int dir)
        {
            if (rows.Count == 0) return;
            int c = cursor;
            for (int i = 0; i < rows.Count; i++)
            {
                c = (c + dir + rows.Count) % rows.Count;
                if (!rows[c].Header) break;
            }
            cursor = c;
            slot = 0;
        }

        static void Page(int dir)
        {
            int pages = PageCount;
            page = (page + dir + pages) % pages;
            cursor = 0;
            scroll = 0;
            slot = 0;
            var rows = Rows();
            if (rows.Count > 0 && rows[0].Header) Move(rows, +1);
        }

        static void StepRow(List<UiRow> rows, int dir, int amount)
        {
            if (cursor >= rows.Count) return;
            var r = rows[cursor];
            if (r.Disabled || r.Step == null) return;
            r.Step(dir, amount);
        }

        static void ActivateRow(List<UiRow> rows)
        {
            if (cursor >= rows.Count) return;
            var r = rows[cursor];
            if (r.Disabled) { if (!string.IsNullOrEmpty(r.Help)) Say(r.Help); return; }
            r.Activate?.Invoke();
        }

        // --- rows -------------------------------------------------------------------------

        static List<UiRow> Rows() => Track ? TrackRows() : page == 0 ? MainRows() : FlagRows(Defs.Sections[page - 1]);

        static List<UiRow> MainRows()
        {
            var rows = new List<UiRow>();
            bool wc = Plugin.WcMode;
            bool title = onTitle;
            if (!wc)
            {
                rows.Add(new UiRow { Header = true, Label = "Worlds Collide's data isn't installed (its Magicite folder): reinstall the mod." });
                rows.Add(new UiRow { Label = "Close", Activate = () => Show(false) });
                return rows;
            }
            var presets = Defs.Presets.Keys.ToList();
            rows.Add(new UiRow
            {
                Label = "Preset",
                Value = presetName == "" ? "Custom" : presetName,
                Help = "A preset sets every flag (as on the website). Change any flag on the pages after this one (L/R).",
                Step = (d, _) =>
                {
                    int i = presets.IndexOf(presetName) + d;
                    if (i < -1) i = presets.Count - 1;
                    if (i >= presets.Count) i = -1;
                    presetName = i < 0 ? "" : presets[i];
                    if (presetName != "") values.Parse(Defs.Presets[presetName]);
                },
                Activate = () => { if (presetName != "") { values.Parse(Defs.Presets[presetName]); Say($"Flags set from {presetName}."); } },
            });
            rows.Add(new UiRow
            {
                Label = "Seed",
                Value = seedText == "" ? "(random)" : seedText,
                Help = "The same seed and flags make the same game as the website. A: type a seed. Left/right: a new random seed.",
                Step = (d, _) => seedText = RandomSeed(),
                Activate = () => StartEdit("seed", seedText),
            });
            var flags = values.Serialize();
            rows.Add(new UiRow
            {
                Label = "Flags",
                Value = flags.Length > 60 ? flags.Substring(0, 57) + "..." : flags == "" ? "(none)" : flags,
                Help = $"{values.Count} flags set{(values.Unknown.Count > 0 ? $", {values.Unknown.Count} the screen doesn't know (kept)" : "")}. A: edit as text (paste a flag string from the website with Ctrl+V).",
                Activate = () => StartEdit("flags", flags),
            });
            rows.Add(new UiRow
            {
                Label = "Paste flags from clipboard",
                Help = "Replace the flags with the clipboard's flag string (copy it from the website's seed page).",
                Activate = () =>
                {
                    var clip = GUIUtility.systemCopyBuffer ?? "";
                    if (!clip.Contains("-")) { Say("The clipboard has no flags."); return; }
                    values.Parse(clip);
                    presetName = "";
                    Say($"{values.Count} flags pasted{(values.Unknown.Count > 0 ? $"; {values.Unknown.Count} unknown tokens kept as typed" : "")}.");
                },
            });
            rows.Add(new UiRow
            {
                Label = "Copy flags to clipboard",
                Activate = () => { GUIUtility.systemCopyBuffer = values.Serialize(); Say("Flags copied."); },
            });
            rows.Add(new UiRow
            {
                Label = WcGenerator.Running ? $"Generating... {WcGenerator.Status}" : "Generate and play this seed",
                Disabled = !title || WcGenerator.Running,
                Help = !title ? "Seeds change the whole game: return to the title screen to make or switch one."
                     : "Makes the seed (Worlds Collide, then the port to the Pixel Remaster: a few seconds) and applies it at once. " +
                       "Then start a New Game. Saves remember their seed.",
                Activate = Generate,
            });
            rows.Add(new UiRow { Header = true, Label = "Pages marked * work partly in the Pixel Remaster port; dimmed ones not yet (WC's events)." });
            var active = WcSeeds.Active;
            rows.Add(new UiRow { Header = true, Label = "Current seed" });
            rows.Add(new UiRow
            {
                Label = active == null ? "None: the game's own data" : $"{active.SeedText}",
                Value = active == null ? "" : active.Hash,
                Indent = 1,
                Help = active == null ? "Generate a seed to play Worlds Collide." : $"Flags: {active.Flags}\nMade {active.Created} (WC {active.WcVersion}). A: copy its seed and flags into the fields above.",
                Activate = active == null ? (Action)null : () =>
                {
                    values.Parse(active.Flags);
                    seedText = active.SeedText;
                    presetName = "";
                    Say("Seed and flags copied from the current seed.");
                },
            });
            var saved = WcSeeds.All().Where(s => s.Id != active?.Id).ToList();
            bool deleteShown = false;
            if (saved.Count > 0)
            {
                rows.Add(new UiRow { Header = true, Label = "Earlier seeds (A: play it again   Left/Right: delete it)" });
                foreach (var s in saved)
                {
                    // Left/Right turn the row under the cursor to Delete; A asks once more, then deletes
                    bool del = deleteId == s.Id && cursor == rows.Count;
                    deleteShown |= del;
                    rows.Add(new UiRow
                    {
                        Key = s.Id,
                        Label = $"{s.SeedText}",
                        Value = !del ? $"{s.Hash}   {s.Created}" : deleteSure ? "Delete it? A: yes, Left/Right: no" : "Delete   (A)",
                        Indent = 1,
                        Disabled = !title,
                        Help = !title ? "Return to the title screen to switch or delete seeds."
                             : del ? $"Deletes seed {s.SeedText} ({s.Hash}, made {s.Created}) from this game. Not if a save still uses it."
                             : $"Flags: {s.Flags}",
                        Step = (d, _) => { deleteId = deleteId == s.Id ? null : s.Id; deleteSure = false; },
                        Activate = () =>
                        {
                            if (deleteId == s.Id && !deleteSure) { deleteSure = true; return; }
                            if (deleteId == s.Id)
                            {
                                deleteId = null; deleteSure = false;
                                var derr = WcSeeds.Delete(s);
                                Say(derr == null ? $"Seed {s.SeedText} ({s.Hash}) deleted." : "Not deleted: " + derr, 8);
                                return;
                            }
                            var err = WcSeeds.Apply(s);
                            Say(err == null ? $"Seed {s.SeedText} ({s.Hash}) is back. Start a New Game or load one of its saves." : "Couldn't apply: " + err, 8);
                        },
                    });
                }
            }
            if (!deleteShown) { deleteId = null; deleteSure = false; }   // the cursor left that row
            if (active != null)
                rows.Add(new UiRow
                {
                    Label = "Play without a seed (the game's own data)",
                    Disabled = !title,
                    Activate = () => { var err = WcSeeds.Apply(null); Say(err == null ? "Back to the game's own data." : err); },
                });
            rows.Add(new UiRow { Label = "Close", Activate = () => Show(false) });
            return rows;
        }

        static void Generate()
        {
            var seed = seedText.Trim();
            if (seed == "") seedText = seed = RandomSeed();
            if (seed.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_'))) { Say("Seeds use letters, digits, - and _."); return; }
            var id = WcSeeds.NewId(seed);
            var flags = values.Serialize();
            var err = WcGenerator.Start(seed, flags, WcSeeds.DirFor(id));
            Say(err ?? "Generating... (you can close this screen)", err == null ? 3 : 10);
        }

        // finished generations are applied here (main thread)
        public static void Poll()
        {
            if (!WcGenerator.TakeResult(out bool ok, out string error, out string dir)) return;
            if (!ok) { Say("The seed couldn't be made: " + error, 15); return; }
            var s = WcSeeds.Load(System.IO.Path.GetFileName(dir));
            if (s == null) { Say("The seed was made but can't be read.", 10); return; }
            if (!onTitle) { Say($"Seed {s.SeedText} is ready; return to the title screen to play it (Earlier seeds).", 12); return; }
            var err = WcSeeds.Apply(s);
            WcSeeds.Prune();
            Say(err == null ? $"Seed {s.SeedText} ({s.Hash}) is ready: start a New Game." : "Couldn't apply the seed: " + err, 12);
        }

        static List<UiRow> FlagRows(Section sec)
        {
            var rows = new List<UiRow>();
            foreach (var r in sec.Rows)
            {
                if (r is FlagRow fr) rows.Add(FlagUi(fr.Flag, fr.Label, 0));
                else if (r is ChoiceRow cr)
                {
                    int sel = values.Selected(cr);
                    var help = string.Join("\n", cr.Options.Select((o, i) => $"{cr.OptionLabels[i]}: {o.Help}"));
                    rows.Add(new UiRow
                    {
                        Label = cr.Label,
                        Value = sel < 0 ? "Off" : cr.OptionLabels[sel],
                        Help = help,
                        Step = (d, _) =>
                        {
                            int i = values.Selected(cr) + d;
                            if (i < -1) i = cr.Options.Count - 1;
                            if (i >= cr.Options.Count) i = -1;
                            values.Select(cr, i);
                            presetName = "";
                        },
                        Activate = () =>
                        {
                            int i = values.Selected(cr) + 1;
                            values.Select(cr, i >= cr.Options.Count ? -1 : i);
                            presetName = "";
                        },
                    });
                    if (sel >= 0 && cr.Options[sel].Nargs != 0)
                        rows.Add(FlagUi(cr.Options[sel], cr.Options[sel].Metavar.Length > 0 ? Pretty(string.Join(" / ", cr.Options[sel].Metavar)) : "Value", 1));
                }
            }
            return rows;
        }

        static string Pretty(string s) => s.Length == 0 ? s : s.Substring(0, 1).ToUpperInvariant() + s.Substring(1).ToLowerInvariant().Replace("_", " ");

        static UiRow FlagUi(FlagDef d, string label, int indent)
        {
            var help = d.Help;
            if (d.Min.HasValue && d.Max.HasValue) help += $"  ({FlagValues.Num(d, d.Min.Value)} to {FlagValues.Num(d, d.Max.Value)})";
            if (d.Choices != null && d.Choices.Length <= 20) help += "  Choices: " + string.Join(", ", d.Choices.Select(c => c == "" ? "(none)" : c));
            if (d.Text) help += "  A: type it.";
            if (d.Nargs == 2) help += "  A: switch between the two values.";
            help += $"  [{d.Flag}]";
            var value = values.Describe(d);
            if (d.Nargs == 2 && values.IsOn(d.Flag))
            {
                var v = values.Values(d.Flag);
                value = slot == 0 ? $"[{v[0]}] - {(v.Count > 1 ? v[1] : "?")}" : $"{v[0]} - [{(v.Count > 1 ? v[1] : "?")}]";
            }
            return new UiRow
            {
                Label = label,
                Value = value.Length > 48 ? value.Substring(0, 45) + "..." : value,
                Help = help,
                Indent = indent,
                Step = (dir, amount) =>
                {
                    if (d.Text) { if (dir < 0 && values.IsOn(d.Flag)) values.Off(d.Flag); else StartEdit(d.Flag, string.Join(" ", values.Values(d.Flag))); return; }
                    values.Step(d, slot, dir, amount);
                    presetName = "";
                },
                Activate = () =>
                {
                    presetName = "";
                    if (d.Text) { StartEdit(d.Flag, string.Join(" ", values.Values(d.Flag))); return; }
                    if (d.Nargs == 2 && values.IsOn(d.Flag)) { slot = 1 - slot; return; }
                    if (values.IsOn(d.Flag)) values.Off(d.Flag); else values.Set(d.Flag);
                },
            };
        }

        // headless check (WcTest): styles build, every page's rows build, and every flag row
        // steps both ways and toggles without errors; the flags are put back afterwards
        internal static string SelfTest()
        {
            var keep = values.Serialize();
            var keepPreset = presetName;
            int rowsSeen = 0, actions = 0;
            try
            {
                Styles();
                MainRows();
                for (int p = 1; p <= Defs.Sections.Count; p++)
                {
                    page = p;
                    for (int i = 0; i < 400; i++)
                    {
                        var rows = Rows();
                        if (i >= rows.Count) break;
                        var r = rows[i];
                        rowsSeen++;
                        if (r.Header) continue;
                        r.Step?.Invoke(+1, 1); r.Step?.Invoke(+1, 10); r.Step?.Invoke(-1, 1);
                        if (editing == null) r.Activate?.Invoke();
                        editing = null;
                        r.Step?.Invoke(-1, 1000);
                        editing = null;
                        actions += 5;
                    }
                }
                return $"{Defs.Sections.Count + 1} pages, {rowsSeen} rows, {actions} steps/toggles; flags after: {values.Count} on";
            }
            finally
            {
                page = 0; cursor = 0; scroll = 0; editing = null;
                values.Parse(keep);
                presetName = keepPreset;
            }
        }

        // --- text editing -----------------------------------------------------------------

        static void StartEdit(string what, string text)
        {
            editing = what;
            editBuffer = text ?? "";
        }

        static void CommitEdit()
        {
            var what = editing;
            editing = null;
            if (what == "seed") seedText = new string(editBuffer.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            else if (what == "flags") { values.Parse(editBuffer); presetName = ""; Say($"{values.Count} flags set."); }
            else if (Defs.Flags.TryGetValue(what, out var d)) { values.SetText(d, editBuffer); presetName = ""; }
        }

        static void EditKeys(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            var k = e.keyCode;
            if (k == KeyCode.Return || k == KeyCode.KeypadEnter) CommitEdit();
            else if (k == KeyCode.Escape) editing = null;
            else if (k == KeyCode.Backspace) { if (editBuffer.Length > 0) editBuffer = editBuffer.Substring(0, editBuffer.Length - 1); }
            else if (k == KeyCode.V && (e.control || e.command)) editBuffer += (GUIUtility.systemCopyBuffer ?? "").Replace("\r", " ").Replace("\n", " ");
            else if (k == KeyCode.Delete) editBuffer = "";
            else
            {
                char c = e.character;
                if (c >= ' ' && c != '\u007f' && !e.control) editBuffer += c;
                else return;
            }
            e.Use();
        }

        // --- drawing (OnGUI) --------------------------------------------------------------

        const float VW = 1280, VH = 720;
        static GUIStyle sBg, sPanel, sSel, sText, sValue, sDim, sTitle, sHelp, sPage, sPageSel, sPageDim, sHint, sClick, sHeader, sEdit;
        static GUIContent content;

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static float S = 1f, builtFor = -1f;  // pixels per virtual unit; scale the styles were built for

        static Rect R(float x, float y, float w, float h) => new Rect(x * S, y * S, w * S, h * S);
        static Rect R(Rect v) => R(v.x, v.y, v.width, v.height);

        static GUIStyle Style(int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false, Texture2D bg = null)
        {
            var s = new GUIStyle();
            if (font != null) s.font = font;
            s.fontSize = Math.Max(8, (int)Math.Round(size * S));
            s.normal.textColor = color;
            s.alignment = anchor;
            s.wordWrap = wrap;
            s.clipping = TextClipping.Clip;
            if (bg != null) s.normal.background = bg;
            return s;
        }

        // FF6-style window: vertical blue gradient, light rounded border, drawn 9-sliced
        static GUIStyle Window(Color top, Color bottom, Color edge, float edgeAlpha = 1f)
        {
            const int W = 32, H = 64, B = 8, Rad = 6;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.HideAndDontSave;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var c = Color.Lerp(bottom, top, y / (float)(H - 1));
                    // distance outside the rounded corner's circle
                    float cx = Math.Max(0, Math.Max(Rad - x, x - (W - 1 - Rad))), cy = Math.Max(0, Math.Max(Rad - y, y - (H - 1 - Rad)));
                    float d = (float)Math.Sqrt(cx * cx + cy * cy);
                    if (d > Rad + 0.5f) c = new Color(0, 0, 0, 0);
                    else if (d > Rad - 2.5f || x < 2 || y < 2 || x > W - 3 || y > H - 3)
                        c = new Color(edge.r, edge.g, edge.b, edge.a * edgeAlpha);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            var s = new GUIStyle();
            s.normal.background = t;
            s.border = new RectOffset(B, B, B, B);
            return s;
        }

        static Font font;
        static bool fontFound;

        // the game's own menu font (from any UI text on screen); until one is up, Unity's default
        static void FindFont()
        {
            if (fontFound) return;
            try
            {
                foreach (var t in Object.FindObjectsOfType<UnityEngine.UI.Text>())
                    if (t != null && t.font != null) { font = t.font; break; }
            }
            catch (Exception) { }
            if (font == null) return;
            fontFound = true;
            builtFor = -1f;  // rebuild the styles with it
            Plugin.L.LogInfo($"[wc screen] using the game's font '{font.name}'");
        }

        static void Styles()
        {
            if (sBg != null && Math.Abs(builtFor - S) < 0.01f) return;
            builtFor = S;
            leftValue = null;
            shadows.Clear();
            content = new GUIContent("");
            var edge = new Color(0.86f, 0.89f, 0.97f);
            sBg = Style(1, Color.white, bg: Solid(new Color(0, 0, 0, 0.55f)));
            sPanel = Window(new Color(0.16f, 0.24f, 0.62f, 0.97f), new Color(0.03f, 0.05f, 0.24f, 0.97f), edge);
            sSel = Window(new Color(0.45f, 0.58f, 0.98f, 0.85f), new Color(0.24f, 0.36f, 0.82f, 0.85f), edge, 0.7f);
            sSelDim = Window(new Color(0.3f, 0.38f, 0.7f, 0.45f), new Color(0.18f, 0.25f, 0.58f, 0.45f), edge, 0.25f);
            sText = Style(22, Color.white);
            sValue = Style(22, new Color(1f, 0.89f, 0.5f), TextAnchor.MiddleRight);
            sDim = Style(22, new Color(0.56f, 0.6f, 0.72f));
            sHeader = Style(19, new Color(0.62f, 0.82f, 1f));
            sTitle = Style(30, Color.white);
            sHelp = Style(18, new Color(0.88f, 0.9f, 0.97f), TextAnchor.UpperLeft, true);
            sPage = Style(17, new Color(0.8f, 0.83f, 0.92f));
            sPageDim = Style(17, new Color(0.5f, 0.53f, 0.64f));
            sPageSel = Style(17, Color.white);
            sHint = Style(17, Color.white);
            sClick = new GUIStyle();
            sEdit = Style(24, Color.white);
            sEditBox = Window(new Color(0.06f, 0.08f, 0.26f, 1f), new Color(0.02f, 0.03f, 0.14f, 1f), edge, 0.8f);
        }

        static GUIStyle sEditBox, sSelDim;
        static readonly Dictionary<GUIStyle, GUIStyle> shadows = new Dictionary<GUIStyle, GUIStyle>();

        // text with the game's drop shadow
        static void Shadowed(Rect r, string text, GUIStyle st)
        {
            if (!shadows.TryGetValue(st, out var sh))
            {
                sh = new GUIStyle();
                sh.font = st.font;
                sh.fontSize = st.fontSize;
                sh.alignment = st.alignment;
                sh.wordWrap = st.wordWrap;
                sh.clipping = st.clipping;
                sh.normal.textColor = new Color(0, 0, 0, 0.75f);
                shadows[st] = sh;
            }
            float o = Math.Max(1f, 2f * S);
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), C(text), sh);
            GUI.Label(r, C(text), st);
        }

        static GUIContent C(string text)
        {
            content.text = text;
            return content;
        }

        static void Label(float x, float y, float w, float h, string text, GUIStyle s) => Shadowed(R(x, y, w, h), text, s);
        static bool Click(float x, float y, float w, float h) => GUI.Button(R(x, y, w, h), C(""), sClick);

        // a line over the game for a few seconds (objective results)
        static string toast = "";
        static float toastUntil;

        internal static void Toast(string text)
        {
            toast = text;
            toastUntil = Time.unscaledTime + 6;
        }

        public static void Draw()
        {
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F7 && editing == null)
            {
                Show(!Open);
                e.Use();
                return;
            }
            S = Mathf.Min(Screen.width / VW, Screen.height / VH);
            if (S <= 0) return;
            if (Open) FindFont();
            Styles();
            float w = Screen.width / S, h = Screen.height / S;  // virtual size
            if (!Open && Time.unscaledTime < toastUntil && toast != "")
            {
                FindFont();
                GUI.Box(R((w - 900) / 2, 30, 900, 48), C(""), sPanel);
                Label((w - 900) / 2 + 20, 30, 860, 48, toast, Style2(sValue));
            }
            if (!Open) return;
            if (editing != null && e != null) EditKeys(e);
            GUI.Box(R(0, 0, w, h), C(""), sBg);
            float px = (w - 1250) / 2, py = (h - 700) / 2;
            GUI.Box(R(px, py, 1250, 700), C(""), sPanel);
            var mode = Track ? "Worlds Collide: Track" : Plugin.WcMode ? $"Worlds Collide {Defs.WcVersion}" : "Mods";
            Label(px + 20, py + 8, 800, 36, mode, sTitle);
            var status = WcGenerator.Running ? $"Generating: {WcGenerator.Status} ({WcGenerator.Seconds:0} s)"
                       : WcSeeds.Active != null ? $"Seed: {WcSeeds.Active.SeedText}  {WcSeeds.Active.Hash}" : "No seed";
            if (Plugin.WcMode) Label(px + 600, py + 8, 630, 36, status, Style2(sValue));

            // pages
            int pages = Track ? TrackPages.Length : Defs.Sections.Count + 1;
            float lx = px + 14, ly = py + 52, lh = Track ? 34 : Mathf.Min(21, 630f / pages);
            for (int i = 0; i < pages; i++)
            {
                var port = Track || i == 0 ? "yes" : Defs.Sections[i - 1].Port;
                var title = Track ? TrackPages[i] : (i == 0 ? "Seed" : Defs.Sections[i - 1].Title) + (port == "partly" ? " *" : "");
                if (i > 0 && !Plugin.WcMode) break;
                var r = new Rect(lx, ly + i * lh, 236, lh);
                if (i == page) GUI.Box(R(r), C(""), onPages ? sSel : sSelDim);
                int on = Track || i == 0 ? 0 : CountOn(Defs.Sections[i - 1]);
                Shadowed(R(r.x + 10, r.y, r.width - 12, r.height), title + (on > 0 ? $"  ({on})" : ""), i == page ? sPageSel : port == "no" ? sPageDim : sPage);
                if (GUI.Button(R(r), C(""), sClick)) { page = i; cursor = 0; scroll = 0; slot = 0; onPages = false; }
            }

            // rows
            var rows = Rows();
            if (cursor >= rows.Count) cursor = Math.Max(0, rows.Count - 1);
            if (rows.Count > 0 && rows[cursor].Header) Move(rows, +1);
            float rx = px + 266, ry = py + 52, rw = 970, rh = 34;
            int visible = 15;
            if (cursor < scroll) scroll = cursor;
            if (cursor >= scroll + visible) scroll = cursor - visible + 1;
            if (e != null && e.type == EventType.ScrollWheel)
            {
                scroll = Mathf.Clamp(scroll + (e.delta.y > 0 ? 3 : -3), 0, Math.Max(0, rows.Count - visible));
                cursor = Mathf.Clamp(cursor, scroll, Math.Min(rows.Count - 1, scroll + visible - 1));
                e.Use();
            }
            for (int i = scroll; i < rows.Count && i < scroll + visible; i++)
            {
                var row = rows[i];
                float y = ry + (i - scroll) * rh;
                var rect = new Rect(rx, y, rw, rh - 2);
                if (i == cursor) GUI.Box(R(rect), C(""), onPages ? sSelDim : sSel);
                float ix = rx + 12 + row.Indent * 28;
                if (row.Header) { Label(ix, y, rw - 24, rh, row.Label, sHeader); continue; }
                Label(ix, y, 560 - row.Indent * 28, rh, row.Label, row.Disabled ? sDim : sText);
                if (row.Value != "") Label(rx + 420, y, rw - 432, rh, row.Value, row.Disabled ? sDim : sValue);
                if (Click(rect.x, rect.y, rect.width, rect.height))
                {
                    if (cursor == i && !row.Disabled)
                    {
                        // tap the right end of the value to step, elsewhere to activate
                        float mx = (e?.mousePosition.x ?? 0) / S;  // back to virtual units
                        if (row.Step != null && mx > rx + rw - 120) row.Step(+1, 1);
                        else if (row.Step != null && mx > rx + 420 && mx < rx + 540) row.Step(-1, 1);
                        else row.Activate?.Invoke();
                    }
                    cursor = i;
                    slot = 0;
                }
            }
            if (rows.Count > visible)
                Label(rx, ry + visible * rh, rw, 18, $"{scroll + 1}-{Math.Min(rows.Count, scroll + visible)} of {rows.Count}", sPage);

            // help + keys + notes
            float hy = py + 588;
            var help = cursor < rows.Count ? rows[cursor].Help : "";
            var secPort = Track || page == 0 ? "yes" : Defs.Sections[page - 1].Port;
            if (secPort == "no") help = "Not in the Pixel Remaster port yet (WC events/assembly): kept in the seed's flags, no effect in this game. " + help;
            else if (secPort == "partly") help = "Partly in the Pixel Remaster port (data yes, WC events/assembly not yet). " + help;
            else if (secPort == "pr") help = "The Pixel Remaster already fixes most of these SNES bugs itself. " + help;
            Label(rx, hy, rw, 72, help, sHelp);
            var keys = Track ? (onPages ? "Up/Down: choose a page   A / Right: go to it   L1/R1: pages   B: close"
                                       : "Up/Down: scroll   L1/R1: pages   B: page list")
                     : onPages ? "Up/Down: choose a page   A / Right: go to its options   L1/R1: pages   B: close"
                     : page == 0 ? "Up/Down: choose   Left/Right: change   A: select   B: page list   L1/R1: pages"
                                 : "Up/Down: choose   Left/Right: change   A: toggle / edit   B: page list   L1/R1: pages";
            Label(rx, py + 668, rw, 24, Time.unscaledTime < noteUntil ? note : keys, Time.unscaledTime < noteUntil ? Style2(sValue) : sPage);

            if (editing != null) DrawEditor(w, h);
        }

        static GUIStyle leftValue;
        static GUIStyle Style2(GUIStyle _) => leftValue ??= Style(18, new Color(1f, 0.88f, 0.45f), TextAnchor.MiddleLeft);

        static int CountOn(Section s) =>
            s.Rows.Sum(r => r is FlagRow f ? (values.IsOn(f.Flag.Flag) ? 1 : 0) : ((ChoiceRow)r).Options.Count(o => values.IsOn(o.Flag)));

        static void DrawEditor(float w, float h)
        {
            float ex = (w - 1000) / 2, ey = (h - 170) / 2;
            GUI.Box(R(ex - 6, ey - 6, 1012, 182), C(""), sSel);
            GUI.Box(R(ex, ey, 1000, 170), C(""), sPanel);
            var what = editing == "seed" ? "Seed" : editing == "flags" ? "Flags" : Defs.Flags.TryGetValue(editing, out var d) ? $"{d.Label} ({d.Flag})" : editing;
            Label(ex + 16, ey + 8, 960, 30, what, sHeader);
            var shown = editBuffer;
            if (shown.Length > 80) shown = "..." + shown.Substring(shown.Length - 77);
            bool caret = ((int)(Time.unscaledTime * 2)) % 2 == 0;
            GUI.Box(R(ex + 16, ey + 46, 968, 44), C(""), sEditBox);
            Label(ex + 26, ey + 46, 948, 44, shown + (caret ? "|" : ""), sEdit);
            Label(ex + 16, ey + 100, 968, 26, "Enter / A: OK    Esc / B: cancel    Ctrl+V: paste    Delete: clear", sPage);
            Label(ex + 16, ey + 128, 968, 26, "Steam Deck: Steam + X opens the on-screen keyboard.", sPage);
        }
    }
}
