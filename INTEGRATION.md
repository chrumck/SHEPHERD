# SHEPHERD integration contract

What another program — a client plugin, a mod, another script — may rely on when it works alongside SHEPHERD. It has
two parts:

1. **Part A is promised.** Each item there changes only under the change rule at its end. It is almost all the same
   grammar players already use: tags, settings, Custom Data sections, commands. Plus the status panel's first three
   lines and its health words.
2. **Part B is informative.** It describes how SHEPHERD behaves today, so a consumer can make sense of what it sees,
   but any release may change it without notice. Don't build on it. Part B covers which PB leads and why, redraw
   timing, startup behaviour, and the panel's message section.

Anything in neither part (the Echo pane, the `[SHEPHERD-DEBUG]`, `[SHEPHERD-LOG]`, levels and stock screens, log
wording) can also change in any release.

Sample files for every health word and for the default settings are in `SHEPHERD.Tests/Fixtures/` (section A7).

Known consumers:

1. **Sacrificial Stockpile Manager (SSM)**, a client plugin by scotlandsyard. Its side of the contract is
   `Projects/Sacrificial Stockpile Manager/INTEGRATION.md` in
   [scotlandsyard/SpaceEngineersPlugins](https://github.com/scotlandsyard/SpaceEngineersPlugins).

---

# Part A — promised

## A1. Detecting SHEPHERD

Either of these marks SHEPHERD:

1. **A programmable block** whose Custom Data has a `[Settings]` section with the key `refineryFillPct`, and whose
   script text contains `SHEPHERD` where the reader has the script text (the packed script's header comment names
   it). SHEPHERD adds any missing `[Settings]` key on its first save after a start or a `configReload`, so a newly
   built PB has it within seconds of its first run.
2. **A `[SHEPHERD]` status panel** whose line 1 has the shape in A3 (a block with a screen: a light with the tag
   is a status light, which has no text).

The PB must also be on, built and powered for SHEPHERD to run. Whether it is doing its jobs is told by the status
panel (A3), not by the PB.

---

## A2. Job switches — the `[Settings]` section

SHEPHERD reads `[Settings]` at startup and on `configReload` only. These keys turn SHEPHERD's jobs off, so another
program can take those jobs over:

| Key | Default | Range | Meaning |
|---|---|---|---|
| `reactorFillPct` | 15 | 0–100 | Reactors are filled to this % of their volume. **0: SHEPHERD feeds no reactor** and leaves each one's conveyor setting alone. |
| `gasGenFillPct` | 50 | 0–100 | The same for O2/H2 generators. **0: none fed.** |
| `refineryFillPct` | 30 | 0–100 | The same for refineries. **0: no refinery is fed at all** — no greedy feeding, no refining for ingot quotas, no yield learning. |
| `bottleSweepMinutes` | 3 | 0 or more | Minutes between bottle-refill sweeps. **0: SHEPHERD refills no bottles.** |
| `dockedFuel` | false | | Also top up reactors and gas generators of docked grids. |
| `dockedProduction` | false | | Also feed and queue on refineries and assemblers of docked grids. |

With a fill % above 0, SHEPHERD turns off the conveyor auto-pull of each such machine on its own grid, so its fill
amount holds.

**How a value is read.** A program reading these must read them the same way, or the two disagree about who does a
job:

1. **Keys are case-sensitive.** `RefineryFillPct` is not `refineryFillPct`: it is logged as `unknown setting`, and the
   default applies.
2. **A trailing comment is cut off.** MyIni returns `refineryFillPct=0 ; note` with the comment as part of the value;
   SHEPHERD cuts the value at the first `;` and trims it.
3. **A number counts only when it parses** (`.` as the decimal mark) **and is in range.** Otherwise the default
   applies, so a typo never switches something off.
4. **Booleans** are `true` / `false` in any case. Anything else reads `false`.

The other `[Settings]` keys are SHEPHERD's own; `Fixtures/settings-defaults.ini` lists them all as SHEPHERD writes
them into an empty Custom Data.

---

## A3. The `[SHEPHERD]` status panel

The panel is one text block, drawn with `WriteText` in the Monospace font. Treat `\r\n` and `\n` alike, in the panel
text and in the sample files.

1. **Line 1** is `SHEPHERD <version> · <health word>`. The version is `MAJOR.MINOR.PATCH`, digits only, no `v`. The
   separator is ` · ` (U+00B7 with a space each side).
2. **Line 2** is the name of the PB that drew the panel, as the game shows it. Names repeat (two PBs can both be
   "Programmable Block").
3. **Line 3** is a clock, `HH:mm:ss`, written on every redraw: two redraws in different seconds always differ. A
   panel whose clock stops moving is no longer being redrawn.
4. **Health words:**

   | Word | Meaning | SHEPHERD is doing its jobs |
   |---|---|---|
   | `working` | Running, and moving items or busy with its first cycle after a start. | yes |
   | `idle` | Running; nothing to move. | yes |
   | `learning` | Learn mode: deliberately moves nothing while the player teaches it. Moving items then would undo the teaching. | yes — leave its jobs alone |
   | `HALTED` | Its crash breaker has opened: it does no work, and resumes by itself once a step succeeds. | **no** |
   | `standing by` | Another SHEPHERD PB is doing the work. | **another PB is** — never read this as "SHEPHERD is not running" |

5. **The rest of the panel** — the rows below line 3, its rules and its messages — is informative only (B3).

**Reading it.** SHEPHERD is doing its jobs on a grid when a `[SHEPHERD]` panel there has a moving clock and a
`working`, `idle` or `learning` health word. A reader needs nothing else: which PB leads and why is SHEPHERD's own
business (B1).

---

## A4. Tags and scope

The keyword `SHEPHERD` and the words after `-` are case-insensitive; category tokens and pool names are case-sensitive.
A tag ends at the first `]`.

| Tag | On a block with an inventory | On a block without one |
|---|---|---|
| `[SHEPHERD:Cat1,Cat2]` | a destination for those categories | a stock screen |
| `[SHEPHERD:*]` | a catch-all destination | a stock screen |
| `…:#N` at the end, e.g. `[SHEPHERD:Ores:#5]` | priority: higher fills first; default 0, negatives allowed | |
| `[SHEPHERD-RESERVE]`, `[SHEPHERD-RESERVE:Name]` | a reserve container, default or named pool | that pool's levels screen |
| `[SHEPHERD-Main]`, `[SHEPHERD-Coop]` | the assembler SHEPHERD crafts on, and its cooperators | |
| `[SHEPHERD-Off]` | never touched | |
| `[SHEPHERD-Split]` | on a connector: SHEPHERD's reach ends there | |
| `[SHEPHERD]`, `[SHEPHERD-DEBUG]`, `[SHEPHERD-LOG]` | | the status, profiler and log screens; `[SHEPHERD]` on a light makes a status light, never the status panel |

1. **Categories:** the built-ins are `Ores`, `Ingots`, `Components`, `Tools`, `Ammo`, `Consumables`, `Others`. Any
   other token is a custom category. A category token must not contain `[`, `]` or `,`, nor end in `:#` and a number;
   a pool name must not contain `[` or `]`.
2. **Reach:** SHEPHERD manages its own construct and every construct joined to it over connected connectors, except
   past a connector tagged `[SHEPHERD-Split]` (either end of the pair).
3. **Never managed:** weapons and parachute hatches (they pull their own ammo and canvas), and blocks tagged
   `[SHEPHERD-Off]`. Gas tanks are used only to refill bottles.
4. **Docked grids:** their cargo is unloaded home and their `[SHEPHERD-RESERVE…]` pools are filled; their
   `[SHEPHERD:…]` tags are ignored. Their machines follow `dockedFuel` and `dockedProduction` (A2).
5. **Assemblers:** SHEPHERD queues only on `[SHEPHERD-Main]` (one per construct) and `[SHEPHERD-Coop]` assemblers,
   never on an untagged one. On its own construct it sets each tagged assembler's cooperative switch to match.

---

## A5. Custom Data that other programs may write

SHEPHERD reads these at startup and on `configReload`. A program that writes them runs `configReload` afterwards
(which needs the player to own the PB).

### A5.1 Reserve quotas — `[Reserve]` and `[Reserve:Name]`

One section per pool: `[Reserve]` is the default pool, `[Reserve:Name]` the pool named `Name` (exactly as in its
tag). One line per item:

1. **Key:** `TypeId/SubtypeId`, e.g. `Component/SteelPlate`, `Ingot/Iron`. A `MyObjectBuilder_` prefix is accepted
   and stripped. Item keys are case-sensitive.
2. **Value:** `<amount>`, `<amount>M` or `<amount>L`, each optionally followed by `A`; or `All`. Letters are
   case-insensitive; the amount is a plain non-negative number. `AllA`, a reordered or repeated letter, a negative or
   anything else is logged as `bad quota` and ignored.
   1. `<amount>` keeps exactly that many in the pool. `M` keeps at least that many; its surplus leaves only to fill
      another pool's shortfall of the item. `L` pulls up to that many and removes any above, like a plain amount.
   2. `A` lets SHEPHERD **produce** toward the amount: craft it, or for an ingot, refine it. With `A`, a plain amount
      also disassembles surplus above it, and `L` disassembles surplus but never crafts. `M` with `A` crafts but never
      disassembles. `A` does nothing on ore, nor with `L` on an ingot (ingots are never disassembled); both show a
      standing state.
   3. `All` gathers every unit of the item into the pool.
3. **`docked=true`** is a pool setting, not an item: the pool lives on a docked ship.
4. **Quotas add up across pools.** For each item, production aims at the sum of every pool's plain and `M` amounts,
   making at most the sum of those that carry `A`, and disassembly holds back the sum of every pool's amounts (`L`
   included).
