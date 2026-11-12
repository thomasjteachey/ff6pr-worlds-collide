"""Run Worlds Collide's own wc.py main() unchanged and also dump what its events chose.

  (cwd = the WC folder) python tools/wc_run.py DUMP.json <wc.py arguments...>

Same process, same code path and call order as `python wc.py ...` (the seed's ROM is
identical); this wrapper only records Events.mod()'s result once WC has made it: every event's
name, character gate and rewards (SNES ids: character 0-13, esper 0-26, item 0-255), plus the
start party. wc_seed.py turns that into PR ids for the plugin (start party, check rewards).
"""
import json
import os
import sys


# more patches by module name, applied as the module loads (tools/wc_trace.py sets them)
EXTRA_PATCHES = {}


def read_output(path):
    """WC's output: the ROM file, or a ROM-free run's compressed bytes (tools/wc_romfree.py)."""
    with open(path, "rb") as f:
        data = f.read()
    if data.startswith(b"FF6WCO1\0"):
        import zlib
        data = zlib.decompress(data[8:])
    return data


def main():
    dump = sys.argv[1]
    sys.argv = ["wc.py"] + sys.argv[2:]
    sys.path.insert(0, os.getcwd())

    captured = []
    packs = {}

    def patch_events(module):
        original = module.Events.mod

        def mod(self):
            result = original(self)
            captured.extend(result)
            return result

        module.Events.mod = mod

    def patch_packs(module):
        # WC's enemy packs: the vanilla formations (read at load) and the instance, to see at the
        # end which packs its events' boss battles now call and what its fixed encounters became
        original = module.EnemyPacks.__init__

        def init(self, *a, **k):
            original(self, *a, **k)
            packs["self"] = self
            packs["vanilla"] = [list(p.formations) for p in self.packs]

        module.EnemyPacks.__init__ = init

    start_dances = {}

    def patch_dances(module):
        # WC writes its start dances as code (LDA #bits): keep the bits it chose
        original = module.Dances.set_start_dances

        def set_start_dances(self, bits):
            start_dances["bits"] = int(bits)
            return original(self, bits)

        module.Dances.set_start_dances = set_start_dances

    espers = {}

    def patch_espers(module):
        original = module.Espers.__init__

        def init(self, *a, **k):
            original(self, *a, **k)
            espers["self"] = self

        module.Espers.__init__ = init

    items_obj = {}

    def patch_items(module):
        original = module.Items.__init__

        def init(self, *a, **k):
            original(self, *a, **k)
            items_obj["self"] = self

        module.Items.__init__ = init

    chests_obj = {}

    def patch_chests(module):
        original = module.Chests.__init__

        def init(self, *a, **k):
            original(self, *a, **k)
            chests_obj["self"] = self

        module.Chests.__init__ = init

    # WC's random Zozo clock (-rc, always on: event/zozo.py randomize_clock_mod): its time and the
    # ten clue dialogs it rewrites (data/dialogs set_text)
    clock = {"texts": {}}

    def patch_zozo(module):
        original = module.Zozo.randomize_clock_mod

        def randomize(self, *a, **k):
            clock["recording"] = True
            try:
                clock["time"] = original(self, *a, **k)
            finally:
                clock["recording"] = False
            return clock["time"]

        module.Zozo.randomize_clock_mod = randomize

    def patch_dialogs(module):
        original = module.Dialogs.set_text

        def set_text(self, id, text):
            if clock.get("recording"):
                clock["texts"][int(id)] = text
            return original(self, id, text)

        module.Dialogs.set_text = set_text

    # the map NPCs WC's events re-skin (a check's NPC shown as its reward: the reward character,
    # or for an esper/item a soldier, imp, merchant or ghost, WC's own random pick): every
    # sprite/palette set on an NPC its events asked the maps for, by the event module that set it
    npc_sets, npc_added = {}, []

    def event_module():
        f = sys._getframe(2)
        while f is not None:
            fn = f.f_code.co_filename.replace("\\", "/")
            if "/event/" in fn:
                return os.path.splitext(os.path.basename(fn))[0]
            f = f.f_back
        return "?"

    def patch_npc(module):
        def setattr_(self, name, value):
            object.__setattr__(self, name, value)
            key = self.__dict__.get("_wc_key")
            if key is not None and name in ("sprite", "palette"):
                rec = npc_sets.setdefault(key, {"module": event_module()})
                rec[name] = int(value)

        module.NPC.__setattr__ = setattr_

    def patch_maps(module):
        get, append = module.Maps.get_npc, module.Maps.append_npc

        def get_npc(self, map_id, npc_id):
            npc = get(self, map_id, npc_id)
            object.__setattr__(npc, "_wc_key", (int(map_id), int(npc_id)))
            return npc

        def append_npc(self, map_id, new_npc):
            npc_id = append(self, map_id, new_npc)
            npc_added.append({"map": int(map_id), "npc": int(npc_id), "sprite": int(new_npc.sprite),
                              "palette": int(new_npc.palette), "module": event_module()})
            return npc_id

        module.Maps.get_npc, module.Maps.append_npc = get_npc, append_npc

    patches = {"event.events": patch_events, "data.enemy_packs": patch_packs, "data.dances": patch_dances,
               "data.espers": patch_espers, "data.items": patch_items, "data.chests": patch_chests,
               "event.zozo": patch_zozo, "data.dialogs.dialogs": patch_dialogs,
               "data.npc": patch_npc, "data.maps": patch_maps}
    def chain(first, then):
        return lambda module: (first(module), then(module) if then else None)
    mode = os.environ.get("WC_ROMFREE", "")
    if mode in ("1", "stub"):
        # the ROM-free generator (tools/wc_romfree.py): WC's input from the shipped data, stand-ins
        # for the content WC only rewrites; "stub": the stand-ins on a real ROM (experiments)
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import wc_romfree
        for name, fn in wc_romfree.patches(rom=mode == "1").items():
            patches[name] = chain(fn, patches.get(name))
    for name, fn in EXTRA_PATCHES.items():          # tools/wc_trace.py's
        patches[name] = chain(fn, patches.get(name))

    # WC claims ROM space as its modules are imported (after Memory() exists), so event.events
    # must load when wc.main() imports it: patch it from an import hook, not by importing it here
    import importlib.abc
    import importlib.util

    class Hook(importlib.abc.MetaPathFinder):
        busy = False

        def find_spec(self, name, path, target=None):
            if name not in patches or Hook.busy:
                return None
            patch = patches.pop(name)
            if not patches:
                sys.meta_path.remove(self)
            Hook.busy = True
            try:
                spec = importlib.util.find_spec(name)
            finally:
                Hook.busy = False
            run = spec.loader.exec_module

            def exec_module(module):
                run(module)
                patch(module)

            spec.loader.exec_module = exec_module
            return spec

    sys.meta_path.insert(0, Hook())

    import wc
    wc.main()

    from event.event_reward import RewardType
    kinds = {RewardType.CHARACTER: "character", RewardType.ESPER: "esper", RewardType.ITEM: "item"}
    out = []
    for e in captured:
        rewards = []
        for r in e.rewards:
            # the attribute holding it (reward1, reward2...): the Auction House adds reward2 first
            slot = next((k for k, v in vars(e).items() if v is r), None)
            rewards.append({"type": kinds.get(r.type, str(r.type)), "id": r.id, "slot": slot})
        gate = e.character_gate()
        out.append({
            "event": e.name(),
            "module": type(e).__module__.split(".")[-1],
            "gate": None if gate is None else int(gate),
            "rewards": rewards,
            "log": list(e.rewards_log),
        })
    with open(dump, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1)
    with open(os.path.splitext(dump)[0] + "_npcs.json", "w", encoding="utf-8") as f:
        json.dump({"set": [dict(v, map=k[0], npc=k[1]) for k, v in sorted(npc_sets.items())],
                   "added": npc_added}, f, indent=1)

    # WC's random start skills (-srr / -slr / -sdr): rage ids, lore indexes, dance indexes
    try:
        import args as wc_args
        out_rom = sys.argv[sys.argv.index("-o") + 1]
        data = read_output(out_rom)
        if len(data) % 0x8000 == 0x200:
            data = data[0x200:]

        def bits(at, count):
            return [i for i in range(count) if data[at + i // 8] >> (i % 8) & 1]

        skills = {}
        if getattr(wc_args, "start_rages_random", False):
            skills["rages"] = bits(0x47AA0, 255)
        if getattr(wc_args, "start_lores_random", False):
            skills["lores"] = bits(0x26F564, 24)
        if getattr(wc_args, "start_dances_random", False) and "bits" in start_dances:
            skills["dances"] = [i for i in range(8) if start_dances["bits"] >> i & 1]
        if skills:
            with open(os.path.splitext(dump)[0] + "_skills.json", "w", encoding="utf-8") as f:
                json.dump(skills, f)
        # -ccrs (chest contents scaled by chests opened): the n-th item / gil chest opened gives
        # WC's n-th (data/chests.py random_scaled, chests_asm.scale_items / scale_gold)
        ch = chests_obj.get("self")
        if getattr(wc_args, "chest_contents_random_scaled", False) and ch is not None and getattr(ch, "item_contents", None):
            with open(os.path.splitext(dump)[0] + "_ccrs.json", "w", encoding="utf-8") as f:
                json.dump({"items": [int(x) for x in ch.item_contents], "gold": [int(x) for x in getattr(ch, "gold_contents", [])]}, f)
        if clock.get("time"):
            with open(os.path.splitext(dump)[0] + "_clock.json", "w", encoding="utf-8") as f:
                json.dump({"time": clock["time"], "texts": clock["texts"]}, f)
        # the cursed shield's battles to uncurse (WC: 256 unless -csb picked another)
        n = getattr(items_obj.get("self"), "cursed_shield_battles", None)
        if n:
            with open(os.path.splitext(dump)[0] + "_csb.json", "w", encoding="utf-8") as f:
                json.dump(int(n), f)
        # WC's esper restrictions (-eer / -eebr): who may equip each esper (character bits)
        if (getattr(wc_args, "esper_equipable_random", False) or getattr(wc_args, "esper_equipable_balanced_random", False)) \
                and "self" in espers:
            with open(os.path.splitext(dump)[0] + "_esper_equip.json", "w", encoding="utf-8") as f:
                json.dump([int(e.equipable_characters) for e in espers["self"].espers], f)
    except Exception as e:  # noqa: BLE001 - the PR's own start skills
        print(f"skills dump: {e}", file=sys.stderr)

    # WC's boss / dragon / statue shuffles and random fixed encounters: event pack -> the pack its
    # event now calls (boss shuffles), and every pack's formations before and after (fixed ones)
    try:
        p = packs.get("self")
        if p is not None:
            # WC's battle quests (condition_bits.quest_bit): the event's own pack, whatever WC put
            # there (its bit is set by the event, not by the boss)
            import data.bosses as wc_bosses
            quests = {"Defeat Sealed Cave Ninja": 405}
            for name, boss in (("Defeat KT Ambusher", "Inferno"), ("Defeat KT Robot", "Guardian"), ("Defeat KT Left Statue", "Doom"),
                               ("Defeat KT Mid Statue", "Poltrgeist"), ("Defeat KT Right Statue", "Goddess")):
                if boss in wc_bosses.name_pack:
                    quests[name] = wc_bosses.name_pack[boss]
            with open(os.path.splitext(dump)[0] + "_packs.json", "w", encoding="utf-8") as f:
                json.dump({"quests": quests,
                           "replacements": {str(int(k)): int(v) for k, v in getattr(p, "event_boss_replacements", {}).items()},
                           "fixed": [int(x) for x in getattr(p, "fixed", [])],
                           "vanilla": packs["vanilla"],
                           "final": [list(map(int, q.formations)) for q in p.packs]}, f)
    except Exception as e:  # noqa: BLE001 - the seed plays with vanilla bosses
        print(f"packs dump: {e}", file=sys.stderr)

    # WC's objectives (the Track menu's Objectives page, the plugin's objective checks): each one's
    # result, how many conditions it needs and the conditions (names as WC's condition_bits list them)
    try:
        import objectives
        from constants.objectives import condition_bits as B
        names = {"Check": B.check_bit, "Dragon": B.dragon_bit, "Boss": B.boss_bit, "Quest": B.quest_bit}
        def items_of(result):
            # the items a field result gives (its src, as WC writes it: AddItem / AddItems), SNES ids
            out = []
            try:
                obj = object.__new__(result.field_class)
                src = result.field_class.src(obj, *result.args)
            except Exception:  # noqa: BLE001 - results that aren't item gifts
                return []

            def walk(x):
                if isinstance(x, (list, tuple)):
                    for y in x:
                        walk(y)
                elif type(x).__name__ == "AddItem":
                    out.append([int(x.item), 1])
                elif type(x).__name__ == "AddItems":
                    out.append([int(x.item), int(x.args[0]) if x.args and x.args[0] != 0x80 else 1])
            walk(src)
            return out

        objs = []
        for o in objectives:
            conds = []
            for c in o.conditions:
                d = {"type": c.NAME, "text": str(c)}
                for attr in ("character", "esper", "count"):
                    if hasattr(c, attr):
                        d[attr] = int(getattr(c, attr))
                for attr in ("check", "dragon", "boss", "quest"):
                    if hasattr(c, attr):
                        d["name"] = names[c.NAME][getattr(c, attr)].name
                conds.append(d)
            objs.append({"letter": o.letter, "result": str(o.result), "result_name": o.result.NAME,
                         "result_args": [a if isinstance(a, (int, float, str)) else str(a) for a in o.result.args],
                         "items": items_of(o.result),
                         "required": int(o.conditions_required), "conditions": conds})
        with open(os.path.splitext(dump)[0] + "_objectives.json", "w", encoding="utf-8") as f:
            json.dump(objs, f, indent=1)
        # WC's bosses for objectives ("Defeat N Bosses", "Defeat <boss>"): names as its conditions
        # word them, SNES formation ids (PR monster party = formation + 1)
        # WC's "Learn / Forget Spells" random spell orders (written into its ROM), SNES spell ids
        for mod, attr, suffix in (("objectives.results.learn_spells", "random_learn_spell_table", "_spells.json"),
                                  ("objectives.results.forget_spells", "random_forget_spell_table", "_forget.json"),
                                  ("objectives.results.learn_rages", "random_rage_table", "_rages.json")):
            m = sys.modules.get(mod)
            if m is None or getattr(m, attr, None) is None:
                continue
            out_rom = sys.argv[sys.argv.index("-o") + 1]
            data = read_output(out_rom)
            if len(data) % 0x8000 == 0x200:
                data = data[0x200:]
            at, size = getattr(m, attr), getattr(m, attr + "_size")
            with open(os.path.splitext(dump)[0] + suffix, "w", encoding="utf-8") as f:
                json.dump(list(data[at:at + size]), f)
        import args as wc_args
        factor = getattr(wc_args, "ability_scaling_element", None) or getattr(wc_args, "ability_scaling_random", None)
        if factor:
            import data.enemy_ability_tiers as T
            cats = ("fire", "ice", "bolt", "earth", "wind", "water", "poison", "pearl", "non_elemental", "cure", "tek", "damage")
            with open(os.path.splitext(dump)[0] + "_tiers.json", "w", encoding="utf-8") as f:
                json.dump({"factor": float(factor), "random": bool(getattr(wc_args, "ability_scaling_random", None)),
                           "tiers": {c: [[int(x) for x in tier] for tier in getattr(T, c)] for c in cats if hasattr(T, c)}}, f)
        from constants.objectives.boss_ids import boss_objective_ids
        from data.bosses import normal_formation_name
        with open(os.path.splitext(dump)[0] + "_bosses.json", "w", encoding="utf-8") as f:
            json.dump([{"name": normal_formation_name[b], "formation": int(b)} for b in boss_objective_ids], f, indent=1)
    except Exception as e:  # noqa: BLE001 - the seed plays without the Track menu's objectives
        print(f"objectives dump: {e}", file=sys.stderr)


if __name__ == "__main__":
    main()
