"""Worlds Collide without the SNES ROM's content: stand-ins for what WC only rewrites.

The ROM-free generator (PORTING.md "WC without an SNES ROM"). WC reads its input ROM for two
kinds of things (tools/wc_trace.py measures it):
  - data it randomizes with its random draws: characters, items, espers, spells, shops, chests,
    enemies, formations, packs, zones, skills, the Coliseum, natural magic, map properties
    (enemies.py asks each map whether it has random battles)
  - content it only rewrites, with no random draws: character sprites, portraits and palettes,
    dialogue, maps' NPCs, events and exits, font widths, game code it moves
(The enemy AI scripts are in the shipped data, tools/wc_basedata.py: WC's AI changes run as
they are and our AI port compiles them.)
These patches replace the second kind with stand-ins that need no ROM bytes, so WC makes the
same draws and the same data from the first kind alone. What WC writes for the second kind is
SNES content our port never reads (it ports WC's choices: tools/wc_run.py's dumps).

Used by tools/wc_run.py when WC_ROMFREE=1 (patches(), applied as each WC module loads).
"""
import random


class Sink:
    """Accepts any call or attribute set; reads give a no-op function."""

    def __getattr__(self, name):
        return lambda *a, **k: None


def patch_valid_rom_file(module):
    # WC only takes the vanilla ROM (its SHA-256): the ROM-free input isn't that file
    module.valid_rom_file = lambda path: True


OUT_MAGIC = b"FF6WCO1\0"   # = ff6snes.OUT_MAGIC


def patch_rom(module):
    """WC's input: the shipped data (tools/wc_basedata.py, WC_BASE or data/wc_base.bin) in
    memory, never a ROM file; its output: WC's working bytes, compressed (ff6snes.rom_bytes
    reads them for the port), not an .sfc. The generator deletes it with its work folder."""
    import os
    import zlib
    import wc_basedata
    R = module.ROM

    def __init__(self, file_name):
        self.data = list(wc_basedata.load(os.environ.get("WC_BASE") or wc_basedata.PATH))
        self.expand()

    def write(self, file_name):
        with open(file_name, "wb") as f:
            f.write(OUT_MAGIC + zlib.compress(bytes(self.data), 6))

    R.__init__, R.write = __init__, write


