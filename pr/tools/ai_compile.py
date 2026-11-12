"""Compile FF6 SNES monster AI (BNW) into the PR's mnemonic AI scripts.

PR script model (learned from vanilla): execution starts at "Main"; Is* checks
jump to a label when true; a StartTable..EndTable block lists one Act row per
turn (cycled), plus CounterAct* / FinalAttackAct rows that apply while that
table is active; JumpBattleAI ends the evaluation (with an operand it jumps).

v1 covers what regular enemies use: random/fixed attacks per turn, multi-action
turns (LoopAct), conditional branches (HP, MP, own status, variables, variable
bits, timers, monsters remaining), counters (any hit, by command, by element),
death counters and variable updates. v2 adds two boss idioms: a timer reset
(FB 00 / FB 04) after a timer check becomes the PR's TimerAttackAct, and
"monsters die like a boss" on death (F5 xx 01/05 FF) becomes FinalAttackAct with
the PR's erase-all-and-end ability. "Bring monsters in" (F5 xx 00/02 mask) is
resolved against the fight's real formation: slots that don't exist are no-ops,
and a monster "bringing itself back" on death right before a variable-gated
"die like a boss" is FF6's trick for playing the boss death animation, so it
compiles as that death. Real summons and real extra lives stay unsupported.
Anything else makes `compile_script` report the script as unsupported so the
caller keeps the PR's own script.
"""
from collections import OrderedDict

import idmap as M

NOTHING = 696
ERASE_ALL = 735  # PR ability (process SM_ERASER): every monster vanishes, battle ends
CONFUSE = 12
SNES_CMD_TO_PR = {0: 4, 2: 2, 5: 607, 6: 608, 7: 601, 9: 611, 10: 602, 12: 504, 25: 9}
SNES_STATUS_TO_COND = {0: 9, 1: 15, 2: 10, 4: 33, 5: 13, 6: 11, 7: 5, 8: 14, 10: 19, 11: 6,
                       12: 18, 13: 12, 14: 16, 15: 7, 17: 20, 18: 25, 19: 21, 20: 26, 21: 22,
                       22: 23, 23: 24, 26: 31, 31: 17}
ELEM_ATTR = [1, 2, 3, 6, 8, 7, 4, 5]
SELF = 0x36
RANDOM_ALLY = 0x44  # "random ally" (a party member, from the monster's side); also the target
_cond_type = None


def cond_type(cond_id):
    """PR condition id (condition.csv) -> its condition_type (the ConditionType enum the
    unit-status checks take, e.g. Float 409)."""
    global _cond_type
    if _cond_type is None:
        import prdata as P
        _cond_type = {int(r["id"]): int(r["condition_type"]) for r in P.Table("condition")}
    return _cond_type.get(cond_id)
UNSUPPORTED_OPS = {"formation", "event", "command"}
COUNTER_TRIGGERS = {0x01, 0x02, 0x04, 0x05, 0x12}  # what a hit was (command, spell, element, any, death)
IGNORED_OPS = {"target", "misc", "message", "animate"}  # default targeting, no flags; animations are cosmetic
# (battle text: "message" lines ride on the turn's first act when ctx.message_keys is set)


class Unsupported(Exception):
    pass


def with_text(rows, keys, filler="Act"):
    """Battle lines on the first row (the PR shows an act's sValues keys in turn); a block
    that only talks gets a do-nothing act to carry them."""
    if not keys:
        return rows
    if not rows:
        rows = [mn(filler, [NOTHING], [100], pad=False)]
    rows[0]["operands"]["sValues"] = (list(keys) + [""] * 8)[:8]
    return rows


def mn(name, i=(), r=(), s=(), label="", pad=True):
    iv, rv, sv = list(i), [float(x) for x in r], list(s)
    if pad:
        iv += [0] * (8 - len(iv))
        rv += [0.0] * (8 - len(rv))
        sv += [""] * (8 - len(sv))
    return {"label": label, "mnemonic": name, "operands": {"iValues": iv, "rValues": rv, "sValues": sv},
            "type": 1, "comment": ""}


MAX_VAR = 14  # the PR's own scripts use AI variables 0..14

