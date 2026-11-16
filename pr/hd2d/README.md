# HD-2D look for FF6 Pixel Remaster

A BepInEx plugin (`pr/plugin/Hd2dPlugin`, `HD2D.PR.dll`) plus a shader bundle
(`hd2d.bundle`) that gives the field, world map and battles an Octopath-style look:
tilt-shift depth of field on the field and world map, a blurred and hazed battle backdrop
behind sharp characters, bloom, dust motes, colour grade and vignette. Menus, text and the
HUD stay sharp. Independent of the BNW plugin; works with vanilla or any Magicite mod.
Installed by `pr/tools/deploy.sh` (PC + Steam Deck) into `BepInEx/plugins/HD2D/`.

The blur pyramid, bloom, grade, vignette and dust follow the user's own HD-2D battle look
in `C:\Projects\Gamedev\Unity\roguejrpg` (`Assets/Resources/Hd2d/Hd2dPost.shader`,
`Assets/Scripts/View/Battle/Hd2d/`), defaults included; the battle backdrop's floor/horizon/
wall model is that project's diorama idea done in screen space.

## Keys and settings

- **F7** turns it on/off, **F6** re-reads the config (tune while playing). On the Deck,
  map a back button to F7/F6 in Steam Input.
- `BepInEx/config/ff6mod.hd2d.cfg` (written on first launch): `[General]` plus one
  section per image — `[Field]`, `[WorldMap]`, `[Battle]` — with focus band, blur,
  bloom, grade, haze and vignette values. Every key has a comment.
- The sharp band follows the party leader on the field and world map (`FollowPlayer`).

## How it works

The game renders the field / world map / battle into their own render textures
(`Last.Camera.FrontRenderTarget.GetRenderTex(kField | kBirdView | kBattle)`) and the UI
into another; a screen camera then draws the composite material
(`PostProcessLite.GetMaterial()`, shader `Last/PostProcessLite`) that samples
`_MainGameTex` + `_OverlayTex`. The plugin, from `Camera.onPreRender` of the first screen
camera each frame (off-screen cameras have all rendered by then):

1. reads `_MainGameTex` and works out which image it is (same-size snapshots keep the
   last look; anything else is left alone);
2. runs `Hidden/HD2D/Post`: sharp-bilinear upscale of the pixel art to about screen size
   (320x180 field / 960x540 world map / 384x216 battle -> 1422x800 on the 1280x800 Deck
   screen), a 4-level 13-tap blur pyramid, then the final pass (tilt-shift blur picked per
   pixel in pyramid levels, haze, bloom from the softest levels, dust, grade, vignette,
   dither);
3. points `_MainGameTex` at the result and puts the game's own texture back at the end of
   the frame (`WaitForEndOfFrame`), so game code (transitions, save screenshots) never sees
   ours.

Battles: the backdrop camera (`CameraBattlebackGround`, depth -5) draws into the battle
image before the characters. From `Camera.onPostRender` of that camera the plugin blurs the
backdrop in place: floor sharp between `UnitsBottom` and `UnitsTop`, blurring up to
`Horizon` and down to the screen's bottom, the wall/sky past the horizon at `FarBlur` with
haze in its own colour. The characters are drawn afterwards, sharp. If that hook doesn't
run, the final pass falls back to the battle section's tilt-shift band.

Blur amounts are pyramid levels at screen resolution; a smaller image subtracts
log2(screen height / image height) levels so numbers look the same in every mode.

Config upgrades: `[General] DefaultsVersion`; on update, values still at an older
version's default (`Defaults.History`) move to the new default, changed values stay, keys a
profile section no longer has are removed.

With the game's fake-CRT option on, the image is processed at its own resolution so the
CRT effect still lines up with the pixels.

## Building

- Shaders: `pr/hd2d/build_bundle.sh` → `pr/hd2d/out/hd2d.bundle`. Needs **Unity
  2019.4.26f1** (the game's version; installed via Unity Hub, changeset `e0392c6b2363`);
  a bundle from another minor version won't load. Compiled for D3D11 (Windows, and the Deck
  through Proton/DXVK), Vulkan and GL core.
- Plugin: `cd pr/plugin/Hd2dPlugin && dotnet build -c Release -p:Deploy=true`.
- Offline tests: `cd pr/plugin/Hd2d.Tests && dotnet run -c Release` (config binding,
  colour parsing, math) and `pr/.venv/Scripts/python -m unittest pr/tests/test_hd2d.py`
  (bundle: Unity version, shader name, passes, platforms, pass numbers vs the plugin).