def patch_dialogs(module):
    """Dialogue: every table's entries, empty (WC sets its own texts by id); nothing written."""
    D = module.Dialogs
    from data.dialogs.dialog import Dialog
    import data.text

    def entries(ptrs_start, ptrs_end, text_type):
        return [Dialog(i, text_type, []) for i in range((ptrs_end - ptrs_start + 1) // 2)]

    def read_dialogs(self):
        self.dialogs = entries(self.DIALOG_PTRS_START, self.DIALOG_PTRS_END, data.text.TEXT1)

    def read_battle_messages(self):
        self.battle_messages = entries(self.BATTLE_MESSAGE_PTRS_START, self.BATTLE_MESSAGE_PTRS_END, data.text.TEXT3)

    def read_single(self):
        self.single_line_battle_dialogs = entries(self.SINGLE_LINE_BATTLE_DIALOG_PTRS_START,
                                                  self.SINGLE_LINE_BATTLE_DIALOG_PTRS_END, data.text.TEXT3)

    def read_multi(self):
        self.multi_line_battle_dialogs = entries(self.MULTI_LINE_BATTLE_DIALOG_PTRS_START,
                                                 self.MULTI_LINE_BATTLE_DIALOG_PTRS_END, data.text.TEXT3)

    def move_battle_messages(self):
        # WC's space bookkeeping as it does it (later allocations land the same), without the
        # old messages' pointer table
        from memory.space import START_ADDRESS_SNES, Bank, Reserve, Allocate, Free
        space = Allocate(Bank.F0, 4000, "battle messages new location")
        bank = Reserve(0x198ff, 0x198ff, "battle messages bank")
        bank.write(START_ADDRESS_SNES + space.start_address >> 16)
        Free(0x11f000, 0x11f79f)

    D.read_dialogs, D.read_battle_messages = read_dialogs, read_battle_messages
    D.read_single_line_battle_dialogs, D.read_multi_line_battle_dialogs = read_single, read_multi
    D.move_battle_messages = move_battle_messages
    D.write = lambda self: None


def patch_widths(module):
    """Font widths (text centering only): one width for every glyph."""
    module.Widths.__init__ = lambda self: setattr(self, "widths", [[8]] * 256)


def patch_maps(module):
    """Maps: chests and map properties (data); NPCs, events, exits and the world map as stand-ins."""
    M = module.Maps
    from data.chests import Chests
    from data.map_property import MapProperty
    from data.npc import NPC
    from data.map_event import MapEvent

    def __init__(self, rom, args, items):
        self.rom, self.args = rom, args
        self.chests = Chests(rom, args, items)
        self.npcs = Sink()
        self.events = Sink()
        self.long_events = Sink()
        self.exits = Sink()
        self.world_map_event_modifications = Sink()
        self.world_map = Sink()
        self._npcs, self._added, self._entrance = {}, {}, {}
        self.read()

    def read(self):
        self.maps, self.properties = [], []
        for map_index in range(self.MAP_COUNT):
            prop = MapProperty(self.rom, map_index)
            self.properties.append(prop)
            self.maps.append({"id": map_index, "name_index": prop.name_index})

    def get_npc(self, map_id, npc_id):
        key = (map_id, npc_id)
        if key not in self._npcs:
            npc = NPC()
            npc.from_data([0] * NPC.DATA_SIZE)
            self._npcs[key] = npc
        return self._npcs[key]

    def append_npc(self, map_id, new_npc):
        n = self._added.get(map_id, 0)
        self._added[map_id] = n + 1
        self._npcs[(map_id, 0x40 + n)] = new_npc
        return 0x40 + n

    def get_event(self, map_id, x, y):
        event = MapEvent()
        event.from_data([x, y, 0, 0, 0])
        return event

    M.__init__, M.read, M.get_npc, M.append_npc, M.get_event = __init__, read, get_npc, append_npc, get_event
    for name in ("remove_npc", "add_event", "delete_event", "add_long_event", "delete_long_event",
                 "delete_short_exit", "_fix_imperial_camp_boxes", "_fix_Cid_timer_glitch"):
        setattr(M, name, lambda self, *a, **k: None)
    M.get_npc_count = lambda self, map_id: self._added.get(map_id, 0)
    M.set_entrance_event = lambda self, map_id, address: self._entrance.__setitem__(map_id, address)
    M.get_entrance_event = lambda self, map_id: self._entrance.get(map_id, 0)
    original_mod = M.mod

    def mod(self, characters):
        self.chests.mod()
        if self.args.no_saves:
            self._disable_saves()

    M.mod = mod
    def write(self):
        self.chests.write()
        for prop in self.properties:      # map properties are data the port reads (random battles)
            prop.write()

    M.write = write
    M._original_mod = original_mod


def patch_party_battle_scripts(module):
    """Scripted party battles (the Imperial Camp's Cyan...): edits only, no draws."""
    P = module.PartyBattleScripts
    P.__init__ = lambda self, *a, **k: None
    P.mod = lambda self, *a, **k: None
    P.write = lambda self: None
    P.__getattr__ = lambda self, name: (lambda *a, **k: None)


def patch_animations(module):
    """Battle animation flashes (-frw/-frm): edits of animation scripts it checks byte by byte."""
    module.Animations.replace_bg_flash_with_monster_flash_mod = lambda self, *a: None
    module.Animations.remove_battle_flashes_mod = lambda self, *a: None


def patch_menu_sprites(module):
    """The menu's character sprites (graphics): nothing written."""
    module.MenuCharacterSprites.write = lambda self: None


def patches(rom=True):
    """rom=False: the stand-ins only, WC still reading a real ROM file (the parity experiments)."""
    return ({"memory.rom": patch_rom} if rom else {}) | {
        "battle.animations": patch_animations,
        "data.menu_character_sprites": patch_menu_sprites,
        "valid_rom_file": patch_valid_rom_file,
        "data.dialogs.dialogs": patch_dialogs,
        "data.fonts.widths": patch_widths,
        "data.maps": patch_maps,
        "data.party_battle_scripts": patch_party_battle_scripts,
    }