# WC's ability scaling (-ase / -asr): its scripts use placeholder ids its own assembly turns into a
# random spell of a tier each time the monster acts: 0x36-0x50 = fire/ice/bolt/earth/wind/water/
# poison/pearl/non-elemental at tier +0/+1/+2, 0x55-0x5D = cure/tek/damage, 0x5E-0x60 = the
# enemy's special. Tier = min(level x 2 // int((factor + 3) x 2), 8) + that offset + a random
# -2..+2, from the monster's level in the battle (after WC's level scaling); then any spell of the
# tier (WC data/enemy_script_custom_commands.py). The port compiles placeholder k (0-35: category
# k // 3, offset k % 3) to ability SCALED_BASE + k, a row of its own (port_ai: a copy of a spell of
# that category), and the WC plugin (WcAbilityScaling.cs) picks the spell when the monster acts,
# from the seed's ability_scaling table (plugin_table). Until 2026-11-11 the port picked one spell
# per script from the monster's unscaled ROM level (the user's Sandhorse always cast Wind Slash).
# The special's tiers (0x5E-0x60) aren't scaled: the monster's own special. WC's tiers come from
# the generator (WC_AI_TIERS, tools/wc_run.py). Without them, the ids are plain spells as before.
SCALED_BASE, SCALED_COUNT = 7600, 36
SCALED_CATS = ["fire", "ice", "bolt", "earth", "wind", "water", "poison", "pearl", "non_elemental",
               "cure", "tek", "damage"]
_SCALED = {}


def scaled_tiers():
    """WC's tier table for this seed ({} when the port isn't a scaled WC seed)."""
    import json
    import os
    path = os.environ.get("WC_AI_TIERS") or ""
    if path not in _SCALED:
        _SCALED[path] = json.load(open(path, encoding="utf-8")) if path and os.path.exists(path) else {}
    return _SCALED[path]


def scaled_index(snes):
    """WC's placeholder spell id -> its index k (0-35), or None (not a placeholder, or no scaling)."""
    if not scaled_tiers():
        return None
    if 0x36 <= snes <= 0x50:
        return snes - 0x36
    if 0x55 <= snes <= 0x5D:
        return snes - 0x36 - 4     # WC skips 0x51-0x54 (skeans/edges) the same way
    return None


