# SHEPHERD — a server-legal inventory sorter for Space Engineers

<p align="center">
    <img src="./thumb.png" alt="SHEPHERD" width="70%"/>
</p>

SHEPHERD moves every item stack into the container you've tagged for its category, keeps machines fed,
keeps loadouts stocked, and crafts or refines what you're short of. It is built to stay inside strict
programmable-block runtime limits (such as the Advanced PB Limiter): at most one conveyor transfer per tick,
paced off its own run time, and routing that keeps no saved state.

**Found a bug or have a question?** Open an issue at
[github.com/chrumck/SHEPHERD/issues](https://github.com/chrumck/SHEPHERD/issues) — include your
`[SHEPHERD-LOG]` panel and, for performance, the `[SHEPHERD-DEBUG]` panel.

---

## Using Sacrificial Stockpile Manager?

[SSM](https://github.com/scotlandsyard/SpaceEngineersPlugins/tree/main/Projects/Sacrificial%20Stockpile%20Manager)
is a client plugin by scotlandsyard, installed from the Pulsar plugin list. It gives you a full in-game interface
to your grid's inventory: menus, LCD pages, sorting, quotas and crafting. The two complement each
other: SSM is what you work from while you play, and SHEPHERD runs on the server, so it keeps the grid going
whenever the server has it loaded, whether or not you're at it. Run both:

1. **SSM** for the interface and general grid management — checking stock, sorting on demand, unloading
   ships, setting things up.
2. **SHEPHERD** for automated, headless operation — sorting, machine feeding, reserves and crafting, within
   the server's limits.
3. **Give each job to one of them.** On a SHEPHERD grid leave SSM's Automation off, or keep its quotas, limits
   and accepted items away from what SHEPHERD handles, so the two never move the same items back and forth.

---

## Install

1. **Steam Workshop:** subscribe. **Or GitHub:** download `Script.cs` from the assets of the latest release on
   the [Releases](https://github.com/chrumck/SHEPHERD/releases) page (it isn't in the repository's files) and put
   it into `%AppData%\SpaceEngineers\IngameScripts\local\SHEPHERD\`.
2. Build a programmable block, open **Edit → Browse Scripts → SHEPHERD**, then **Check Code** and **OK**.
3. Tag your containers (below). SHEPHERD starts itself — no timer block, no argument.
4. Give it a minute or two to get going after any start: a compile, a server restart, or your grids loading when
   you log in or arrive in a sector. A script's first runs are its slowest, so SHEPHERD spaces them out on purpose
   to stay under the server's runtime limits.

---

## Tags

Everything is driven by block names. The `SHEPHERD` keyword and the word after its dash (`-Off`, `-RESERVE`,
`-LOG`, …) are case-insensitive; category and pool names are matched as written.

| Tag in the block name | What it does |
|---|---|
| `[SHEPHERD:Ores,Ingots]` | Container for these categories. |
| `[SHEPHERD:*]` | Catch-all: takes anything with no home of its own, and overflow. Give at least one container this. |
| `[SHEPHERD:Ores:#5]` | Fill priority: higher fills first (default `0`, negatives allowed). Applies to every category on the container, `*` included. |
| `[SHEPHERD-Off]` | Leave this block completely alone. |
| `[SHEPHERD-Split]` | On a connector: nothing beyond it is managed (see **Docked ships**). |
| `[SHEPHERD-RESERVE]`, `[SHEPHERD-RESERVE:Name]` | Reserve-pool container (see **Reserve pools**). |
| `[SHEPHERD-Main]`, `[SHEPHERD-Coop]` | The assembler SHEPHERD crafts on, and the ones that help it (see **Assembler modes**). |
| `[SHEPHERD]`, `[SHEPHERD-LOG]`, `[SHEPHERD-DEBUG]` | Status, log and run-time screens (see **Displays**). |

A container tag or reserve tag on a block **without an inventory** (an LCD) makes it a stock or levels screen
instead.

**Where a stack goes:** a container naming its category beats the catch-all; within each, higher `:#N`
first, then the order blocks are found. A stack already in a container that accepts it stays put — except
in a catch-all, which it leaves as soon as a container named for its category has room.

---

## Categories

| Category | Holds |
|---|---|
| `Ores` | Raw ore |
| `Ingots` | Refined materials |
| `Components` | Construction components |
| `Tools` | Hand tools, hand weapons, gas bottles |
| `Ammo` | Magazines and ammunition |
| `Consumables` | Food, medkits, powerkits |
| `Others` | Everything else (datapads, …) |

### Custom categories

Override an item's category, or invent a new one, in the programmable block's **Custom Data**:

```ini
[Categories]
Ore/Stone=Ingots
Component/Explosives=Explosives
```

1. The key is the item's `TypeId/SubtypeId` (the `MyObjectBuilder_` prefix is optional). **Learn mode**
   reads exact keys, modded ones included, off the items for you.
2. A new name like `Explosives` routes to `[SHEPHERD:Explosives]` and gets its own row on the status panel.
3. A recategorized fuel still feeds its machines first: with `Ore/Ice=Ice`, generators fill before
   `[SHEPHERD:Ice]`.
4. Run `configReload` after editing. Delete a line to revert the item to its built-in category.

---

## Displays

Name any block with a screen. All are optional. Only the first `[SHEPHERD]`, `[SHEPHERD-LOG]` and
`[SHEPHERD-DEBUG]` found are used; levels and stock screens can be as many as you like.

1. **`[SHEPHERD]` — status.** Version, health (`working` / `idle` / `learning` / `standing by` /
   `HALTED`), the programmable block drawing it, a ticking clock, how many tagged assemblers it controls — those
   switched on and functional, none while the tags have a problem (`3 Assemblers Controlled`, plus
   `, one disassembling` while one is lent to disassembly) — and a fill bar per category (`──catch-all──` when only
   a catch-all takes it, `──no route──` when nothing does), for the catch-all and per reserve pool. Below the rule:
   what production is doing and every problem that needs you. **Nothing below the rule = nothing to do.**
   ```
   crafting Component/SteelPlate +70                     ← doing it
   disassembling Component/SteelPlate 400                ← doing it
   refining Ore/Iron for Ingot/Iron                      ← doing it
   learn Ore/Modium: measuring                           ← learning
   craft Component/ModAlloy: no blueprint                ← blocked
   refine Ingot/Iron: output has no room                 ← blocked
   craft Component/SteelPlate: disassembly queued        ← waiting for the opposite order
   learn Component/ModAlloy: queue some alone on an assembler   ← needs you
   can't reach Refinery for Ores - check conveyor/sorter        ← needs you
   reserves Ammo and Ship both want All of Ingot/Iron - only one fills   ← needs you
   clearing stalled disassembly queue on Assembler 3     ← fixing it itself
   ```
2. **`[SHEPHERD-LOG]` — history.** The same lines timestamped, except the "doing it" ones, each logged once when it appears, plus
   events (started, learned a recipe, stood down, bad config line). A few diagnostics are log-only, such as
   a category with no container (the status panel already shows `──no route──`).
3. **`[SHEPHERD-DEBUG]` — run times.** Peak and rolling-average run time against the server's limits,
   estimated sim speed, block counts and a per-tick-kind table — where you check server-legality.
   1. `U1 4% · U10 95% · U100 1%` — time spent at each speed. `U1` (every tick) while items queue up, after
      Check Code, during a bottle sweep and in learn mode; `U10` when caught up or slowed after a long run or
      a sim dip; `U100` when holding back for the server, for a few seconds after a run over `spikeMs`, standing by or `HALTED`.
   2. `THROTTLED · U10` in the header has two causes, counted apart: `thr xN P%` (a run over the per-run
      limit) and `wary xN P%` (the server's sim speed dipped).
   3. `over N` on each row counts every run over `peakMs`, including ones a garbage collection landed on
      (which the limiter ignores), so it reads higher than the limiter's own count.
4. **`[SHEPHERD-RESERVE:Name]` / `[SHEPHERD-RESERVE]` — reserve levels.** One pool per screen: each item
   with what the pool holds and its quota; the header % is held ÷ quota over the numbered quotas (`All` lines
   aren't counted). Amounts from 10,000 are shortened to `k` (`2000k`, never `M`, which is the Min letter). A
   pool with no quotas lists its contents instead — see **Find the exact keys**.
   ```
   Ammo · 68%
   AmmoMagazine
    NATO_5p56x45mm  340/500M
   Ore
    Iron            12.3k/All
   ```
5. **`[SHEPHERD:Ores,Ingots]` — stock.** Every item of those categories on the grid (containers, machine
   inputs, docked ships it unloads) with how many you have. `*` adds what your catch-all containers hold.
   Refreshes once per sort cycle, so on a busy grid it can lag a minute or more.
   ```
   Tools · 3 items
   GasContainerObject
    HydrogenBottle    4
   PhysicalGunObject
    AngleGrinderItem  1
    WelderItem        2
   ```

---

## Settings

All settings live under `[Settings]` in the programmable block's Custom Data, so script updates never
reset them. SHEPHERD adds any missing key with its default at startup and on `configReload` (a block
standing by leaves its Custom Data alone). It never changes a line you wrote; delete a line to get its default
back. Keys are case-sensitive. Run `configReload` after editing.

| Key | Default | Range | What it does |
|---|---|---|---|
| `utcOffsetHours` | `0` | −14 to 14 | Hours added to UTC for the clock and log stamps. |
| `reactorFillPct` | `15` | 0–100 | How full SHEPHERD keeps reactor inputs, % of volume. `0` = don't feed. |
| `gasGenFillPct` | `50` | 0–100 | Same for O2/H2 generators. |
| `refineryFillPct` | `30` | 0–100 | Same for refineries. |
| `bottleSweepMinutes` | `3` | ≥ 0 | How often stored gas bottles are refilled through a gas tank. `0` = never. |
| `sortPassSeconds` | `60` | ≥ 0 | Longest sort pass before a rescan refreshes plans and counts (~8 % throughput cost). `0` = no limit. |
| `priority` | `0` | whole number | Which of several SHEPHERD blocks leads; higher wins. |
| `factionShare` | `false` | | Also manage blocks other players shared with your faction. |
| `dockedProduction` | `false` | | Also feed, harvest and queue a docked ship's refineries and assemblers (crafting only — never disassembly). |
| `dockedFuel` | `false` | | Also top up a docked ship's reactors and generators (never empties them). |
| `peakMs` | `0.8` | 0.001–1000 | A run longer than this backs SHEPHERD off; set it to your server's per-run limit. |
| `parkSim` | `0.75` | 0–0.99 | Park while sim speed keeps dropping below this. `0` = never park. |
| `resumeSim` | `0.8` | up to 1 | Resume once average sim speed is back above this. Must be ≥ `parkSim`, or both revert. |
| `backoffSim` | `0.9` | 0–1 | The `peakMs` back-off applies only below this sim speed. `1` = always, `0` = never. |
| `spikeMs` | `5` | 0.001–1000 | A run longer than this pauses SHEPHERD's heavy work and screens for `spikeHoldSeconds`, at any sim speed. |
| `spikeHoldSeconds` | `6` | ≥ 0 | How long that pause lasts, in game seconds. `0` = no pause. |

**Machines.** A machine below its target pulls fuel or ore ahead of storage, refilled once it has burnt a
quarter of the target; a refinery on a shortage you set up is filled at once. Output is collected every
15 s, or at once when a reserve or machine wants it. SHEPHERD turns off **Use Conveyor System** on every
base machine it feeds and never turns it back on. On a kind set to `0` or a block tagged `[SHEPHERD-Off]`,
switch it back on yourself and it stays on.

**Bottles.** A script can't read a bottle's fill, so every sweep refills them all, full or not — hence the
long interval.

**Server limits.** The defaults match Destiny's Advanced PB Limiter (0.8 ms per run, enforced below sim
0.7). **Raising `peakMs` or lowering `parkSim`/`backoffSim` lets SHEPHERD run into the limiter's
punishment** — change them only for a server with different limits or a test world. Destiny also punishes
three runs over 10 ms within 10 s at any sim speed; a 6 s pause after every run over 5 ms keeps any 10 s to at most
two such runs, so raise `spikeMs` or shorten `spikeHoldSeconds` with the same care. That rule applies from the
first runs after a grid loads, which are the slow ones, so SHEPHERD's first cycle after a start takes a minute or two.
A `spikeGraceSeconds` line left by v1.0.3–v1.0.4 now logs `unknown setting`; delete it.

**Mistakes are reported, never guessed.** An unknown key logs `unknown setting '<key>'`; a bad or
out-of-range value logs `bad setting '<key>=<value>'` and keeps the default. `configReload` shows what was
read in the programmable block's detail pane (a toggle missing from the `priority` line is off):

```
reloaded 2 pools, 14 quotas, 3 overrides, 0 facts
priority 0, dockedProduction
peakMs 0.8, parkSim 0.75, resumeSim 0.8, backoffSim 0.9
spikeMs 5, spikeHoldSeconds 6
fill % 15/50/30, bottles 3 min, pass 60 s, UTC+1
```

---

## Reserve pools — keep a loadout stocked

A pool is a set of containers kept stocked to quotas: an ammo locker, a component cache. SHEPHERD fills a
pool ahead of ordinary storage, even out of category containers, and trims it where the mode says so.

### 1. Tag the containers

`[SHEPHERD-RESERVE]` joins the default pool, `[SHEPHERD-RESERVE:Ammo]` the pool `Ammo`. A pool can span
several containers.

1. **Pool names are case-sensitive:** `[SHEPHERD-RESERVE:Ammo]` pairs with `[Reserve:Ammo]`, not
   `[Reserve:ammo]`. A mismatch shows as `reserve Ammo has no quota section` plus `reserve ammo quotas unused -
   no container` (not shown for a section of only `A` lines, which needs no container — see below).
2. **A pool without quotas is left alone** — tagging never empties a container (the bottle sweep still
   refills its gas bottles, which come back to it).
3. **A pool with quotas holds only what they name:** anything else in it moves out to its category.
4. A block with both a reserve tag and a `[SHEPHERD:…]` tag is reserve-only, and the status panel says so.

### 2. Write the quotas

In the programmable block's Custom Data, one section per pool (`[Reserve]` for the default pool):

```ini
[Reserve:Ammo]
AmmoMagazine/NATO_5p56x45mm=2000
AmmoMagazine/Missile200mm=500M

[Reserve]
Component/SteelPlate=All
Ore/Stone=5000L
```

| Amount | Mode | Pulls in | Trims |
|---|---|---|---|
| `500` | Exact | up to 500 | above 500 |
| `500M` | Min | up to 500 | never |
| `500L` | Limit | up to 500 | above 500 |
| `All` | All | everything | never |

`L` moves like a plain amount; it differs only with `A` (below). Letters are case-insensitive; the item key is not. Fractions use `.` (`12.5`). A malformed line is skipped
and logged. Add `docked=true` to a ship's pool so its missing containers aren't flagged on the status panel
while the ship is away (the log still notes them). **Two pools can't both `All` the same item** — only one fills, and the status panel warns.

### 3. Automatic production — the `A` flag

A trailing `A` lets SHEPHERD make the difference. Crafting needs an assembler tagged `[SHEPHERD-Main]`
(see **Assembler modes**).

| Amount | Makes |
|---|---|
| `500MA` | Assembles (or refines, for ingots) the shortfall below 500. |
| `500LA` | Disassembles the true grid-wide surplus above 500. |
| `500A` | Both. |

1. Fuel and ore already loaded into reactors, generators and refineries are spoken for; everything else counts,
   assembler contents and machine output included. Since the count is grid-wide, a pool made only of `A` lines
   needs no container: a `[Reserve:Quotas]` section of crafting targets works on its own.
2. Ingots are refined, never disassembled (`500LA` on an ingot acts as `500L`). Ore can't be made. `AllA`
   is invalid.
3. **Assembling adds to queues and never takes away your units** — SHEPHERD never clears or reorders queues.
   When a shortfall gets covered another way (a docked ship unloads, you top up by hand), it takes back as many
   units as it queued itself once two cycles in a row show them unneeded: from the newest entry of that item
   (even one you queued later, so your own total stays the same), never the unit being built. It forgets which crafting units were its own when the programmable block recompiles,
   so after that it trims only what it queues next; its own disassembly is always trimmed.
   A shortfall is queued on the `Main` assembler; its `Coop` assemblers share the work through the game's
   cooperative mode. When several assemblers take orders (a docked ship's `Main` with `dockedProduction=true`,
   a Survival Kit tagged `[SHEPHERD-Coop]`), it's split by speed (the Advanced Assembler Unit from TDU Server Mod Pack v2 5×,
   Basic Assembler 0.7×, Survival Kit 0.3×, plus speed modules) and by what's already queued — the Coops that are
   on count as one pool shared by every Main, since each takes work from whichever Main it reaches. A share under 5
   units is folded into the others.
4. **Disassembly** runs on one assembler at a time, recorded in a managed `[Production]` section and switched
   back to Assembly once its queue empties. It takes the slowest base `Coop` that can make the item, so the
   `Main` and the other Coops keep crafting: SHEPHERD turns that Coop's Cooperative mode off, lets it finish
   the few seconds of work it holds, then switches it to Disassembly; Cooperative mode goes back on afterwards.
   With no such Coop it takes the fastest empty base assembler that takes orders — the `Main`, or a Survival Kit
   tagged `Coop` — and a disassembling `Main` crafts nothing (`craft X: Main assembler disassembling`). A docked ship's assembler never disassembles: the ship could leave
   with the order and take apart its own cargo. An assembler you set to Disassembly yourself is never used.
   If the surplus shrinks while it runs (you used some of the items), the extra is trimmed the same way. If
   the disassembly queue makes no progress for about 2 minutes (its surplus vanished), SHEPHERD clears it —
   the only queue it ever clears.
5. Assembly and disassembly of one item never run at once (`craft X: disassembly queued`,
   `disassemble X: assembly queued`); the held one starts when the other finishes.
6. **Refining:** for an ingot shortfall SHEPHERD picks the best ore (Stone counts toward all four of its
   outputs) and steers one refinery to only that ore, capped at the need and the ore available; if that ore
   runs short, the next-best ore gets a refinery of its own. Your other refineries refine whatever arrives.
7. A modded item or ore is learned the first time an `A` quota needs it — see **Blueprints and ore
   yields**.

### 4. Find the exact keys

1. Tag a container for a pool with no quotas yet (`[SHEPHERD-RESERVE:Missiles]`) and drop one of each
   item in.
2. Tag an LCD the same. It lists the contents by type:
   ```
   Missiles · no quotas
   AmmoMagazine
    NATO_5p56x45mm  40
   ```
3. Key = type line + `/` + item name: `AmmoMagazine/NATO_5p56x45mm`.
4. Write the lines into `[Reserve:Missiles]` and `configReload`. Items you left out now move out of the pool.

Or let **Learn mode** write the lines from what the containers hold.

---

## Assembler modes — where SHEPHERD crafts

SHEPHERD crafts only on assemblers you tag:

| Tag in the assembler's name | SHEPHERD | Cooperative mode |
|---|---|---|
| `[SHEPHERD-Main]` | queues orders, tests modded blueprints, disassembles when there's no Coop | switched off |
| `[SHEPHERD-Coop]` | queues nothing — the assembler helps its Main; one may be lent to disassembly | switched on (off while lent) |
| no tag | never queues on it | untouched |

1. **Tag one Main per base or ship**, and Coop on the assemblers that should help it. Untagged assemblers
   stay yours: their queues still count, so nothing is made twice.
2. On the base SHEPHERD sets Cooperative mode to match the tag (not while the tags have a problem, such as two
   Mains); a docked ship's switch is never changed,
   and one that doesn't match is reported instead. A base Coop found in Disassembly is switched back to
   Assembly once its queue is empty — the game lets a Coop help only in Assembly mode.
3. A Survival Kit can't cooperate: tagged Coop, it takes orders like a Main.
4. Output is collected from every base assembler, tagged or not, and from a docked ship's with
   `dockedProduction=true`.
5. Sacrificial Stockpile Manager and BaR Maid use their own marks (`[TIM]` in Custom Data), which SHEPHERD
   doesn't read. If you run one of them too, give it different assemblers.

| Message | Meaning |
|---|---|
| `assemblers: several Main - A, B` | Keep one `Main`; nothing new is crafted there until then. |
| `assemblers: no Main among Coop A, B` | Tag one of them `[SHEPHERD-Main]` instead. |
| `A: cooperative switch doesn't match Coop` | A docked ship's switch — set it on the ship. |
| `craft X: no Main assembler` | No assembler is tagged `[SHEPHERD-Main]`. |
| `craft X: Main assembler off` | Turn it on. |
| `craft X: Main assembler disassembling` | Waits for the disassembly to finish. |
| `craft X: no assembler` | The `Main` can't make X. |
| `disassemble X: no free assembler` | The surplus waits until the base `Main` is empty or a `Coop` is switched on. |
| `disassemble X: …` (other reasons) | Same meaning as the `craft X: …` line with that reason. |

---

## Learn mode — teach by placing items

1. Run the programmable block with **`learn`**. Sorting stops so what you place stays put.
2. Arrange items:
   1. **Categories:** put an item in a container tagged with exactly one category (`[SHEPHERD:Ice]` creates
      a custom one). Putting it back in its built-in category's container removes the override.
   2. **Loadouts:** fill a pool's reserve containers with exactly what it should hold (docked ships count).
3. The status panel previews what will be saved, refreshed every few seconds:
   ```
   learn Ore/Ice -> Ice
   learn Ore/Stone -> built-in
   learn Reserve:Ship Component/SteelPlate=2000
   conflict Component/Explosives: Components, Explosives
   ```
4. Run **`learn`** again to save and resume (`learn saved 2 categories, 1 reserve lines` in the log).

Rules:

1. Categories are taught only by containers on your own grid with exactly one category; a catch-all or
   several-category container is skipped with a log note. Reserve containers teach loadouts on docked ships too.
2. An item found in two categories is a conflict and isn't saved — move all of it to one place.
3. A new reserve item gets a plain line (`=2000`, exact); an existing line keeps its `M`/`L`/`A` letters;
   `All` lines are left alone. A pool taught only from docked ships gets `docked=true`.
4. Nothing is removed for being missing — delete lines by hand.
5. Nothing is saved until the second `learn`; a recompile discards the preview (your items stay where they
   are).
6. Conveyors keep running — switch off drills and collectors that feed teaching containers.
7. Saving rewrites `[Categories]` (comments in it are lost); other Custom Data is untouched.
8. With several SHEPHERD blocks, run it on the leader.

---

## Blueprints and ore yields

A programmable block can't look up recipes: it can only ask an assembler whether it can use a blueprint of
a given name, queue it, and watch what comes out. Blueprint names often differ from the item's
(`Component/Construction` is made by `ConstructionComponent`).

1. **Vanilla needs nothing** — every vanilla blueprint and ore yield is built in.
2. **Modded items and ores are learned once, only when an `A` quota needs them,** and saved to Custom Data:
   ```ini
   [Recipes]
   Component/ModAlloy=ModAlloyBlueprint

   [Yields]
   Ore/Modium=Ingot/Modium:0.25,Ingot/Iron:0.1
   ```
   Delete a line and `configReload` to have it relearned.
3. **Ingredients stay invisible:** SHEPHERD can't tell what a modded component consumes — give its ingots
   `A` quotas too.

**Blueprints:** if an assembler that takes orders (the `Main`) is idle and empty at that moment, SHEPHERD tries
likely names on it. If none work, or no assembler was free, the status panel asks you to queue the item — **alone on an assembler, as a batch** (a mixed queue can't be attributed; a single
unit may finish before SHEPHERD looks).

**Ore yields:** measured only while an ingot shortfall is blocked, no known ore serves it, and at least 110
of the unknown ore is available. SHEPHERD isolates one refinery, feeds it only that ore, and records the
outputs after 100 units. **Leave that refinery alone while a `learn Ore/X: …` line shows** — interference
aborts the sample (`learn Ore/X: aborted - refinery disturbed`) and it retries, but detection isn't
perfect, so sanity-check a new `[Yields]` line.

---

## Docked ships

SHEPHERD manages its own grid (rotors, pistons, hinges included) and **your** ships docked by connector,
chains included.

1. **Unloaded:** cargo, cockpits, connectors, drills, grinders, welders and collectors are emptied into the
   base — everything except reserve stock and `[SHEPHERD-Off]` blocks. A docked container's `[SHEPHERD:…]`
   tag is ignored while docked.
2. **Resupplied:** docked reserve containers are filled to their pool's quotas from the **base's** Custom
   Data, so a ship needs no SHEPHERD of its own. Pool names are shared across the docked group — give ship
   loadouts their own names. A ship pool with no section on the base is left alone.
3. **Left alone:** gas tanks; reactors and generators unless `dockedFuel=true`; refineries and assemblers
   unless `dockedProduction=true` (the ship then needs its own `Main` — **Assembler modes**). A docked
   block's Use Conveyor System and Cooperative mode are never changed.
4. **Never touched:** other players' blocks, unless shared with your faction and `factionShare=true`.
5. **`[SHEPHERD-Split]`** on a connector cuts the group there, both ways — for a ship you want left alone,
   or a huge grid you want kept out of the base's work.

Docking and undocking are picked up within seconds (`docking changed - rescanning` in the log).

---

## Several SHEPHERD programmable blocks

Among one owner's SHEPHERD blocks on a ship or docked group, one leads and the rest **stand by** (move
nothing; commands other than `learn` still work). A docked ship hands over to the base and takes over again ~10 s after
undocking. A block started while docked runs one scan before it stands by. Another player's SHEPHERD, or one beyond a `[SHEPHERD-Split]`, runs on its own.

Who leads: highest `priority`, then station over ship, then large grid over small, then a fixed pick. A
standing-by block takes over 10–15 s after the leader goes away. The log records each change (`standing by
- <block> on <grid> leads`, `resuming - no higher-priority SHEPHERD`). A standing-by ship's `[SHEPHERD]`
panel shows `standing by` and the leader's name.

If a block stops leading while it has an assembler in Disassembly, the new leader leaves a `Main` there —
switch it back by hand if needed. A base `Coop` goes back to Assembly once its queue is empty, with
Cooperative mode on.

---

## Commands

Run the programmable block with one of these arguments (case-sensitive):

| Argument | Does |
|---|---|
| `configReload` | Re-reads Custom Data (settings, quotas, categories, learned facts) and shows what it read. Needed after every edit. |
| `learn` | Enters learn mode; run again to save and resume. |
| `statsReset` | Restarts the `[SHEPHERD-DEBUG]` measurement window. |

`[SHEPHERD-DEBUG]` rows: `ftch` block sweep · `scan` block classify · `bld` sort-plan build · `xfer` item
transfer · `plan` production plan · `save` writing Custom Data · `cmd` these commands · `othr` cheap ticks.
To check server-legality: let the grid settle, `statsReset`, wait a while, read `peak` and each row's
`max`.

---

## Coming from Isy's Inventory Manager or GOAT Sorter

SHEPHERD reads only `[SHEPHERD…]` tags; old keywords in names do nothing. **Never run the old script and
SHEPHERD on the same grid** — load SHEPHERD into the old programmable block or turn the old one off.

| What you want | Isy's Inventory Manager | GOAT Sorter | SHEPHERD |
|---|---|---|---|
| Category container | `Ores`, `Ingots`, … in the name | category name in the name | `[SHEPHERD:Ores,Ingots]` |
| Overflow | auto-assigned | `Misc` | `[SHEPHERD:*]` |
| Fill first | `[P1]` (lower first) | `[P:5]` (higher first) | `[SHEPHERD:Ores:#5]` (higher first) |
| Don't touch | `Locked`, `!manual` | `[Locked]`, `[Manual]` | `[SHEPHERD-Off]` |
| Kept-stocked container | `Special` | `[Stock]` | `[SHEPHERD-RESERVE:Name]` |
| Ignore a docked grid | `[No IIM]` on a connector | `[No GOAT]` on a connector | `[SHEPHERD-Split]` on a connector |
| Status / log / performance | `IIM-main`, `IIM-actions`, `IIM-performance` | `[StatusScreen]`, `[LogScreen]` | `[SHEPHERD]`, `[SHEPHERD-LOG]`, `[SHEPHERD-DEBUG]` |
| Stock screen | `IIM-inventory` | `[Inventory]` | `[SHEPHERD:Ores]` or `[SHEPHERD-RESERVE:Name]` on a screen |

| Wanted amount 5000 … | Isy's Inventory Manager | GOAT Sorter | SHEPHERD quota line |
|---|---|---|---|
| assemble up to it | `5000A` | `/5000A` | `Component/SteelPlate=5000MA` |
| disassemble above it | `5000D` | `/5000D` | `Component/SteelPlate=5000LA` |
| both | `5000` with disassembling on | `/5000AD` | `Component/SteelPlate=5000A` |

1. **Priority runs opposite to IIM:** rename `[P1]` … `[P3]` to `:#3` … `:#1`.
2. **Categories:** bottles are `Tools`, GOAT's `Misc` is `Others`. For a separate bottle, ice or food
   container, add a custom category (or use **Learn mode**).
3. **Stock containers → reserve pools:** quotas go in the **programmable block's** Custom Data, one section
   per pool, with full keys (`Component/SteelPlate=100`). Unlike theirs, an `L` amount also pulls up to the
   amount. Quickest: tag the old containers as reserves with
   their contents inside and run `learn` twice.
4. **Autocrafting:** no screen — an `A` quota line and one assembler tagged `[SHEPHERD-Main]` (**Assembler
   modes**). Your own queue entries are never touched; modded recipes are learned automatically (no switch — `learn` is for
   categories and loadouts).
5. **Machines:** filled to a % of input volume (`reactorFillPct`, …) instead of fixed amounts; fed
   machines' Use Conveyor System is turned off, as in GOAT.
6. **Settings** live in Custom Data, so updates don't reset them.
7. **Not in SHEPHERD:** internal sorting (never — it desyncs servers); automatic container assignment and
   balancing (maybe later); item renaming; container-level screens; GOAT's pins and stock-group balance
   methods; per-item crafting priority, yield and hide flags.

---

## Notes

1. **Routing keeps no saved state** — it is re-derived from block names and item types every cycle. Custom
   Data is read only at startup and on `configReload`; SHEPHERD writes only its managed `[Categories]`,
   `[Production]`, `[Recipes]` and `[Yields]` sections, missing `[Settings]` keys and learned `[Reserve…]` lines,
   keeping your other lines and comments.
2. **Weapons are left alone** — they pull their own ammo. A modded weapon built on a conveyor-sorter block
   isn't recognised as one; tag it `[SHEPHERD-Off]`.
3. **Switched-off or broken machines get nothing**, and an off assembler's queue doesn't count, so its
   shortfall goes to another assembler taking orders — or, if it's the `Main`, waits
   (`craft X: Main assembler off`). A machine without power counts as on.
4. **Grid changes are noticed within seconds** — a large chunk undocked, deleted or ground down triggers a
   rescan (`scope shrank 526 to 76 - rescanning`).
5. Built and tested on the **Destiny Universe** server's mod set (WeaponCore and Defense Shields included).

---

## License

Copyright (C) 2026 Tomasz Dobrowolski.

SHEPHERD is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full
text (SPDX: `GPL-3.0-or-later`).

The script on the Steam Workshop and mod.io is a minified build of this source. Anyone who republishes it, or a
modified version, must point to its full source as well.

Versions 1.0.0 to 1.0.3 were released under the MIT license and remain available under it.