5. A pool whose every line has `A` needs no container: it only drives production.
6. **Production needs a `[SHEPHERD-Main]`** on the construct; without one, `A` lines are held and the panel says so.

SHEPHERD writes into a `[Reserve…]` section only when a player leaves learn mode, and then only the lines it learned.

### A5.2 Custom categories — `[Categories]`

One line per item: `TypeId/SubtypeId=<category>`. The item then routes **only** to the containers of that category
(then the catch-all), not to its built-in category's containers. Machines still take it as fuel or ore.

SHEPHERD rewrites `[Categories]` as a whole when a player leaves learn mode, and never otherwise.

### A5.3 `[Settings]`

A2. SHEPHERD only adds missing keys, with their defaults, and never changes a line already there.

### A5.4 SHEPHERD's own sections

`[Production]`, `[Recipes]` and `[Yields]` are SHEPHERD's memory. It rewrites them as a whole whenever it saves.
Another program must not write them.

---

## A6. Commands

Run with the PB's Run action and an argument (the player must own the PB):

1. `configReload` — re-read Custom Data. A save SHEPHERD still had pending is written first.
2. `learn` — enter learn mode; run it again to save and leave.
3. `statsReset` — zero the `[SHEPHERD-DEBUG]` figures.

---

## A7. Sample files and the contract test