def scaled_stand_in(k):
    """The PR ability a placeholder's row copies (what it casts if the plugin doesn't swap it): the
    first spell of its category two tiers up (an early-game pick)."""
    tiers = scaled_tiers()["tiers"].get(SCALED_CATS[k // 3]) or [[0xEE]]
    pool = tiers[min(k % 3 + 2, len(tiers) - 1)] or [x for row in tiers for x in row]
    return M.spell(pool[0]) if pool else None


def plugin_table():
    """The seed's ability_scaling entry for the WC plugin: tiers as PR ability ids."""
    t = scaled_tiers()
    if not t:
        return None
    return {"base": SCALED_BASE, "count": SCALED_COUNT, "factor": float(t["factor"]),
            "random": bool(t.get("random")), "categories": SCALED_CATS,
            "tiers": [[[M.spell(s) for s in tier if M.spell(s)] for tier in t["tiers"].get(c, [])]
                      for c in SCALED_CATS]}


class Context:
    def __init__(self, enemy_id, battle_ability, special_ability, slots=None, pr_entrances=False):
        self.enemy_id = enemy_id
        # True: the PR's battle event routine for this fight decides which monsters are
        # on screen, so BNW's own "bring in / hide / kill" instructions are left out
        self.pr_entrances = pr_entrances
        self.pr_events = set()   # SNES battle events the PR's battle script already plays (left out)
        self.slots = slots or {}  # formation slot -> {SNES enemy ids there}, over every fight it's in
        self.battle = battle_ability
        self.special = special_ability
        self.direct = set()   # SNES variables 0..14 the script reads/writes whole: same PR index
        self.alloc = {}       # (SNES var, bit) or (SNES var, "whole") -> PR index
        self.bits_of = {}     # SNES var -> [bits tested/set on it]
        self.skipped = 0
        self.uid = 0
        # SNES formation -> the PR party ids that battle can be (port_ai sets it), for FC 1B
        self.formation_parties = None
        # SNES battle message index -> PR story_mes keys, one per line (port_ai sets it)
        self.message_keys = None

    def messages(self, actions):
        """The PR keys for every "message" in these actions, in order (at most 8)."""
        if self.message_keys is None:
            return []
        keys = []
        for op, a in actions:
            if op == "message":
                keys += self.message_keys(a[0] | (a[1] << 8 if len(a) > 1 else 0))
        return keys[:8]

    def label(self, prefix):
        self.uid += 1
        return f"{prefix}{self.uid}"

    def prescan(self, ops):
        """Learn which variables are used whole and which bitwise, before allocating."""
        for op, args in ops:
            if op == "var" and args[0] <= MAX_VAR:
                self.direct.add(args[0])
            elif op == "if" and args[0] in (0x0C, 0x0D) and args[1] <= MAX_VAR:
                self.direct.add(args[1])
        for op, args in ops:
            if op == "if" and args[0] in (0x14, 0x15):
                self.bitvar(args[1], args[2] & 7)
            elif op == "bits":
                self.bitvar(args[1], args[2] & 7)

    def _new(self, key):
        if key not in self.alloc:
            used = self.direct | set(self.alloc.values())
            free = [n for n in range(MAX_VAR + 1) if n not in used]
            if not free:
                raise Unsupported("too many variables")
            self.alloc[key] = free[-1]  # from the top, away from the low ones scripts use directly
        return self.alloc[key]

    def ability(self, snes):
        if snes == 0xEE:
            return self.battle
        if snes == 0xEF:
            return self.special
        if snes == 0xFE:
            return NOTHING
        if scaled_tiers() and 0x5E <= snes <= 0x60:
            return self.special      # WC's special tiers: the monster's own special
        k = scaled_index(snes)
        if k is not None:
            return SCALED_BASE + k   # picked when it acts (WcAbilityScaling.cs)
        return M.spell(snes) or self.battle

    def bitvar(self, var, bit):
        """One SNES variable bit -> its own PR variable (0/1)."""
        if bit not in self.bits_of.setdefault(var, []):
            self.bits_of[var].append(bit)
        return self._new((var, bit))

    def bring_targets(self, mask):
        """Formation slots a "bring in" really affects: (self slots, other slots)."""
        hit = {s for s in range(6) if mask >> s & 1 and s in self.slots}
        mine = {s for s in hit if self.slots[s] == {self.enemy_id}}
        return mine, hit - mine

    def check_brings(self, actions):
        """A "bring in" outside a death block must be a no-op to be compiled."""
        for op, a in actions:
            if op == "end_battle":
                raise Unsupported("misc: end battle")
            if op == "self_death":
                raise Unsupported("misc: monster gives itself Death")
            if op == "bring":
                mine, others = self.bring_targets(a[0])
                if others:
                    raise Unsupported("brings in monsters")
                if mine:
                    raise Unsupported("brings itself back")

    def whole(self, var):
        """A SNES variable used as a number -> PR index."""
        return var if var <= MAX_VAR and var in self.direct else self._new((var, "whole"))


def choices(ctx, op, args):
    """An attack action -> OrderedDict(ability -> weight)."""
    out = OrderedDict()
    for a in (args if op == "random" else args[:1]):
        k = ctx.ability(a)
        out[k] = out.get(k, 0) + 1
    return out


def rates(weights):
    total = sum(weights.values())
    rs = [round(100 * w / total) for w in weights.values()]
    rs[-1] += 100 - sum(rs)
    return list(weights), rs


def var_ops(ctx, op, args):
    """var/bits actions -> list of PR mnemonics."""
    if op == "var":
        v, x = args
        mode, n = x >> 6, x & 0x3F
        bits = ctx.bits_of.get(v, [])
        if mode in (2, 3):
            if bits:
                raise Unsupported("arithmetic on a bit-tested variable")
            return [mn("ValueCalc", [ctx.whole(v), n], s=["＋" if mode == 2 else "－"])]
        value = x & 0x7F
        out = [mn("VariableAssign", [ctx.whole(v), value])]
        out += [mn("VariableAssign", [ctx.bitvar(v, b), value >> b & 1]) for b in bits]
        return out
    if op == "bits":
        kind, v, bit = args
        pv = ctx.bitvar(v, bit & 7)
        if kind == 0:  # toggle: the PR has no XOR, so compare and jump
            was_set, done = ctx.label("toggle_off"), ctx.label("toggle_done")
            return [mn("IsVariableComparison", [pv, 1, was_set], s=["＝"]),
                    mn("VariableAssign", [pv, 1]),
                    mn("IsVariableComparison", [pv, 1, done], s=["＝"]),
                    mn("Nop", label=was_set, pad=False),
                    mn("VariableAssign", [pv, 0]),
                    mn("Nop", label=done, pad=False)]
        return [mn("VariableAssign", [pv, 0 if kind == 2 else 1])]
    return []


def condition(ctx, args, target_label):
    """SNES FC condition -> list of PR mnemonics that jump to target_label when true."""
    kind, a, b = args
    J = target_label
    if kind == 0x06 and a == SELF:
        return [("IsCheckHp", [b * 128, J], ["≦"])]
    if kind == 0x07 and a == SELF:
        return [("IsCheckMp", [b, J], ["≦"])]
    if kind in (0x08, 0x09) and a == SELF and b in SNES_STATUS_TO_COND:
        return [("IsMyCondition" if kind == 0x08 else "IsNotCondition", [SNES_STATUS_TO_COND[b], J], [])]
    if kind in (0x0B, 0x16):
        return [("IsTimerJump", [J], [], [a])]
    if kind in (0x0C, 0x0D):
        return [("IsVariableComparison", [ctx.whole(a), b, J], ["＜" if kind == 0x0C else "≧"])]
    if kind in (0x14, 0x15):  # b is a bit number (BNW: "FC 15 0D 07 ; VAR013 ... bit 7")
        return [("IsVariableComparison", [ctx.bitvar(a, b & 7), 1 if kind == 0x14 else 0, J], ["＝"])]
    # a random party member has / lacks a status: the PR checks the party for it
    # (vanilla Earth Dragon: FC 08 44 Float -> IsCheckUnitStatus [1, 409, 1, J])
    if kind in (0x08, 0x09) and a == RANDOM_ALLY and b in SNES_STATUS_TO_COND and cond_type(SNES_STATUS_TO_COND[b]):
        return [("IsCheckUnitStatus", [1, cond_type(SNES_STATUS_TO_COND[b]), 1 if kind == 0x08 else 0, J], [])]
    # a random party member's level >= b (vanilla Silver Lobo: FC 0F 44 07 -> IsLevelCheck [1, 7, J])
    if kind == 0x0F and a == RANDOM_ALLY:
        return [("IsLevelCheck", [1, b, J], [])]
    # weak to one of the elements in b (vanilla Number 024: IsWeakCheck [PR element, J])
    if kind == 0x1A and a == SELF and b:
        return [("IsWeakCheck", [ELEM_ATTR[x], J], []) for x in range(8) if b >> x & 1]
    # this battle is formation a + 256 b (vanilla Chaser: FC 1B 7B 00 -> IsCheckMonsterPTId
    # [PR party, J] for the party and the PR's own copies of it)
    if kind == 0x1B and ctx.formation_parties is not None:
        ids = ctx.formation_parties(a | b << 8)
        if ids:
            return [("IsCheckMonsterPTId", [pid, J], []) for pid in ids]
    if kind == 0x10:
        return [("IsOnlyAlive", [J], [])]
    if kind == 0x13:
        return [("IsUnitAliveCount", [2, a + 1, J], ["＜"])]
    return None  # not expressible: the branch is skipped


def normalize(op, args):
    """Boss instructions v2 understands -> ("erase"|"timer_reset", ()), else unchanged.
    Raises Unsupported for ones that change the battle's flow."""
    if op == "enemies":
        anim, mode, mask = args
        if mode in (1, 5) and mask == 0xFF:
            return "erase", ()          # all monsters leave/die like a boss
        if mode in (0, 2):
            return "bring", (mask,)     # resolved against the formation later
        raise Unsupported("enemies")    # hiding / killing some monsters: not yet
    if op == "misc":
        kind = args[0]
        if kind in (0x00, 0x04):
            return "timer_reset", ()
        if kind == 0x02:
            return "end_battle", ()  # only understood inside a death block
        if kind == 0x0B and args[1] == 0x07:
            return "self_death", ()  # only understood inside a death block (it's dying anyway)
    return op, args


def is_death(cond):
    kind, a, b = cond
    return (kind == 0x06 and a == SELF and b == 0) or (kind == 0x12 and a == 0 and b == 0)


def split_blocks(ops):
    """SNES section -> list of ("if", [conds], [actions]) and ("turns", [[actions]...])."""
    blocks, conds, actions, turn, turns = [], None, [], [], []
    for op, args in ops:
        if op in UNSUPPORTED_OPS:
            raise Unsupported(op)
        op, args = normalize(op, args)
        if op == "if":
            if conds is None:
                conds, actions = [], []
            elif actions:  # new if after actions without endif: close the previous block
                blocks.append(("if", conds, actions))
                conds, actions = [], []
            conds.append(args)
        elif op == "endif":
            if conds is not None:
                blocks.append(("if", conds, actions))
            conds, actions = None, []
        elif op == "wait":
            if conds is not None:
                actions.append(("wait", ()))  # the block goes on next turn (until its endif)
            else:
                turns.append(turn)
                turn = []
        elif op == "end":
            break
        elif conds is not None:
            actions.append((op, args))
        else:
            turn.append((op, args))
    if conds is not None:
        blocks.append(("if", conds, actions))
    if turn:
        turns.append(turn)
    return blocks, [t for t in turns if t]


def split_turns(actions):
    """A block's actions split at "wait" -> one list per turn."""
    turns, cur = [], []
    for op, a in actions:
        if op == "wait":
            turns.append(cur)
            cur = []
        else:
            cur.append((op, a))
    turns.append(cur)
    return [t for t in turns if t] or [[]]


def turn_rows(ctx, turn, timer=False):
    """One SNES turn -> (pre-mnemonics, [table rows]). The PR can't pick several
    random actions in one turn, so each random action gets its own turn row;
    a turn of fixed attacks becomes one LoopAct. timer=True (a timer check whose
    actions reset the timer): the first action is a TimerAttackAct, which acts
    and resets the timer."""
    pre, attacks = [], []
    for op, args in turn:
        if op == "timer_reset":
            continue
        if op == "erase":
            raise Unsupported("enemies")  # only understood as a death action
        if op == "end_battle":
            raise Unsupported("misc: end battle")
        if op == "self_death":
            raise Unsupported("misc: monster gives itself Death")
        if op == "bring":
            mine, others = ctx.bring_targets(args[0])
            if others:
                raise Unsupported("brings in monsters")
            if mine:
                raise Unsupported("brings itself back")
            continue  # slots that aren't in its fights: nothing happens on the SNES either
        if op in ("attack", "random"):
            attacks.append(choices(ctx, op, args))
        elif op == "item":
            attacks.append(OrderedDict([(ctx.battle, 1)]))  # thrown/used items -> a normal attack
        elif op in ("var", "bits"):
            pre += var_ops(ctx, op, args)
        elif op in IGNORED_OPS:
            continue
        else:
            raise Unsupported(op)
    attacks = [w for w in attacks if list(w) != [NOTHING]]
    keys = ctx.messages(turn)
    if timer:
        ids, rs = rates(attacks[0]) if attacks else ([NOTHING], [100])
        rows = [mn("TimerAttackAct", [0] + ids, [0] + rs, pad=False)]
        for w in attacks[1:]:
            ids, rs = rates(w)
            rows.append(mn("Act", ids, rs, pad=False))
        return pre, with_text(rows, keys)
    if not attacks:
        return pre, with_text([], keys)
    if len(attacks) > 1 and all(len(w) == 1 for w in attacks):
        # fixed attacks in one turn: LoopAct with repeat counts (the game's own
        # scripts use at most 6 entries), e.g. 10x Needle -> LoopAct [145] [10]
        runs = []
        for w in attacks:
            a = next(iter(w))
            if runs and runs[-1][0] == a:
                runs[-1][1] += 1
            else:
                runs.append([a, 1])
        rows = [mn("LoopAct", [a for a, _ in runs[i:i + 6]], [c for _, c in runs[i:i + 6]], pad=False)
                for i in range(0, len(runs), 6)]
        return pre, with_text(rows, keys)
    rows = []
    for w in attacks:
        ids, rs = rates(w)
        rows.append(mn("Act", ids, rs, pad=False))
    return pre, with_text(rows, keys)


def route_triggers(ctx, cond, label):
    """A counter condition -> Main-section checks that jump to label when this hit
    matches (the PR's way of running side effects on being hit), or None."""
    kind, a, b = cond
    if kind == 0x01:
        cmds = sorted({SNES_CMD_TO_PR[x] for x in (a, b) if x in SNES_CMD_TO_PR})
        return [("IsCheckReceiveCommand", [c, label], []) for c in cmds] or None
    if kind == 0x02:
        abilities = sorted({M.spell(x) for x in (a, b) if M.spell(x)})
        return [("IsRecevieAbility", [x, label], []) for x in abilities] or None
    if kind == 0x05:
        return [("DamagedJump", [label], [])]
    if kind == 0x04 and a:  # hit by one of these elements (Number 024: IsReverseAttribute [PR element, J])
        return [("IsReverseAttribute", [ELEM_ATTR[x], label], []) for x in range(8) if a >> x & 1]
    return None


def end_battle_on_death(conds, actions):
    """"End battle" on death: with no other condition it ends the fight now (= erase all);
    behind another condition (Crane: "and the other crane is down") the fight ends by
    itself once everyone is dead, so it's dropped."""
    actions = [(op, a) for op, a in actions if op != "self_death"]  # dying anyway
    if not any(op == "end_battle" for op, _ in actions):
        return actions
    if len(conds) == 1:
        return [("erase", ()) if op == "end_battle" else (op, a) for op, a in actions]
    return [(op, a) for op, a in actions if op != "end_battle"]


def erase_gates(blocks):
    """{SNES var: n} for blocks "if VAR >= n: monsters die like a boss"."""
    gates = {}
    for _, conds, actions in blocks:
        if len(conds) == 1 and conds[0][0] == 0x0D and any(op == "erase" for op, _ in actions):
            gates[conds[0][1]] = conds[0][2]
    return gates


def death_rows(ctx, actions, gates):
    """A death block's actions -> FinalAttackAct rows. Handles FF6's boss-death trick:
    the monster brings itself back, adds to a counter, and a block gated on that
    counter makes everyone die like a boss in the same pass."""
    brings = [a for op, a in actions if op == "bring"]
    if brings:
        mine, others = set(), set()
        for (mask,) in brings:
            m, o = ctx.bring_targets(mask)
            mine |= m
            others |= o
        if others:
            raise Unsupported("brings in monsters")
        if mine:
            added = {}
            for op, a in actions:
                if op == "var" and a[1] >> 6 == 2:
                    added[a[0]] = added.get(a[0], 0) + (a[1] & 0x3F)
            if not any(v in gates and added.get(v, 0) >= n for v, n in gates.items()):
                raise Unsupported("revives itself (extra life)")
            return [mn("FinalAttackAct", [ERASE_ALL], [100], pad=False)]
    return final_rows(ctx, actions)


def counter_rows(ctx, ops, routes=None, gates=None):
    """Counter section -> rows put in every table (counter attacks, death actions).
    Counters that change variables go to `routes` instead: (triggers, conds, actions)."""
    rows = []
    if not ops:
        return rows
    blocks, turns = split_blocks(ops)
    gates = erase_gates(blocks) if gates is None else gates
    for _, conds, actions in blocks:
        if any(is_death(c) for c in conds):
            rows += death_rows(ctx, end_battle_on_death(conds, actions), gates)
            continue
        ctx.check_brings(actions)
        if routes is not None and any(op in ("var", "bits") for op, _ in actions):
            if conds and conds[0][0] not in COUNTER_TRIGGERS:
                # the SNES runs every counter block on every hit: put the block's own
                # trigger first ("if VAR001 >= 3 and hit by Fire"), or "any hit" if it has none
                trig = next((c for c in conds if c[0] in COUNTER_TRIGGERS), None)
                conds = [trig] + [c for c in conds if c is not trig] if trig else [(0x05, 0, 0)] + list(conds)
            n = len(routes)
            triggers = route_triggers(ctx, conds[0], f"counter{n}")
            if triggers is None or any(condition(ctx, c, "x") is None for c in conds[1:]):
                ctx.skipped += 1
            else:
                routes.append((triggers, conds, actions))
            continue
        attacks = [choices(ctx, op, a) for op, a in actions if op in ("attack", "random")]
        if not attacks:
            continue
        ids, rs = rates(attacks[0])
        triggers = [c for c in conds if c[0] in (0x01, 0x04, 0x05, 0x12)]
        if not triggers:
            continue
        kind, a, b = triggers[0]
        before = len(rows)
        if kind == 0x05:
            rows.append(mn("CounterAct", ids, rs, pad=False))
        elif kind == 0x01 and a in SNES_CMD_TO_PR:
            rows.append(mn("CounterActReceiveCommand", [SNES_CMD_TO_PR[a]] + ids, [0] + rs, pad=False))
        elif kind == 0x04 and a:
            elem = [ELEM_ATTR[x] for x in range(8) if a >> x & 1][0]
            rows.append(mn("CounterActAttribute", [elem, ids[0]]))  # the game always pads this one
        elif kind == 0x12 and a == 0 and b == 0:
            rows.append(mn("FinalAttackAct", ids, rs, pad=False))
        # other counter conditions: not expressible in v1, dropped
        if len(rows) > before:
            with_text(rows[before:], ctx.messages(actions))
    for t in turns:  # unconditional counter actions
        for op, a in t:
            if op in ("attack", "random"):
                ids, rs = rates(choices(ctx, op, a))
                rows.append(mn("CounterAct", ids, rs, pad=False))
    return rows


def final_rows(ctx, actions):
    """Actions on death -> FinalAttackAct rows; erase = everyone leaves, battle ends."""
    if any(op == "erase" for op, _ in actions):
        return [mn("FinalAttackAct", [ERASE_ALL], [100], pad=False)]
    rows = []
    for op, a in actions:
        if op in ("attack", "random"):
            ids, rs = rates(choices(ctx, op, a))
            rows.append(mn("FinalAttackAct", ids, rs, pad=False))
    return with_text(rows, ctx.messages(actions), "FinalAttackAct")


def compile_script(ctx, main, counter):
    """Returns a list of mnemonics (dicts) or raises Unsupported."""
    if ctx.pr_entrances:  # keep "everyone dies like a boss", drop the rest of F5
        keep = lambda op, a: op != "enemies" or (a[1] in (1, 5) and a[2] == 0xFF)
        main = [(op, a) for op, a in main if keep(op, a)]
        counter = [(op, a) for op, a in counter if keep(op, a)]
    if ctx.pr_events:     # battle events the PR's battle script plays itself
        main = [(op, a) for op, a in main if not (op == "event" and a[0] in ctx.pr_events)]
        counter = [(op, a) for op, a in counter if not (op == "event" and a[0] in ctx.pr_events)]
    # a monster whose main section never attacks does nothing on its turns (the Whelk's shell),
    # not the normal attack the tables fall back on otherwise
    idle = [mn("Act", [NOTHING], [100], pad=False)] if not any(op in ("attack", "random", "item") for op, _ in main) else None
    ctx.prescan(list(main) + list(counter))
    blocks, turns = split_blocks(main)
    routes = []
    counters = counter_rows(ctx, counter, routes)
    # death checks in the main section ("if my HP <= 0") act like death counters
    gates = erase_gates(blocks)
    for _, conds, actions in [b for b in blocks if len(b[1]) == 1 and is_death(b[1][0])]:
        counters += death_rows(ctx, end_battle_on_death(conds, actions), gates)
    blocks = [b for b in blocks if not (len(b[1]) == 1 and is_death(b[1][0]))]
    out = []
    jumps = []  # (index in out, operand index, label)

    def emit(m):
        out.append(m)
        return len(out) - 1

    def table(rows):
        emit(mn("StartTable"))
        for r in counters:
            emit(r)
        for r in rows:
            emit(r)
        emit(mn("EndTable"))
        emit(mn("JumpBattleAI"))

    emit(mn("Nop", label="Main", pad=False))
    emit(mn("SyncAI"))
    jumps.append((emit(mn("IsMyCondition", [CONFUSE, 0])), 1, "confused"))
    # being hit in a way a counter cares about: run its side effects (and counter attack)
    for n, (triggers, conds, actions) in enumerate(routes):
        label = f"counter{n}"
        for name, iv, sv in triggers:
            idx = emit(mn(name, iv, [], sv))
            jumps.append((idx, iv.index(label), label))
    # conditional branches, checked in order; AND chains continue at a sub-label
    branch_labels = []
    for n, (_, conds, actions) in enumerate(blocks):
        label = f"branch{n}"
        first = condition(ctx, conds[0], label)
        if first is None or any(condition(ctx, c, "x") is None for c in conds[1:]):
            ctx.skipped += 1
            continue
        branch_labels.append((label, conds, actions))
        for name, iv, sv, *rv in first:
            idx = emit(mn(name, iv, rv[0] if rv else [], sv))
            jumps.append((idx, iv.index(label), label))
    # default turns
    pre_all, rows = [], []
    for t in turns:
        pre, trows = turn_rows(ctx, t)
        pre_all += pre
        rows += trows
    for m in pre_all:
        emit(m)
    table(rows or idle or [mn("Act", [ctx.battle], [100], pad=False)])
    for n, (label, conds, actions) in enumerate(branch_labels):
        start = len(out)
        emit(mn("Nop", label=label, pad=False))
        for k, cond in enumerate(conds[1:], 1):  # remaining ANDed conditions
            sub = f"{label}_{k}"
            for name, iv, sv, *rv in condition(ctx, cond, sub):
                idx = emit(mn(name, iv, rv[0] if rv else [], sv))
                jumps.append((idx, iv.index(sub), sub))
            emit(mn("JumpBattleAI"))  # condition failed: act normally next time
            emit(mn("Nop", label=sub, pad=False))
        timer = any(c[0] in (0x0B, 0x16) for c in conds) and any(op == "timer_reset" for op, _ in actions)
        pre, brows = [], []
        for k, t in enumerate(split_turns(actions)):  # a block can span turns (SNES "wait")
            tp, tr = turn_rows(ctx, t, timer=timer and k == 0)
            pre += tp
            brows += tr
        for m in pre:
            emit(m)
        table(brows or idle or [mn("Act", [ctx.battle], [100], pad=False)])
    for n, (triggers, conds, actions) in enumerate(routes):
        label = f"counter{n}"
        emit(mn("Nop", label=label, pad=False))
        for k, cond in enumerate(conds[1:], 1):  # remaining ANDed conditions
            sub = f"{label}_{k}"
            for name, iv, sv, *rv in condition(ctx, cond, sub):
                idx = emit(mn(name, iv, rv[0] if rv else [], sv))
                jumps.append((idx, iv.index(sub), sub))
            emit(mn("JumpBattleAI"))
            emit(mn("Nop", label=sub, pad=False))
        emit(mn("StartTable"))
        crows = []
        for op, a in actions:
            if op in ("attack", "random"):
                ids, rs = rates(choices(ctx, op, a))
                crows.append(mn("CounterAct", ids, rs, pad=False))
        for r in with_text(crows, ctx.messages(actions), "CounterAct"):
            emit(r)
        for op, a in actions:
            for m in var_ops(ctx, op, a):
                emit(m)
        emit(mn("EndTable"))
        emit(mn("JumpBattleAI"))
    emit(mn("Nop", label="confused", pad=False))
    emit(mn("StartTable"))
    emit(mn("Act", [ctx.battle], [100], pad=False))
    emit(mn("EndTable"))
    emit(mn("JumpBattleAI"))
    emit(mn("Nop", label="EndBattle", pad=False))
    emit(mn("Exit"))
    # resolve labels -> mnemonic indices
    where = {m["label"]: i for i, m in enumerate(out) if m["label"]}
    for idx, pos, label in jumps:
        out[idx]["operands"]["iValues"][pos] = where[label]
    for m in out:
        for i, v in enumerate(m["operands"]["iValues"]):
            if isinstance(v, str):
                m["operands"]["iValues"][i] = where[v]
    return out
