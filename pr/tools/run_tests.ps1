# Every test of the ports in one go, with a pass/fail table at the end.
#
#   offline (seconds):
#     pr/tests            Python unittest: test_wc.py (the WC event scripts run in a model of the
#                         game's script machine and flag storage), test_wc_xref.py (one seed from
#                         WC's own SNES generator and from ours, cross-checked; the SNES game's
#                         start run in Mesen 2 headless, tools/mesen2), test_mod.py (BNW's
#                         Magicite files), test_hd2d.py (the HD-2D bundle)
#     plugin/Wc.Tests     the WC flag screen (every flag on/off, presets round-trip)
#     plugin/Rules.Tests  BNW's rules
#     plugin/PrKit.Tests  the modding kit's game-free parts (pr/MODDING.md); pr/tests has test_prkit.py
#   in the game, headless (a minute or two each; never a window, muted; WcTest.cs):
#     narshe-wob          New Game, (Take the wheel.), land, walk into Narshe: the WoB's; walk out:
#                         the WoB map with the Blackjack
#     narshe-wor          New Game, (Fly to the World of Ruin.): riding the Falcon; land, Narshe is
#                         the WoR's; walk out: the WoR map with the Falcon
#     worldsync ...       the world flags / airship follow the map; the other scenarios below
#
#   pr/tools/run_tests.ps1                     everything
#   pr/tools/run_tests.ps1 -Offline            offline only
#   pr/tools/run_tests.ps1 -Only narshe-wob,worldsync
#
# The game scenarios never start while the game runs (and a run stops its own game if you start
# yours); each makes its own test seeds and puts the active seed back (WcTest.cs). A run that
# didn't finish is cleaned up with tools/wc_test_reset.py and the active seed it had.
param([switch]$Offline, [string[]]$Only = @(), [int]$Minutes = 8)

$ErrorActionPreference = "Continue"
$pr = Split-Path $PSScriptRoot -Parent
$root = Split-Path $pr -Parent
$py = Join-Path $pr ".venv\Scripts\python.exe"
$game = "D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR"
$state = Join-Path $game "BepInEx\plugins\WorldsCollide\seeds\state.json"
$log = Join-Path $game "BepInEx\LogOutput.log"
$results = New-Object System.Collections.Generic.List[object]
$report = Join-Path $pr "build\test-report.txt"
New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
"tests $(Get-Date -Format s)" | Set-Content -Encoding utf8 $report

function Add-Result($name, $ok, $detail, $seconds) {
    $results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) { "pass" } else { "FAIL" }); Seconds = [int]$seconds; Detail = $detail })
    "$(if ($ok) { 'pass' } else { 'FAIL' })  $name  $detail" | Add-Content -Encoding utf8 $report
}

function Wanted($name) { return $Only.Count -eq 0 -or ($Only | ForEach-Object { $_ -split "," }) -contains $name }

# --- offline -------------------------------------------------------------------------------
if (Wanted "python") {
    $t = Get-Date
    $out = & $py -m unittest discover -s (Join-Path $pr "tests") 2>&1 | Out-String
    $ok = $LASTEXITCODE -eq 0
    $summary = ($out -split "`n" | Where-Object { $_ -match "^Ran \d+ tests|^OK|^FAILED" }) -join "; "
    Add-Result "python (pr/tests)" $ok $summary ((Get-Date) - $t).TotalSeconds
    if (-not $ok) { $out | Add-Content -Encoding utf8 $report }
}
foreach ($proj in @("Wc.Tests", "Rules.Tests", "PrKit.Tests")) {
    if (-not (Wanted $proj.ToLower())) { continue }
    $t = Get-Date
    Push-Location (Join-Path $pr "plugin\$proj")
    $out = & dotnet run -c Release 2>&1 | Out-String
    $ok = $LASTEXITCODE -eq 0
    Pop-Location
    $last = ($out.Trim() -split "`n" | Select-Object -Last 1).Trim()
    Add-Result $proj $ok $last ((Get-Date) - $t).TotalSeconds
    if (-not $ok) { $out | Add-Content -Encoding utf8 $report }
}