- `[Testing] SelfTestQuitSeconds` (developer check, 0 = off): runs the effect once on
  whatever is on screen, logs mean colour and top/middle detail of source vs output, and
  quits the game after that many seconds.

## Status

**Shelved 2026-11-05:** uninstalled from the PC and the Deck (BepInEx/plugins/HD2D
deleted) and taken out of `pr/tools/deploy.sh`; the source and `ff6mod.hd2d.cfg` stay. Last
version 0.4.2: the world map tilt was fixed to add to the game's own ~20 degrees on foot
(0.4.1 skipped it) but never seen in game; town shadows' draw order and the leader standing
up on the world map were still open.

v0.1.0 (seen in game, 2026-11-04): loads and renders on PC (D3D11, gamma). The world map
came out blurred everywhere with a green wash: the band follows the player through
`CameraBirdView.WorldToViewportPoint`, but world-map sprites use their own projection
(FieldSpriteEntityShader's ratio parameters), so the band landed near the top; bloom's
0.7 max-channel threshold caught ordinary bright grass. Battles barely changed (gentle band).
Opening the menu switches the main image to 'CampMenu BG' 1280x800 (left as is).

v0.2.0: world map band fixed at 0.42 (FollowPlayer off there; any projected height outside
0.05-0.95 is ignored everywhere), bloom 0.8 threshold from blurred levels, haze mostly off
on the field, battle backdrop blur (above), roguejrpg's grade/vignette/dust, `ShowBlur`
tuning view. Not yet seen in game: needs a look at a town, the world map (on foot and by
airship) and a battle, and the log's `battle backdrop:` and `player on screen at height`
lines.

v0.2.0 seen on PC (2026-11-04): runs clean; battle backdrop camera draws into its own
512x256 texture (blurred there). User: "the main problem is the lack of shadows", and the
field should tilt so characters aren't lying flat on it.

v0.3.0 (`Diorama.cs`, `HD2DShadow.shader`; `[Field]` Tilt/Perspective/StandUp/Resolution,
`[Field]`+`[Battle]` CastShadow/ContactShadow/ShadowLength/ShadowSlant):
- Field drawn at N x 320x180 (`Resolution` 0 = screen height / 180): from `Camera.onPreCull`
  the field cameras (target = the game's 'Field' texture) render into our texture instead;
  after each, it's blitted down into 'Field' so the game's own uses stay current. The first
  redirect checks `RenderTexture.active`/`activeTexture` in onPreRender and falls back to
  the game's size if Unity kept the old target (log: `field at WxH: ...`).
- Tilt: keystone projection on those cameras (`Hd2dMath.Keystone`): x, y / (1 + k*y),
  y * cos(tilt), depth unchanged (x0.5). A rotated camera would mix map height into depth
  and break the game's layering (roof tiles over characters by depth); this keeps it exact.
  Projection reset in onPostRender.
- Stand-up: each character's body + head SpriteRenderers (FieldCharaEntityShader, grouped
  by parent) are stretched by w(feet)/cos(tilt) about the feet for the CameraTileMap render,
  restored in its onPostRender.
- Shadows (roguejrpg's Hd2dShadow, plus a procedural contact ellipse): our own GameObjects
  under a DontDestroyOnLoad root. Field: per character a cast copy of each body part (one
  material, queue AlphaTest+2, ZTest LEqual: over the ground, under characters and roofs)
  and a contact quad. Battle: per BattleSpriteEntity a copy of the body mesh with its
  texture, ground = lowest feet seen (a Jump lifts the unit off it), queue backdrop+1 or
  units-1 (log: `battle: N units ...; backdrop ...; shadows at queue ...`).
- Not yet seen in game. Watch for: characters poking through roofs (shouldn't), map tiles
  missing at the top edge when tilted (if the game culls by the flat view), shadow offset
  from the feet (sprite padding), battle shadows hidden under the backdrop.

## Ideas for later

- Per-backdrop horizons (FF6's backdrops differ: caves, towns, interiors), keyed by the
  battle's background asset id, like roguejrpg's hd2d.json.
- Sun glow and light shafts per backdrop (roguejrpg has them).
- Diorama tilt: perspective camera on the field with billboarded sprites.
- Point lights on torches/lamps/windows, light shafts, dust motes.
- Per-map presets (caves darker, towns warmer, World of Ruin desaturated).
