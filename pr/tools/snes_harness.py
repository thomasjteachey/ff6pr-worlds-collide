"""A windowless SNES for tests: a libretro core driven in-process from Python.

No window, no sound, no input devices: the game only gets the buttons this script presses, so it can
run while the user is at the computer (Mesen 2's test runner took input from the user's game,
2026-11-11: "don't hijack input from my wow rn. i'm playing. you can make some other harness to
control the emulator"). Work RAM is read and written directly; any frame can be saved as a PNG.
Nothing else is written (no SRAM, no states), and the ROM it's given is the caller's to delete.

The core: FF6_LIBRETRO_CORE, else RetroArch's Snes9x core from Steam. (RetroArch's bsnes cores, 115
and HD beta, give no way to work RAM: no SYSTEM_RAM and no memory map.) The process runs at below
normal priority so the user's own programs come first.

    snes = Snes(rom_path)
    snes.press("a"); snes.run(2); snes.release()
    snes.ram[0x1F64]          # WRAM $7E:1F64
    snes.screenshot("x.png")  # the last frame
"""
import ctypes
import os
import tempfile

CORE = os.environ.get("FF6_LIBRETRO_CORE") or \
    r"C:\Program Files (x86)\Steam\steamapps\common\RetroArch\cores\snes9x_libretro.dll"

BUTTONS = {"b": 0, "y": 1, "select": 2, "start": 3, "up": 4, "down": 5, "left": 6, "right": 7,
           "a": 8, "x": 9, "l": 10, "r": 11}
RETRO_DEVICE_JOYPAD = 1
RETRO_MEMORY_SYSTEM_RAM = 2

ENV_CB = ctypes.CFUNCTYPE(ctypes.c_bool, ctypes.c_uint, ctypes.c_void_p)
VIDEO_CB = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_size_t)
AUDIO_CB = ctypes.CFUNCTYPE(None, ctypes.c_int16, ctypes.c_int16)
AUDIO_BATCH_CB = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t)
POLL_CB = ctypes.CFUNCTYPE(None)
STATE_CB = ctypes.CFUNCTYPE(ctypes.c_int16, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint)


class GameInfo(ctypes.Structure):
    _fields_ = [("path", ctypes.c_char_p), ("data", ctypes.c_void_p), ("size", ctypes.c_size_t), ("meta", ctypes.c_char_p)]


class MemoryDescriptor(ctypes.Structure):
    _fields_ = [("flags", ctypes.c_uint64), ("ptr", ctypes.c_void_p), ("offset", ctypes.c_size_t), ("start", ctypes.c_size_t),
                ("select", ctypes.c_size_t), ("disconnect", ctypes.c_size_t), ("len", ctypes.c_size_t), ("addrspace", ctypes.c_char_p)]


class MemoryMap(ctypes.Structure):
    _fields_ = [("descriptors", ctypes.POINTER(MemoryDescriptor)), ("num_descriptors", ctypes.c_uint)]


class SystemInfo(ctypes.Structure):
    _fields_ = [("library_name", ctypes.c_char_p), ("library_version", ctypes.c_char_p),
                ("valid_extensions", ctypes.c_char_p), ("need_fullpath", ctypes.c_bool), ("block_extract", ctypes.c_bool)]