`SHEPHERD.Tests/Fixtures/`:

| File | What it is |
|---|---|
| `status-working.txt` | The status panel while `working`, with a reserve pool and a standing state. |
| `status-idle.txt` | The same grid, `idle`. |
| `status-learning.txt` | The same grid in learn mode, with its preview line. |
| `status-halted.txt` | The same grid after the crash breaker opened. |
| `status-standing-by.txt` | A docked shuttle's PB standing by while the base PB leads. |
| `settings-defaults.ini` | The Custom Data SHEPHERD writes into an empty one. |

1. `IntegrationContractTests` runs the real script against a mocked grid (`ProgramHarness.cs`) and compares each
   panel with its sample. Line 1's version and line 3's clock are masked, so a sample changes only when what is on
   the panel changes.
2. The samples show whole panels, Part B content included. Only what Part A describes is promised: lines 1–3 and the
   health word, and in `settings-defaults.ini` the A2 keys.
3. A consumer's own tests can parse these files, pinned to a SHEPHERD release tag (`vX.Y.Z`), and move the pin when
   SHEPHERD releases.

---

## A8. Change rule

1. A change to anything in Part A is announced before the release that makes it, as an issue labelled `integration`
   on each known consumer's repository.
2. It ships with a version bump and an **Integration** heading in the release notes.
3. This file, the sample files and the code change in the same commit.
4. People approve every post to another project's repository. No agent pushes to it.

Part B changes need none of this. Where one would visibly change what a consumer sees, the release notes say so.

---

# Part B — informative, may change

## B1. Which PB leads

Several SHEPHERD PBs within each other's reach elect one leader; the rest stand by. Today:

1. Higher `[Settings] priority` wins, then a static grid over a ship, then a large grid over a small one. Only PBs with
   the same owner take part, so a faction-mate's SHEPHERD leads for its own owner, alongside.
2. A `HALTED` PB drops below every healthy one, so a healthy peer takes over within seconds; alone, it still leads.
3. A newly started PB that is outranked finishes its first cycle's plan (moving and queuing nothing) before it stands
   by.
4. The PBs coordinate over the IGC tag `SHEPHERD`. Another program must not broadcast on it.

## B2. Which panel is drawn

1. A PB draws only the **first** `[SHEPHERD]` panel on its own construct (its grid and everything joined to it by
   rotors, hinges and pistons), in the order the game lists blocks. Any other `[SHEPHERD]` panel there is not
   redrawn and keeps whatever it last showed. A light named `[SHEPHERD]` is not a panel: it neither counts as the
   first nor stops a panel after it from being drawn.
2. The leader draws its construct's panel. A standing-by PB draws its own panel only when the leader is on another
   construct, ending with a line naming the leader: `<PB name> on <grid name> leads`. One whose leader shares its
   construct draws nothing.

## B3. Redraw timing and the rest of the panel

1. Once running, SHEPHERD redraws one screen per run, taking turns: status, `-DEBUG`, `-LOG`, then each levels and
   each stock screen. It skips the redraw on a run that does a transfer or another heavy call, and during a spike hold
   (up to ~10 s). At its slowest wake rate a run comes every ~1.7 s, so a full turn takes about 1.7 s × the number of
   screens.
2. After a start (a compile, a server restart, or the grid loading at its owner's login), the first runs are slow and
   spike holds are common. Until every screen has drawn once, a run with time to spare draws several. Until the first
   cycle completes — under a minute — the panel can go 10 s or more without a redraw.
3. Below line 3: an optional setup line, one row per category, a rule (25 × U+2500), one row per reserve pool (with
   its own rule, only when pools exist), then the last rule and the messages.
4. Messages, in order: learn mode's preview (lines starting with `learn `, only while `learning`), production actions
   (starting with `crafting `, `disassembling ` or `refining `), then standing states, which stay while their
   condition holds. Discovery states also start with `learn ` (`learn X: probing`).