# --- in the game, headless -------------------------------------------------------------------
$p = "preset=moogle cup"
$scenarios = [ordered]@{
    "narshe-wob"  = "seed=t-nwob;$p;newgame=1;narshe=wob"
    "narshe-wor"  = "seed=t-nwor;$p;newgame=1;narshe=wor"
    "worldsync"   = "seed=t-wsync;$p;newgame=1;worldsync=1"
    "worlds"      = "seed=t-worlds;$p;newgame=1;worlds=1"
    "worlds-back" = "seed=t-worlds2;$p;newgame=1;worlds=2"
    "atma"        = "seed=t-atma;$p;newgame=1;atma=1"
    "metamorph"   = "seed=t-morph;$p;flags=-pd -nee;newgame=1;morph=1"
    "zozo-clock"  = "seed=t-clock;$p;newgame=1;clock=1"
    "rooms"       = "seed=t-rooms;$p;newgame=1;rooms=1"
    # an audit, not a check: every town and dungeon visited in a fresh WC game, what plays on arrival logged
    "tour-wob"    = "seed=t-tour;$p;newgame=1;tour=wob"
    "tour-wor"    = "seed=t-tour2;$p;newgame=1;tour=wor"
    # the game started with a seed already active (made here first, the way a restart finds one):
    # its data in the game, every character's command the seed's (the user's restarted games ran
    # seeds on vanilla data)
    "startup"     = "startup=1"
    "south-figaro" = "seed=t-sf;$p;newgame=1;check=south_figaro"
    # freeing Celes by talking to her (as the action button does): the reward at once, no
    # question, no story line (WC's event/south_figaro.py; the user's playtest, 2026-11-06)
    "celes"       = "seed=t-celes;$p;newgame=1;celes=1"
    # down the Ancient Castle library's stairs: the queen's room fades in (the user's Deck seed,
    # 2026-11-12: its queen statue shows the reward, Gogo, and the room stayed black)
    "queen-room"  = "seed=gaadl68ix7j4;flags=-cg -oa 2.2.2.2.6.6.4.9.9 -ob 3.1.1.2.9.9.4.12.12.10.21.21 -oc 30.8.8.1.1.11.8 -od 59.1.1.11.31 -oe 26.8.8.1.1.12.7 -sc1 random -sc2 random -sc3 random -sal -eu -csrp 100 125 -fst -brl -slr 6 10 -lmprp 75 115 -lel -srr 25 35 -rnl -sdr 3 4 -das -dda -dns -sch -scis -com 98989898989898989898989898 -rec1 28 -xpm 3 -mpm 5 -gpm 5 -nxppd -lsced 2 -hmce 2 -xgced 2.5 -ase 2 -msl 40 -sed -bbs -drloc shuffle -stloc mix -be -bnu -dgne -wnz -mmnu -cmd -res -fer 0 -escr 100 -esr 2 5 -elr -ebr 100 -emprp 75 115 -ems -emi -nm1 random -rnl1 -rns1 -nm2 random -rnl2 -rns2 -nmmi -mmprp 75 115 -gp 5000 -smc 3 -sws 3 -sfd 3 -sto 1 -ieor 45 -ieror 45 -ir premium -csb 2 16 -mca -stra -saw -sisr 35 -sprp 75 115 -sdm 5 -npi -ccsr 35 -chrm 0 0 -cms -frw -cor 100 -crr 100 -crvr 255 255 -crm -ari -anca -adeh -ame 1 -nmc -noshoes -nfps -fs -fe -fvd -fr -fj -fbs -fedc -ond -etn -wmhc;newgame=1;queen=1"
    # ... and with the queen's own statue (this seed's reward there is an esper): her stone
    "queen-room-esper" = "seed=t-cwob;$p;newgame=1;queen=1"
    "features"    = "seed=t-feat;$p;flags=-eer 2 4 -scan -pd -chrm 20 10 -csb 3 8;newgame=1;features=1"
    "sprites"     = "seed=t-spr;$p;flags=-eer 2 4 -scan -pd -cspr 3.1.2.0.4.5.6.7.8.9.10.11.12.13.14.15.18.19.20.21;newgame=1;features=1"
    "battles"     = "seed=t-boss;$p;newgame=1;battles=1"
    "objectives"  = "seed=t-obj;$p;flags=-oa 48.4.4.1.1.2.1.1 -ob 30.8.8.1.1.2.1.1 -oc 37.1.1.2.1.1 -od 51.3.3.1.1.2.1.1 -oe 55.1.1.2.1.1;newgame=1;objectives=1"
    "checks-wob"  = "seed=t-cwob;$p;newgame=1;check=ancient_castle,gau_father_house,kohlingen,zozo,owzer_mansion,zone_eater,mt_zozo,umaro_cave,tritoch,empty_chest,train_chest"
    "checks-more" = "seed=t-cmore;$p;newgame=1;check=serpent_trench,phantom_train,south_figaro_cave_wob,doma_wob,phoenix_cave,floating_continent_3,imperial_camp"
    # the long scenes, each its own run (together they outlast one run's time)
    "sealed-gate" = "seed=t-sg;$p;newgame=1;check=sealed_gate"
    "narshe-battle" = "seed=t-nb;$p;newgame=1;check=narshe_battle"
    "kefka-tower" = "seed=t-kt;$p;newgame=1;track=1;check=kefka_tower"
    "final-kefka" = "seed=t-fk;$p;newgame=1;check=final_kefka"
    "tzen"        = "seed=t-tzen;$p;newgame=1;tzen=1"
    "seeds"       = "seed=t-a;$p;seed2=t-b;load=1"
    # the seed list's Delete: not the current seed or one a save uses; through the screen's rows
    "seed-delete" = "seed=t-d1;$p;seed2=t-d2;delete=1"
    # WC dealing MagiTek as a command (-com): listed and usable for Terra only, one slot, WC's 4 moves
    "magitek"     = "seed=t-tek;$p;flags=-com 29989898989898989898989898;newgame=1"
    # a monster-in-a-box with the seed's monsters (-cms/-fer): the port's battle, on the box's background
    "monster-box" = "seed=t-box;$p;newgame=1;check=monster_box"
    # the Veldt: a WC encounter (a per-area copy of its formation's party) opens its Veldt fight
    "veldt"       = "seed=t-veldt;$p;newgame=1;check=veldt"
    # auto-battle with Mug first and no Fight: Mugs at monsters, then a second battle that replays
    # the first one's Mug (it must be re-aimed at this battle's monsters, not sit with a full gauge)
    "mug-auto"    = "seed=t-mug;$p;newgame=1;check=mug_auto"
    # -nxppd (both presets): each standing character gets a battle's whole EXP
    "nxppd"       = "seed=t-exp;$p;newgame=1;check=nxppd"
    # -fbs (both presets): Kefka's Tower's drop past the Demon statue opens only once it is beaten
    "kt-skip"     = "seed=t-ktskip;$p;newgame=1;check=kt_skip"
    # the Floating Continent from the Blackjack's wheel: the trip's deck under attack (no wheel),
    # WC's 3 air force fights, Ultros/Chupon, Air Force, the landing (the user's Deck, 2026-11-12)
    "fc-trip"     = "seed=t-fctrip;$p;newgame=1;check=fc_trip"
    # Vargas' fight without the PR's Sabin events (WC blanks them): no one joins or leaves, no line, won
    "vargas"      = "seed=t-vargas;$p;newgame=1;check=vargas"
    # the game's own GetItem op gives what a script names (the porter's scripted chests without the WC plugin)
    "getitem"     = "seed=t-getitem;$p;newgame=1;check=getitem"
    "menu"        = "menu=1"
}
if ($Offline) { $scenarios = [ordered]@{} }
$activeWas = ""
if (Test-Path $state) { try { $activeWas = (Get-Content $state -Raw | ConvertFrom-Json).active } catch { } }
foreach ($name in $scenarios.Keys) {
    if (-not (Wanted $name)) { continue }
    if (@(Get-Process -Name "FINAL FANTASY VI" -ErrorAction SilentlyContinue).Count -gt 0) {
        Add-Result $name $false "skipped: the game is running" 0
        continue
    }
    $t = Get-Date
    if ($name -eq "startup") {
        # a seed made outside the game and set active, as a restart finds the last one played
        $seeds = Split-Path $state -Parent
        $id = (Get-Date -Format "yyyyMMdd-HHmmss") + "-t-startup-test"
        $gen = & $py (Join-Path $PSScriptRoot "wc_seed.py") --seed t-startup --preset "moogle cup" --out (Join-Path $seeds $id) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Add-Result $name $false "the test seed wasn't made: $(($gen.Trim() -split "`n" | Select-Object -Last 1))" ((Get-Date) - $t).TotalSeconds
            & $py (Join-Path $PSScriptRoot "wc_test_reset.py") $activeWas | Out-Null
            continue
        }
        $st = Get-Content $state -Raw | ConvertFrom-Json
        $st.active = $id
        $st.activeFiles = @()
        [IO.File]::WriteAllText($state, ($st | ConvertTo-Json -Depth 6))   # UTF-8 without a BOM (Python reads it too)
    }
    $out = & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "probe_headless.ps1") `
        -Minutes $Minutes -Until "wc test\] done" -WcTest $scenarios[$name] 2>&1 | Out-String
    if ($name -eq "startup") { & $py (Join-Path $PSScriptRoot "wc_test_reset.py") $activeWas | Out-Null }
    $done = [regex]::Match($out, "wc test\] done: (\d+) failures")
    $fails = ($out -split "`n" | Where-Object { $_ -match "\[wc test\] FAIL" } | ForEach-Object { ($_ -replace ".*\[wc test\] FAIL\s*", "").Trim() })
    if ($done.Success) {
        $n = [int]$done.Groups[1].Value
        Add-Result $name ($n -eq 0) $(if ($n -eq 0) { "0 failures" } else { "$n failures: " + ($fails -join " | ") }) ((Get-Date) - $t).TotalSeconds
    }
    else {
        $how = ($out -split "`n" | Where-Object { $_ -match " after \d+ s" } | Select-Object -First 1)
        Add-Result $name $false "didn't finish ($($how.Trim())); $($fails -join ' | ')" ((Get-Date) - $t).TotalSeconds
        & $py (Join-Path $PSScriptRoot "wc_test_reset.py") $activeWas | Out-Null
    }
    if (Test-Path $log) { Copy-Item $log (Join-Path $pr "build\test-$name.log") -Force }
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Output
$failed = @($results | Where-Object { $_.Result -ne "pass" }).Count
Write-Output "$($results.Count - $failed) of $($results.Count) passed; report: $report"
exit $(if ($failed -eq 0) { 0 } else { 1 })