class Snes:
    def __init__(self, rom_path, core=CORE):
        if not os.path.exists(core):
            raise FileNotFoundError(f"no libretro SNES core at {core} (FF6_LIBRETRO_CORE)")
        if os.name == "nt":                # below normal priority: the user's programs come first
            k32 = ctypes.windll.kernel32
            k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)
        self.lib = ctypes.CDLL(core)
        self.frame = 0
        self.held = set()
        self.pixel_format = 0
        self._want_frame = False
        self.last_frame = None          # (bytes, width, height, pitch, pixel format)
        self.track_black = False        # True: self.black says whether each frame is all black (rows sampled)
        self.black = None
        self._wram = None               # (pointer, size) from the core's memory map (bsnes has no SYSTEM_RAM)
        self._dir = tempfile.mkdtemp(prefix="snes_harness_")
        self._dir_c = ctypes.c_char_p(self._dir.encode())
        # the callbacks must outlive the core's use of them
        self._cbs = (ENV_CB(self._env), VIDEO_CB(self._video), AUDIO_CB(lambda l, r: None),
                     AUDIO_BATCH_CB(lambda d, n: n), POLL_CB(lambda: None), STATE_CB(self._state))
        L = self.lib
        L.retro_set_environment(self._cbs[0])
        L.retro_set_video_refresh(self._cbs[1])
        L.retro_set_audio_sample(self._cbs[2])
        L.retro_set_audio_sample_batch(self._cbs[3])
        L.retro_set_input_poll(self._cbs[4])
        L.retro_set_input_state(self._cbs[5])
        L.retro_init()
        info = SystemInfo()
        L.retro_get_system_info(ctypes.byref(info))
        self.core_name = f"{(info.library_name or b'').decode()} {(info.library_version or b'').decode()}"
        data = open(rom_path, "rb").read()
        self._rom = ctypes.create_string_buffer(data, len(data))
        game = GameInfo(rom_path.encode(), ctypes.cast(self._rom, ctypes.c_void_p), len(data), None)
        L.retro_load_game.restype = ctypes.c_bool
        if not L.retro_load_game(ctypes.byref(game)):
            raise RuntimeError(f"{self.core_name} didn't load {rom_path}")
        L.retro_get_memory_data.restype = ctypes.c_void_p
        L.retro_get_memory_size.restype = ctypes.c_size_t
        size = L.retro_get_memory_size(RETRO_MEMORY_SYSTEM_RAM)
        addr = L.retro_get_memory_data(RETRO_MEMORY_SYSTEM_RAM)
        if (not addr or size < 0x20000) and self._wram:
            addr, size = self._wram
        if not addr or size < 0x20000:
            raise RuntimeError(f"{self.core_name}: no work RAM ({size} bytes)")
        self.ram = (ctypes.c_uint8 * size).from_address(addr)   # WRAM $7E:0000-$7F:FFFF, writable

    # --- libretro callbacks -------------------------------------------------------------------------
    def _env(self, cmd, data):
        cmd &= 0xFFFF
        if cmd == 3:                       # GET_CAN_DUPE
            ctypes.cast(data, ctypes.POINTER(ctypes.c_bool))[0] = True
            return True
        if cmd == 10:                      # SET_PIXEL_FORMAT
            self.pixel_format = ctypes.cast(data, ctypes.POINTER(ctypes.c_uint))[0]
            return self.pixel_format in (0, 1, 2)
        if cmd in (9, 31):                 # GET_SYSTEM_DIRECTORY / GET_SAVE_DIRECTORY: a temp folder
            ctypes.cast(data, ctypes.POINTER(ctypes.c_char_p))[0] = self._dir_c.value
            return True
        if cmd == 17:                      # GET_VARIABLE_UPDATE
            ctypes.cast(data, ctypes.POINTER(ctypes.c_bool))[0] = False
            return True
        if cmd == 36:                      # SET_MEMORY_MAPS: work RAM's block ($7E:0000, 128 KB)
            mm = ctypes.cast(data, ctypes.POINTER(MemoryMap))[0]
            for i in range(mm.num_descriptors):
                d = mm.descriptors[i]
                if d.ptr and d.start == 0x7E0000 and d.len >= 0x20000:
                    self._wram = (d.ptr + d.offset, d.len)
            return True
        if cmd == 11:                      # SET_INPUT_DESCRIPTORS
            return True
        return False

    def _video(self, data, width, height, pitch):
        if not data:                       # a repeated frame
            return
        if self._want_frame:
            self.last_frame = (ctypes.string_at(data, pitch * height), width, height, pitch, self.pixel_format)
            self._want_frame = False
        if self.track_black:
            n = width * (4 if self.pixel_format == 1 else 2)
            zero = bytes(n)
            self.black = all(ctypes.string_at(data + y * pitch, n) == zero
                             for y in range(height // 8, height - height // 8, max(1, height // 8)))

    def _state(self, port, device, index, id_):
        if port != 0 or device != RETRO_DEVICE_JOYPAD:
            return 0
        if id_ == 256:                     # the bitmask query
            return sum(1 << BUTTONS[b] for b in self.held)
        return 1 if any(BUTTONS[b] == id_ for b in self.held) else 0

    # --- driving ------------------------------------------------------------------------------------
    def press(self, *buttons):
        self.held = set(buttons)

    def release(self):
        self.held = set()

    def run(self, frames=1):
        for _ in range(frames):
            self.lib.retro_run()
            self.frame += 1

    def rd(self, a):
        return self.ram[a & 0x1FFFF]

    def rd16(self, a):
        return self.rd(a) | self.rd(a + 1) << 8

    def wr(self, a, v):
        self.ram[a & 0x1FFFF] = v & 0xFF

    def screenshot(self, path, scale=2):
        """The next frame as a PNG (runs one frame to get it)."""
        from PIL import Image
        self._want_frame = True
        self.run(1)
        if self.last_frame is None:
            return None
        buf, w, h, pitch, fmt = self.last_frame
        if fmt == 1:                       # XRGB8888
            img = Image.frombuffer("RGBX", (w, h), buf, "raw", "BGRX", pitch, 1).convert("RGB")
        else:                              # RGB565 (2) / 0RGB1555 (0)
            px = []
            for y in range(h):
                row = buf[y * pitch:y * pitch + w * 2]
                for x in range(w):
                    v = row[2 * x] | row[2 * x + 1] << 8
                    if fmt == 2:
                        px.append(((v >> 11 & 31) * 255 // 31, (v >> 5 & 63) * 255 // 63, (v & 31) * 255 // 31))
                    else:
                        px.append(((v >> 10 & 31) * 255 // 31, (v >> 5 & 31) * 255 // 31, (v & 31) * 255 // 31))
            img = Image.new("RGB", (w, h))
            img.putdata(px)
        if scale != 1:
            img = img.resize((w * scale, h * scale), Image.NEAREST)
        img.save(path)
        return path

    def close(self):
        try:
            self.lib.retro_unload_game()
            self.lib.retro_deinit()
        except Exception:
            pass
        import shutil
        shutil.rmtree(self._dir, ignore_errors=True)   # the core's system/save folder (nothing is kept)
