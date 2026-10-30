using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    // SHEPHERD — a server-legal inventory sorter. The Pacer-driven loop paces the SortJob to ONE transfer per tick
    // and renders its LCD surfaces ([SHEPHERD] status, [SHEPHERD-DEBUG] profiler, [SHEPHERD-LOG] log, the
    // [SHEPHERD-RESERVE] levels panels) one panel per tick.
    public partial class Program : MyGridProgram
    {
        #region mdk preserve
        // SHEPHERD inventory sorter. Copyright (C) 2026 Tomasz Dobrowolski. GPL-3.0-or-later, without any
        // warranty. Source and full license: github.com/chrumck/SHEPHERD
        // Setup, tags, and categories: README.md.
        // Settings live in this programmable block's Custom Data, [Settings] section (README "Settings"),
        // so a script update never resets them.
        #endregion

        const string ScriptVersion = "1.0.41";

        // Per-Step() work budget: each item examined AND each source finished draws it down, so one Step() does a
        // bounded slice of the scan regardless of grid size. Higher values push on the average bar as well as
        // shortening the idle full-scan cycle, so retune from both othr avg and othr max.
        const int ExamineBudget = 100;

        // Blocks classified per scan tick. The classify pass is spread across ticks (ClassifyCursor) so one scan
        // run can't grow past the limiter's peak bar with grid size; this bounds each tick. Higher = fewer, heavier
        // scan ticks (nearer the 0.8 ms bar), lower = more, cheaper ticks — retune from the [SHEPHERD-DEBUG] scan max.
        const int ClassifyBudget = 200;
        const double DiscoverYieldFloor = 100.0;
        const double DiscoverYieldSample = 110.0;

        // How often to re-count the grid while a cycle is in flight, and the minimum gap between two scope-trip
        // restarts. The probe is one GetBlocksOfType (measured ~0.03 ms), so the interval is set by how long a
        // stale scope is tolerable, not by cost; the cooldown bounds the worst case of a grid being dismantled
        // block by block, where every probe would otherwise discard the pass that just restarted.
        const double ScopeProbeSeconds = 2.0;
        const double ScopeTripCooldownSeconds = 10.0;

        // A routine back-off is logged only after this long without one: on a large conveyor network transfers sit
        // just over the peak bar every few seconds, and logging each pushed every other message out of the log.
        const double BackoffLogQuietSeconds = 60.0;
        const double StartupLogSeconds = 180.0;    // how long after a start every run over the peak bar is logged
        const double RunGapLogSeconds = 5.0;       // 3x the slowest cadence's gap
        // A start's packed run stops taking more steps past this. The first calls compile for 14–95 ms each in game,
        // and spike pacing allows two long runs per 10.5 s, so a budget under one call gives each its own run.
        const double PackMs = 150.0;
        // How long a start packs its runs and skips the breach back-off: well inside the limiter's per-PB startup
        // allowance (about a minute from a block's first run), so the cold first calls land in it.
        const double DefaultStartupSeconds = 45.0;

        // Moves in one cycle that make it a backlog worth the fast cadence. Below this the moves are a trickle (a
        // running base's harvests and refills) that the idle cadence keeps up with; even at the heaviest measured
        // transfer (~1 ms) this many at Update10 keep the limiter's 50-run average near half its bar, while past
        // it Update1's cheap ticks are what dilute the average.
        const int BacklogMoves = 8;

        // A running machine's output is harvested at most this often (sim-time) unless a stack in it is wanted by a
        // demand target: one transfer moves the whole stack, so waiting turns a move every cycle into one per interval.
        const double HarvestSeconds = 15.0;


        // The Advanced PB Limiter passes this argument (::-1 peak, ::-2 average) right before it would punish us.
        const string GracefulShutDownArg = "GracefulShutDown::";

        const int LogCapacity = 30;

        // Every SHEPHERD PB advertises and listens on this tag, across connectors because a PB manages its docked
        // grids too; a receiver narrows that to the PBs that would manage the same blocks (StandingBy).
        const string LeadChannel = "SHEPHERD";
        const TransmissionDistance LeadRange = TransmissionDistance.ConnectedConstructs;

        // The SHEPHERD-managed Custom Data sections the persistence writer owns: the category overrides
        // and the craft tracking state (the one disassembler SHEPHERD switched, so a recompile can tell it from a
        // user's deliberate Disassembly mode). The user's [Reserve...] sections and any comments are NOT managed —
        // the writer re-parses and leaves them untouched (merge-not-clobber), except that saving learn mode sets the
        // learned keys in [Reserve...] one line at a time.
        const string CategoriesSection = "Categories";
        const string ProductionSection = "Production";
        const string RecipesSection = "Recipes";
        const string YieldsSection = "Yields";
        const string DisassemblerKey = "disassembler";
        // The user's own section: read on load like the reserve quotas. SHEPHERD only adds the keys it lacks, so
        // every setting and its value is listed in Custom Data without a trip to the guide.
        const string SettingsSection = "Settings";
        // In the guide's order, which AddMissing keeps when it appends the keys a section lacks. Each rule is
        // {low, high, default}; priority is any whole number and the three switches are true/false.
        static readonly string[] SettingKeys = ("utcOffsetHours,reactorFillPct,gasGenFillPct,refineryFillPct,bottleSweepMinutes,"
            + "sortPassSeconds,priority,factionShare,dockedProduction,dockedFuel,peakMs,parkSim,resumeSim,backoffSim,spikeMs,"
            + "spikeWindowSeconds,startupSeconds").Split(',');
        const int UtcOffset = 0, ReactorFill = 1, GasGenFill = 2, RefineryFill = 3, BottleSweep = 4, SortPass = 5, Priority = 6,
            FactionShare = 7, DockedProduction = 8, DockedFuel = 9, PeakMs = 10, ParkSim = 11, ResumeSim = 12, BackoffSim = 13,
            SpikeMs = 14, SpikeWindow = 15, Startup = 16;
        static readonly double[] SettingRules =
        {
            -14, 14, 0,
            0, 100, 15,
            0, 100, 50,
            0, 100, 30,
            0, 1e6, 3,
            0, 1e6, 60,
            0, 0, 0,
            0, 0, 0,
            0, 0, 0,
            0, 0, 0,
            0.001, 1000, Pacer.DefaultPeakMs,
            0, 0.99, Pacer.DefaultParkSim,
            0, 1, Pacer.DefaultResumeSim,
            0, 1, Pacer.DefaultBackoffSim,
            0.001, 1000, Pacer.DefaultSpikeMs,
            0, 1e6, Pacer.DefaultSpikeWindowSec,
            0, 1e6, DefaultStartupSeconds
        };

        readonly List<IMyTerminalBlock> blockBuffer = new List<IMyTerminalBlock>();
        // Its own buffer, not blockBuffer's: the classify cursor iterates blockBuffer across ticks, so re-counting
        // into it mid-pass would shift the list under the cursor.
        readonly List<IMyTerminalBlock> scopeBuffer = new List<IMyTerminalBlock>();
        readonly List<IMyShipConnector> connectorBuffer = new List<IMyShipConnector>();
        // The cycle's managed scope, rebuilt at each fetch (and on each heartbeat while standing by, when no fetch runs).
        readonly ManagedScope scope = new ManagedScope();
        // The tripwire's fresh reading, compared against scope — never written into scope, which the in-flight
        // classify reads.
        readonly ManagedScope probeScope = new ManagedScope();
        readonly List<MyInventoryItem> itemBuffer = new List<MyInventoryItem>();
        readonly List<ISortSource> sortSources = new List<ISortSource>();
        readonly List<LedgerKind> ledgerKinds = new List<LedgerKind>();           // parallel to sortSources
        readonly List<ReservePool> countPools = new List<ReservePool>();          // parallel to sortSources
        List<IAssemblerState> assemblerStates = new List<IAssemblerState>();
        // Filled across the build's ticks and swapped in on its last: an observe tick between build steps reads
        // assemblerStates, and a half-filled list would read as a vanished assembler.
        List<IAssemblerState> builtAssemblers = new List<IAssemblerState>();
        int buildCursor, builtSources;
        readonly List<MyProductionItem> productionQueueBuffer = new List<MyProductionItem>();
        readonly Dictionary<string, float> upgradeBuffer = new Dictionary<string, float>();
        readonly ProductionFacts productionFacts = new ProductionFacts();
        // One delegate for every cycle's refinery feeds (a per-cycle method-group conversion would allocate).
        readonly Func<string, bool> openRefineryTakes;
        readonly Func<bool> full;      // the run is past half the instruction cap; every step loop yields then
        // Unknown-yield ores yield learning is waiting on (a live deficit + a request, an under-sampled stock, or
        // the active sample) — withheld from open greedy feeds so the sample can accumulate. Rebuilt each plan;
        // without demand it stays empty and unknown ores (e.g. a modded processor's fuel ore) feed freely.
        readonly HashSet<string> sampleReservedOres = new HashSet<string>();
        readonly ProductionLedger productionLedger = new ProductionLedger();
        readonly LedgerJob ledgerJob;    // pooled like the phase jobs: re-armed per cycle, never reallocated
        readonly CraftPlanner craftPlanner = new CraftPlanner();
        readonly CraftJob craftJob = new CraftJob();
        readonly RefinePlanner refinePlanner = new RefinePlanner();
        readonly DiscoveryCoordinator discovery = new DiscoveryCoordinator();
        readonly BlueprintProbe blueprintProbe = new BlueprintProbe();
        readonly MarkCheck markCheck = new MarkCheck();
        readonly YieldSample yieldSample = new YieldSample();
        readonly Dictionary<string, double> yieldInput = new Dictionary<string, double>();
        readonly Dictionary<string, double> yieldOutput = new Dictionary<string, double>();
        readonly List<IRefineryFeed> refineryFeeds = new List<IRefineryFeed>();   // this cycle's refinery MachineFeeds
        readonly Func<string, bool> ingotRoomProbe;   // the planner's route-has-room stop condition, bound once
        readonly PhaseJob planJob;       // the one cheap production-plan tick between the ledger tally and the sort
        readonly ProductionStatus production;  // the status LCD's production section + its transition log
        readonly List<KeyValuePair<string, string>> productionEntries = new List<KeyValuePair<string, string>>();
        readonly List<KeyValuePair<string, string>> settingsEntries = new List<KeyValuePair<string, string>>();
        // Set by a reload that found keys missing. The fill uses the values that reload read, so only the flush right
        // after it may fill: a later flush (a learned fact) could re-add a line the user has since deleted.
        bool fillSettings;
        RoutingIndex cycleRouting;       // built at the build phase, consumed when the plan tick enqueues the sort
        List<ManagedBlock> managedBlocks;
        long persistedDisassembler;      // the tracking state as last written, so a flush fires only on the edge
        // [Settings], indexed by the constants above. A sort pass's cap trades a rescan's few seconds of phase ticks
        // against how stale re-planning, reserve counts and a due bottle sweep get while a backlog stretches the pass.
        readonly double[] settings = new double[17];
        readonly PeerElection election = new PeerElection();
        readonly IMyBroadcastListener leadListener;
        string leader;                   // the live peer this PB stands by for; null while it does the work
        // The dry cycle has run since compile (QueueDryRun). Until then an outranked PB keeps working instead of
        // standing by: the first run of each path is slow (its methods compile), and paid inside the limiter's startup
        // allowance it is free, while a stand-by would defer it to the resume — an undock, long after the allowance.
        bool warmedUp;
        bool dryFlush;          // the dry cycle's config save is due
        int dryRuns;            // dry cycles queued since compile
        readonly CraftJob dryCraft = new CraftJob { Dry = true };
        readonly List<CraftAction> dryActions = new List<CraftAction>();
        // The stand-by status panel's one message block ("<leader> leads"), refilled only on a lead change.
        readonly List<string> standbyLines = new List<string>();
        readonly List<List<string>> standbyMessages = new List<List<string>>();
        readonly List<ICategoryAcceptor> acceptors = new List<ICategoryAcceptor>();
        readonly Pacer pacer = new Pacer();
        readonly RunStats stats = new RunStats();
        readonly MessageLog messageLog = new MessageLog(LogCapacity);
        readonly StringBuilder renderBuffer = new StringBuilder();

        readonly ClassifyCursor classifyCursor = new ClassifyCursor(); // spreads the classify pass across scan ticks
        readonly CycleDriver driver = new CycleDriver(); // the cycle's job queue: fetch → classify → build → sort → bottles
        readonly PhaseJob fetchJob;      // the three phase jobs are pooled and re-armed per cycle, never reallocated
        readonly PhaseJob classifyJob;
        readonly PhaseJob buildJob;
        List<IBottleTank> bottleTanks;
        // Sim-time since the last completed bottle sweep → gates the next. Seeded past any interval so the FIRST
        // sweep is due on startup (users expect bottles filled right away); reset to 0 after each sweep.
        double secsSinceBottleSweep = 1e9;
        bool activelyWorking;      // true while a sweep is moving items — the health word; a clean pass clears it
        bool backlog;              // moves are queueing faster than a trickle — the only thing that earns Update1
        // Sim-time each processor's output was last harvested, and the outputs due this cycle (sort-source index →
        // processor id). A due output is stamped only once a sort pass has walked it, so a capped pass that never
        // reached it leaves it due.
        readonly Dictionary<long, double> harvestedAt = new Dictionary<long, double>();
        readonly Dictionary<int, long> dueOutputs = new Dictionary<int, long>();
        RunKind lastTickKind;      // what last tick did → THIS tick's LastRunTimeMs is that kind's cost (one tick late)
        // What last tick did beyond its kind, for the slow and throttling lines: a catch-all othr run can be a find,
        // a bottle step, a panel or nothing at all, and only the job tells our own cold path from an outside stall.
        string lastJob, lastPanel;
        bool lastBeat;             // last tick sent the election heartbeat
        int lastBackoffs;          // last-seen Pacer.Backoffs → a fresh breach back-off engaged
        int lastEscalations;       // last-seen Pacer.Escalations → a back-off escalated to a park
        int lastSpikes;            // last-seen Pacer.Spikes → a run over the spike bar
        double lastBackoffSec = -BackoffLogQuietSeconds;
        DateTime lastRunWallClock = DateTime.UtcNow; // wall-clock of the previous run → the sim-speed estimate's denominator
        readonly DateTime startedAt = DateTime.UtcNow;
        DateTime runStart;
        bool starting;             // this run is within startupSeconds of the start
        bool panelsDrawn;          // every panel of a finished scan has drawn once
        bool heavyRun;             // the pacer allowed a heavy call when this run began
        int runInstructions;       // the previous run's instruction count, for its slow line
        int stepPeak;              // the most instructions one step or render has used
        double gameSeconds;                          // lifetime game-time clock for the yield sample's pace deltas
        int scopeBlocks;
        double lastScopeProbeSec;
        double lastScopeTripSec = -ScopeTripCooldownSeconds;
        bool simParkLogged;        // last-seen Pacer.SimParked → log only the park/resume edges
        bool discoveryObserveTurn;
        string echoText;
        // One StateChannel per recompute cadence (several may share one): each owns its asserted set and its memory,
        // so a site just Begin/Add/Commits on the channel matching its recompute event and no channel prunes
        // another's states. Every channel logs entry edges; only those in statusMessages also render.
        readonly StateChannel learnStates;     // each learn sweep: the preview of what leaving the mode would save
        readonly StateChannel learnSkips;      // each learn sweep: tagged containers that don't teach, and why (log-only)
        readonly StateChannel noRouteStates;      // sort lap: a category with no accepting container. Log-only — its
                                                 // [SHEPHERD] category row already shows ──no route──
        readonly StateChannel unreachableStates; // sort lap: a target that reports room but can't be reached
        readonly StateChannel bottleStates;    // recomputed when a bottle sweep completes (stranded tanks)
        readonly StateChannel reserveStates;   // recomputed each cycle's source build (reserve setup problems)
        readonly StateChannel shipPoolStates;  // same cadence, log-only: the same notes about a ship's pools
        readonly HashSet<string> homePools = new HashSet<string>();   // pools with a container on our own construct
        readonly Dictionary<string, string> allFirstPool = new Dictionary<string, string>();
        readonly StateChannel quotaStates;     // recomputed each configReload (quota semantics)
        // The status panel's message blocks, in render order. Fixed instances filled in place by their owners, so a
        // render neither allocates nor rebuilds them.
        readonly List<List<string>> statusMessages = new List<List<string>>();
        // Reserve pools, keyed by pool name ("" = the default/bare pool). Read once at startup + on `configReload`; the
        // in-memory model is the source of truth (never read per cycle). Counts are reset + refilled each cycle.
        readonly Dictionary<string, ReservePool> reservePools = new Dictionary<string, ReservePool>();
        // The category override map, read from the [Categories] Custom Data section at startup/configReload
        // (never per cycle) and written back merge-not-clobber on change. Memory is the source of truth.
        readonly CategoryOverrides categoryOverrides = new CategoryOverrides();
        // Signature of the managed sections as last read/written, so a flush that would change nothing is skipped.
        string lastFlushSignature = "";
        bool flushPending;         // a managed-section change awaiting its own Save tick
        // Learn mode: item moving stops and each cycle reads the teaching containers instead, previewing what it
        // would save; nothing is written until the mode is left. In memory only, so a recompile leaves the mode and
        // drops what was not saved — the arrangement itself is still in the containers to teach again.
        bool learnMode;
        bool learnSaving;          // `learn` ran again: one more sweep, so the save matches the arrangement right now
        bool learnCommit;          // learned reserve lines ride the next flush, then the config reloads
        readonly PlacementLearner learner = new PlacementLearner();
        readonly PhaseJob learnJob;
        readonly List<Destination> learnTeachers = new List<Destination>();
        readonly List<string> learnTokens = new List<string>();                 // parallel to learnTeachers
        List<ReserveContainer> learnReserves;
        int learnCursor;
        double sortPassStartSec;
        int sortStart;             // where the next sort pass begins, carried across cycles so a capped pass resumes
        int lapFinished;           // sources finished across this lap's passes
        // The sort diagnostics of the current lap, kept across capped passes and committed only once every source
        // has been visited — a partial pass can't tell a cleared state from one it simply didn't reach.
        readonly List<string> lapNoRoute = new List<string>();
        readonly List<UnreachableRoute> lapUnreachable = new List<UnreachableRoute>();
        int movesThisCycle;
        int cycleCount;            // completed sort cycles since compile or the last statsReset
        int renderCursor;          // rotates status→debug→log→levels LCDs so at most ONE panel is built per tick
        int lastManaged, lastDest; // cached scan counts so the status readout can render every tick, not just at scan
        IMyTerminalBlock statusLcd;
        IMyTerminalBlock debugLcd;
        IMyTerminalBlock logLcd;
        List<ReserveContainer> reserveLcds;
        List<Destination> stockLcds;
        List<IMyLightingBlock> lights;
        // The stock LCDs' item keys and their categories, parallel, retaken once per completed ledger tally; keys
        // sorted so the panel order doesn't shuffle between cycles.
        readonly List<string> stockKeys = new List<string>();
        readonly List<string> stockCategories = new List<string>();
        bool stockStale;
        // The status LCD's per-category fill readout, computed off the current scan's destinations: the built-ins,
        // then the custom categories. The rows change only at a cycle's build (configReload restarts the cycle) and
        // are refilled in place, so a status render allocates only the final WriteText string.
        List<Destination> destinations;
        readonly List<CategoryFill> categoryFills = new List<CategoryFill>();
        readonly List<string> customCategories = new List<string>();
        // The last row while any [SHEPHERD:*] container exists: the summed catch-all fill, the one figure every
        // category without its own container shares.
        readonly CategoryFill catchAllRow = new CategoryFill("catch-all");
        // The status LCD's reserve section: one quota-fill bar per configured pool. The pool set changes only on
        // configReload, so the entries are rebuilt there; BuildReserveFills just refills fractions per render.
        readonly List<ReservePool> reserveFillPools = new List<ReservePool>();
        readonly List<CategoryFill> reserveFills = new List<CategoryFill>();       // parallel to reserveFillPools

        public Program()
        {
            learnStates = new StateChannel(Log);
            learnSkips = new StateChannel(Log);
            noRouteStates = new StateChannel(Log);
            unreachableStates = new StateChannel(Log);
            bottleStates = new StateChannel(Log);
            reserveStates = new StateChannel(Log);
            shipPoolStates = new StateChannel(Log);
            quotaStates = new StateChannel(Log);
            production = new ProductionStatus(Log);
            statusMessages.Push(learnStates.Current);
            statusMessages.Push(production.Actions);
            statusMessages.Push(production.States);
            statusMessages.Push(unreachableStates.Current);
            statusMessages.Push(bottleStates.Current);
            statusMessages.Push(reserveStates.Current);
            statusMessages.Push(quotaStates.Current);
            standbyMessages.Push(standbyLines);
            ingotRoomProbe = IngotHasHome;
            openRefineryTakes = OpenRefineryTakes;
            full = () => Runtime.CurrentInstructionCount > Runtime.MaxInstructionCount / 2;
            classifyCursor.Full = full;
            foreach (var cat in Categories.All) categoryFills.Push(new CategoryFill(cat));
            fetchJob = new PhaseJob(FetchPhase, StepResult.Fetched);
            classifyJob = new PhaseJob(ClassifyPhase, StepResult.Classified);
            buildJob = new PhaseJob(BuildPhase, StepResult.Classified);
            ledgerJob = new LedgerJob(productionLedger, productionFacts, reservePools, ExamineBudget) { Full = full };
            planJob = new PhaseJob(PlanPhase, StepResult.Planned);
            learnJob = new PhaseJob(LearnStep, StepResult.Classified);
            leadListener = IGC.RegisterBroadcastListener(LeadChannel);

            // Start eager: the cold first runs must be over within the limiter's startup grace, and a fresh start
            // sorts promptly. The loop settles to idle after one clean pass.
            activelyWorking = backlog = true;
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
            BuildEcho(null);
            Echo(echoText);
            // Config first, so the very first log stamp already carries the configured clock offset.
            var loaded = ReloadConfig();
            Log("v" + ScriptVersion + " started");
            Log(loaded);
        }

        public void Save() { }

        public void Main(string argument, UpdateType updateSource)
        {
            var isTick = (updateSource & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0;
            if (isTick) { Tick(); runInstructions = Runtime.CurrentInstructionCount; return; }

            // Not the scheduled loop — a command, trigger, or the limiter's cooperative shutdown signal. The next
            // tick reads THIS run's ms, so book the previous tick's sample now and charge this run to its own kind,
            // else a command's cost lands on whatever the last tick happened to do.
            stats.Record(Runtime.LastRunTimeMs, lastTickKind);
            lastTickKind = RunKind.Command;
            lastJob = argument;
            lastPanel = null;
            lastBeat = false;
            if (argument != null && argument.StartsWith(GracefulShutDownArg)) HandleGracefulShutDown(argument);
            else if (argument == "configReload")
            {
                // A learned fact waiting for its flush would be dropped by the reload's re-seed from disk.
                if (flushPending) { flushPending = fillSettings = false; FlushConfig(false); }
                ResetCycle();
                ShowCommandResult(ReloadConfig());
            }
            else if (argument == "learn") ShowCommandResult(ToggleLearn());
            else if (argument == "statsReset")
            {
                stats.Reset();
                pacer.ResetCounters();
                lastBackoffs = 0;
                lastEscalations = 0;
                cycleCount = 0; // window-scope the whole [SHEPHERD-DEBUG] header, else lifetime "cycles" sits next
                                // to a just-zeroed "uptime" and the pair reads as nonsense
                ShowCommandResult("run stats reset");
            }
            // A bare run (a timer or button with no argument) is not a mistyped command.
            else if (!string.IsNullOrEmpty(argument)) ShowCommandResult("unrecognized command");
            // TimeSinceLastRun on the next tick counts from THIS run, so the wall clock must too — else the sim
            // estimate's game/wall intervals diverge and a command run reads as a spurious lag dip.
            lastRunWallClock = DateTime.UtcNow;
            runInstructions = Runtime.CurrentInstructionCount;
        }

        // One scheduled run. The Pacer (driven by the previous run's ms) decides whether a heavy call may fire and
        // how fast to wake next. A cycle is a job queue — fetch, then bounded classify batches (spread so no
        // single scan run grows with grid size), then the build, batched the same way, then transfers, then the
        // bottle sweep when due — and the CycleDriver steps ONE job per run, so no two expensive operations share a
        // run. At most one transfer, hence at most one conveyor call, per tick.
        void Tick()
        {
            // A PB can't read its own in-run time, so this is the PREVIOUS tick's cost — attribute it to what
            // that tick did, then let the Pacer learn.
            var lastRunMs = Runtime.LastRunTimeMs;
            var dt = Runtime.TimeSinceLastRun.TotalSeconds; // game time: under lag it under-runs the wall clock
            var now = DateTime.UtcNow;                      // by the sim ratio the limiter divides each run's ms by
            runStart = now;
            starting = (now - startedAt).TotalSeconds < settings[Startup];
            var wall = (now - lastRunWallClock).TotalSeconds;
            lastRunWallClock = now;
            // Game time only passes between runs while the world ticks, so a long gap means the server left this PB
            // unrun: one way a peer's heartbeats go missing after a server restart.
            if (dt > RunGapLogSeconds && gameSeconds > 0.0) Log("no run for " + dt.ToString("0.0") + " s (wall " + wall.ToString("0.0") + " s)");
            var prevKind = lastTickKind;            // the run whose ms may arm a back-off in Observe below
            string prevJob = lastJob, prevPanel = lastPanel;
            var prevBeat = lastBeat;
            lastJob = lastPanel = null;
            lastBeat = false;
            stats.Record(lastRunMs, prevKind);
            var f = Runtime.UpdateFrequency;        // the interval just elapsed ran at the rate set last tick
            stats.AddElapsed(dt, (f & UpdateFrequency.Update1) != 0 ? 0 : (f & UpdateFrequency.Update10) != 0 ? 1 : 2);
            gameSeconds += dt;                      // never resets (unlike the stats window) — pace deltas need it monotone
            secsSinceBottleSweep += dt;
            lastTickKind = RunKind.Other;           // default; overridden below if this tick scans or transfers

            // The limiter checks nothing in its startup allowance, so a start's slow runs arm no back-off: it would
            // only hold back the next cold path.
            pacer.Observe(lastRunMs, dt, activelyWorking && !starting, wall, starting);
            heavyRun = pacer.AllowHeavy;
            if (pacer.SimParked != simParkLogged)
            {
                simParkLogged = pacer.SimParked;
                Log("sim " + pacer.SimInstant.ToString("0.00") + "/" + pacer.SimAvg.ToString("0.00")
                    + (simParkLogged ? " - parking" : " - resuming"));
            }
            // A warmed-up PB standing by does no work at all, and renders only a panel the leader doesn't draw.
            if (StandingBy() && warmedUp)
            {
                lastJob = "standby";
                Echo(echoText);
                if (!election.LeaderOnOurConstruct && !pacer.Quiet) RenderStandingBy();
                ApplyCadence(Cadence.Parked);
                LogPacing(prevKind, prevJob, prevPanel, prevBeat, lastRunMs, now);
                return;
            }
            var observed = DoTickWork();

            // Render only on a plain cheap tick — a heavy tick must do nothing but its call to keep headroom for a
            // GC pause, and the plan and save ticks stay alone so their profiler rows measure only themselves. The
            // cheap ticks render plenty. Until every panel has drawn once, though, each first render is a slow run
            // of its own, so they all ride whatever run has room, heavy or not.
            // A warming PB leaves the panels to a leader on its own construct.
            if (!observed && !pacer.Quiet && (leader == null || !election.LeaderOnOurConstruct)
                && (lastTickKind == RunKind.Other || starting && !panelsDrawn && Room()))
            {
                Echo(echoText);
                var first = renderCursor;
                do
                {
                    var at = Runtime.CurrentInstructionCount;
                    RenderTick();
                    Measure(at);
                }
                while (starting && !panelsDrawn && renderCursor != first && Room());
                if (renderCursor == first && destinations != null) panelsDrawn = true;
            }

            ApplyCadence(pacer.NextCadence(backlog));
            LogPacing(prevKind, prevJob, prevPanel, prevBeat, lastRunMs, now);
        }

        // History only, never the status panel: throttling is common and expected (mostly external GC stalls the
        // limiter drops), so it stays out of the health word too, and the full count is thr xN on the profiler.
        // Logged: an escalation to a park, and the first back-off after a quiet spell — a steady stream of routine
        // ones would bury every other message. prevKind + lastRunMs name the run that armed it (ArmBackoff runs only
        // in Observe off lastRunMs). Every tick calls it, standing by too, so the counters never lag the runs.
        // Wall time since the start, as the limiter's startup allowance counts it: a run over the peak bar in the
        // first minutes says whether the cold runs finished inside that allowance, and which one did not.
        void LogPacing(RunKind prevKind, string job, string panel, bool beat, double lastRunMs, DateTime now)
        {
            var engaged = pacer.Backoffs > lastBackoffs;
            var throttled = pacer.Escalations > lastEscalations || pacer.Spikes > lastSpikes || engaged && gameSeconds - lastBackoffSec >= BackoffLogQuietSeconds;
            var sinceStart = (now - startedAt).TotalSeconds;
            if (throttled || lastRunMs > settings[PeakMs] && sinceStart < StartupLogSeconds)
            {
                if (panel != null) job = job == null ? panel : job + "+" + panel;
                if (beat) job = job == null ? "beat" : job + "+beat";
                Log((throttled ? "throttling " + CadenceLabel() + " (" : "slow (") + Profiler.Tag(prevKind) + " " + (job ?? "none") + " "
                    + lastRunMs.ToString("0.000") + "ms " + runInstructions + "i) +" + sinceStart.ToString("0") + "s");
            }
            if (engaged) lastBackoffSec = gameSeconds;
            lastBackoffs = pacer.Backoffs;
            lastEscalations = pacer.Escalations;
            lastSpikes = pacer.Spikes;
        }

        // Room in this run for another step or render while the start packs them: a stall the server can absorb,
        // and far from the instruction cap, which aborts a script outright — under a quarter of it, and with room for
        // twice the largest step seen, as a step's cost grows with the grid.
        bool Room() => (DateTime.UtcNow - runStart).TotalMilliseconds < PackMs && Runtime.CurrentInstructionCount < Runtime.MaxInstructionCount / 4
            && Runtime.CurrentInstructionCount + 2 * stepPeak < Runtime.MaxInstructionCount;

        void Measure(int at)
        {
            var used = Runtime.CurrentInstructionCount - at;
            if (used > stepPeak) stepPeak = used;
        }

        // True when a blueprint observation took the tick, which then renders nothing either.
        bool DoTickWork()
        {
            try
            {
                if (blueprintProbe.NeedsObservation && !pacer.Halted && !pacer.Throttled)
                {
                    discoveryObserveTurn = !discoveryObserveTurn;
                    if (discoveryObserveTurn) { lastJob = "probe"; ObserveBlueprint(); return true; }
                }
                // Every expensive op runs only when the Pacer allows a heavy call — so a back-off or a
                // GracefulShutDown:: stand-down holds the scan too, not just transfers. A start's run that began
                // allowed goes on past its first heavy call, the election's scope scan included.
                if (!pacer.AllowHeavy && !(starting && heavyRun && Room())) return false;

                // The rewrite re-parses and re-serializes the whole Custom Data, so it gets a tick of its own and
                // the refractory after it, like any other allocation burst — except in a start, which the limiter
                // doesn't check and which has first calls to pack.
                if (flushPending || dryFlush)
                {
                    var dry = !flushPending;
                    flushPending = dryFlush = false;
                    var reload = learnCommit && !dry;
                    FlushConfig(dry);
                    // Learned quotas live only in the user's [Reserve...] sections, so the pools re-read them.
                    if (reload) ReloadConfig();
                    lastTickKind = RunKind.Save;
                    pacer.NotifyHeavyFired();
                    if (!starting || !Room()) return false;
                }
                if (driver.HasWork && gameSeconds - lastScopeProbeSec >= ScopeProbeSeconds)
                {
                    ProbeScope();
                    if (!starting || !Room()) return false;
                }
                if (!driver.HasWork) StartCycle();

                StepResult r;
                bool drySweep;
                do
                {
                    var pass = driver.Current as SortJob;
                    if (pass != null && !pass.Dry && settings[SortPass] > 0.0 && gameSeconds - sortPassStartSec >= settings[SortPass]) pass.Stop();
                    var at = Runtime.CurrentInstructionCount;
                    r = driver.Step();
                    Measure(at);
                    lastJob = JobName(driver.LastCompleted ?? driver.Current);
                    if (driver.LastCompleted == craftJob) blueprintProbe.ProbeExecuted();
                    // A bottle sweep keeps the fast cadence for its whole duration whatever each tick did, but never
                    // bumps movesThisCycle — a sweep is maintenance, so EndCycle's active/idle decision stays driven
                    // by the sort's own moves.
                    var bottleTick = driver.Current is BottleJob || driver.LastCompleted is BottleJob;
                    var sweep = (driver.LastCompleted ?? driver.Current) as BottleJob;
                    drySweep = sweep != null && sweep.Dry;
                    if (bottleTick) activelyWorking = backlog = true;

                    if (r == StepResult.Fetched)
                    {
                        lastTickKind = RunKind.Fetch;
                        pacer.NotifyHeavyFired();
                    }
                    else if (r == StepResult.Classified)
                    {
                        // A classify batch, or a build step (an allocation burst over a slice of the managed blocks):
                        // armed like a heavy call so the GC-risk window lands on a cheap tick and no two batches (nor
                        // a batch and the first transfer) fire back-to-back.
                        lastTickKind = driver.Current == buildJob || driver.LastCompleted == buildJob ? RunKind.Build : RunKind.Scan;
                        pacer.NotifyHeavyFired();
                    }
                    else if (r == StepResult.Moved)
                    {
                        if (!bottleTick)
                        {
                            activelyWorking = true;
                            // Mid-cycle too, so a dock unload speeds up at once rather than a whole slow cycle later.
                            if (++movesThisCycle >= BacklogMoves) backlog = true;
                        }
                        lastTickKind = RunKind.Transfer;
                        pacer.NotifyHeavyFired();
                    }
                    else if (r == StepResult.Stalled)
                    {
                        // The conveyor call fired but moved nothing — a dead target. It cost a transfer, so attribute
                        // it and arm the refractory like a Moved, but it is NOT progress, so leave movesThisCycle/
                        // activelyWorking alone — a stall-only cycle parks at idle and re-probes the dead target
                        // slowly rather than hammering it at the fast cadence.
                        lastTickKind = RunKind.Transfer;
                        pacer.NotifyHeavyFired();
                    }
                    else if (r == StepResult.Planned)
                    {
                        // Usually cheap, but a tick where a discovery operation changes state can cost several ms.
                        lastTickKind = RunKind.Plan;
                        pacer.NotifyHeavyFired();
                    }
                    else if (r == StepResult.Found && !bottleTick)
                    {
                        // A transfer is queued for the next tick. Stay active but do NOT arm the refractory — find and
                        // its commit run back-to-back; only the commit is heavy.
                        activelyWorking = true;
                    }

                    var sort = driver.LastCompleted as SortJob;
                    var dryEnd = sort != null && sort.Dry;
                    if (dryEnd)
                    {
                        warmedUp = true;
                        Log("warmed up +" + (DateTime.UtcNow - startedAt).TotalSeconds.ToString("0") + "s");
                    }
                    if (sort != null) FinishSort(sort);
                    var bottles = driver.LastCompleted as BottleJob;
                    if (bottles != null) FinishBottles(bottles);
                    // The cycle ends when the queue drains, whichever job happened to be last — a completion handler
                    // that enqueues a follow-up (bottles) keeps the cycle open by construction.
                    if (driver.LastCompleted != null && !driver.HasWork) EndCycle(dryEnd);
                }
                // A method compiles on its first call, so after a start every phase's first step is slow wherever it
                // lands. Several share a run then: fewer runs over the spike bar, all inside the limiter's startup
                // allowance. Never past a step that moved items or found a stack to move — one transfer per run —
                // except in the dry sweep, whose moves and refills skip the engine call.
                while (starting && driver.HasWork && (drySweep || r != StepResult.Found && r != StepResult.Moved
                    && r != StepResult.Stalled && r != StepResult.Refilled) && Room());

                pacer.NotifyCleanRun();
            }
            catch (Exception e)
            {
                HandleCrash(e);
            }
            return false;
        }

        // Open a fresh cycle: the pooled phase jobs re-arm and queue up. The build's last step enqueues the ledger and
        // the plan, the plan enqueues the SortJob, and FinishSort queues the bottle sweep when one is armed.
        void StartCycle()
        {
            fetchJob.Rearm();
            classifyJob.Rearm();
            buildJob.Rearm();
            buildCursor = 0;
            driver.Enqueue(fetchJob);
            driver.Enqueue(classifyJob);
            driver.Enqueue(buildJob);
        }

        // `learn`: enter the mode, or — already in it — run one more sweep and save. Entering drops the cycle and any
        // discovery in flight, since nothing moves while learning.
        string ToggleLearn()
        {
            if (leader != null) return "standing by - run learn on " + leader;
            if (learnSaving) return "already saving after one more sweep";
            if (learnMode)
            {
                learnSaving = true;
                ResetWork();
                return "saving after one more sweep";
            }
            ResetCycle();
            learnMode = true;
            Log("learning - arrange items, run learn again to save");
            return "learning: put items where they belong,\nthen run learn again to save";
        }

        // Teachers are the home destinations with exactly one category and no catch-all, and every reserve container in
        // scope; a tagged container that can't teach says why, once.
        void StartLearnSweep(ScanResult scan)
        {
            learnTeachers.Empty();
            learnTokens.Empty();
            learnSkips.Begin();
            foreach (var dest in scan.Destinations)
            {
                string token = null;
                var count = 0;
                foreach (var cat in dest.AcceptedCategories) { token = cat; count++; }
                if (dest.IsCatchAll) learnSkips.Push(dest.Name + ": not a teacher - catch-all");
                else if (count != 1) learnSkips.Push(dest.Name + ": not a teacher - " + count + " categories");
                else { learnTeachers.Push(dest); learnTokens.Push(token); }
            }
            learnSkips.Commit();
            learnReserves = scan.Reserves;
            learnCursor = 0;
            learner.Begin();
            foreach (var pool in reservePools.Values) pool.BeginTally();
            learnJob.Rearm();
            driver.Enqueue(learnJob);
        }

        // One teacher's inventory per tick, then the preview — and, when saving, the save itself.
        bool LearnStep()
        {
            var teachers = learnTeachers.Count;
            if (learnCursor < teachers + learnReserves.Count)
            {
                itemBuffer.Empty();
                if (learnCursor < teachers)
                {
                    learnTeachers[learnCursor].Inventory.GetItems(itemBuffer);
                    foreach (var item in itemBuffer)
                        learner.TeachCategory(learnTokens[learnCursor], ItemKey.Of(item.Type), Category.For(item.Type.TypeId));
                }
                else
                {
                    // No ledger walk runs while learning, so this read also keeps the pools' counts current.
                    var reserve = learnReserves[learnCursor - teachers];
                    var pool = GetOrAddPool(reserve.Pool);
                    reserve.Inventory.GetItems(itemBuffer);
                    foreach (var item in itemBuffer)
                    {
                        var key = ItemKey.Of(item.Type);
                        var amount = (double)item.Amount;
                        learner.TeachReserve(reserve.Pool, key, amount, reserve.Docked);
                        pool.Tally(key, amount);
                    }
                }
                learnCursor++;
                return false;
            }
            learner.Finish(categoryOverrides, reservePools);
            foreach (var pool in reservePools.Values) pool.CommitTally();
            learnStates.Begin();
            foreach (var line in learner.Lines) learnStates.Push(line);
            learnStates.Commit();
            if (learnSaving)
            {
                // The categories change in memory now; the file and the reserve quotas follow on the flush tick.
                learner.Apply(categoryOverrides);
                if (learner.HasChanges) learnCommit = flushPending = true;
                LeaveLearn(learner.HasChanges
                    ? "learn saved " + (learner.CategorySets.Count + learner.CategoryRemovals.Count) + " categories, "
                      + learner.QuotaLines.Count + " reserve lines"
                    : "learn: nothing to save");
            }
            return true;
        }

        void LeaveLearn(string outcome)
        {
            learnMode = learnSaving = false;
            learnStates.Begin();
            learnStates.Commit();
            learnSkips.Begin();
            learnSkips.Commit();
            Log(outcome);
            BuildEcho(outcome);
            Echo(echoText);
        }

        string JobName(IStepJob job) =>
            job is SortJob ? "sort" : job is BottleJob ? "bottle" : job is CraftJob ? "craft" : job is LedgerJob ? "ledger"
            : job == fetchJob ? "fetch" : job == classifyJob ? "scan" : job == buildJob ? "build" : job == planJob ? "plan" : job == learnJob ? "learn" : null;

        class PhaseJob : IStepJob
        {
            readonly Func<bool> body;
            readonly StepResult result;
            bool done;
            public PhaseJob(Func<bool> phaseBody, StepResult phaseResult)
            {
                body = phaseBody;
                result = phaseResult;
            }
            public void Rearm() => done = false;
            public bool IsDone => done;
            public StepResult Step() { if (body()) done = true; return result; }
        }

        bool FetchPhase()
        {
            FetchBlocks();
            scopeBlocks = blockBuffer.Count;
            lastScopeProbeSec = gameSeconds;
            RefreshScope(scope);
            classifyCursor.Begin(blockBuffer, scope);
            return true;
        }

        void RefreshScope(ManagedScope into)
        {
            connectorBuffer.Empty();
            GridTerminalSystem.GetBlocksOfType(connectorBuffer);
            into.Build(connectorBuffer, Me.CubeGrid, Me.OwnerId, On(FactionShare), On(DockedProduction), On(DockedFuel));
        }

        // The election runs ahead of the tick's own heavy gate, so its scans take the gate themselves: a cold first
        // scan inside a spike hold, or sharing a run with the first fetch, can be the third run over 10 ms that
        // Destiny punishes. A skipped scan waits for an allowed tick; until then the PB does no work to lead with anyway.
        void ScanScopeAsHeavy()
        {
            if (!pacer.AllowHeavy) return;
            RefreshScope(scope);
            lastTickKind = RunKind.Fetch;
            lastJob = "scope";
            pacer.NotifyHeavyFired();
        }

        // Re-count the grid and its docked constructs while a cycle is in flight, and abandon the cycle if the grid
        // shrank materially or a construct docked, undocked or was split off — so the jobs stop working from a
        // picture that includes blocks the world no longer has (a small ship's undock is under the shrink bar, yet
        // every transfer to it would fail), and a newly docked ship is picked up without waiting out the pass.
        // Consumes the tick whenever it counts (one concern per tick, outside a start) and is attributed to the
        // fetch bucket — the same calls, at the same cost. A trip drops the work but keeps discovery state, like a stand-down: those
        // operations re-validate against every fresh scan, and a vanished machine is already their own recovery path.
        void ProbeScope()
        {
            lastScopeProbeSec = gameSeconds;
            scopeBuffer.Empty();
            GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(scopeBuffer);
            RefreshScope(probeScope);
            lastTickKind = RunKind.Fetch;
            lastJob = "scope";
            if (gameSeconds - lastScopeTripSec < ScopeTripCooldownSeconds) return;
            var trip = ScopeShrank(scopeBlocks, scopeBuffer.Count) ? "scope shrank " + scopeBlocks + " to " + scopeBuffer.Count
                     : probeScope.Count != scope.Count ? "docking changed" : null;
            if (trip == null) return;
            lastScopeTripSec = gameSeconds;
            Log(trip + " - rescanning");
            ResetWork();
        }

        public static bool ScopeShrank(int captured, int current) =>
            captured - current > (captured / 20 > 4 ? captured / 20 : 4);

        bool ClassifyPhase() => !classifyCursor.Step(ClassifyBudget);

        // The cycle finalize, ClassifyBudget managed blocks per tick so its cost stays flat as the grid grows. What
        // other ticks read between the steps (assemblers, routing, the scan's lists and LCDs) changes only on the last.
        bool BuildPhase()
        {
            var scan = classifyCursor.Result;
            var managed = scan.Managed;
            if (buildCursor == 0)
            {
                acceptors.Empty();
                foreach (var dest in scan.Destinations) acceptors.Push(dest);
                refineryFeeds.Empty();
                ledgerKinds.Empty();
                countPools.Empty();
                builtAssemblers.Empty();
                dueOutputs.Empty();
                builtSources = 0;
            }
            var end = Math.Min(buildCursor + ClassifyBudget, managed.Count);
            for (; buildCursor < end && !full(); buildCursor++) BuildBlock(managed[buildCursor]);
            if (buildCursor < managed.Count || full()) return false;
            FinishBuild(scan);
            return true;
        }

        // The production-plan tick: one pass running the craft planner over the freshly filled ledger, then
        // surfacing its blocked-demand outputs, flagging a tracking change for the save tick, and enqueueing the
        // craft actions (if any) ahead of the sort — so a planned queue mutation lands the same cycle it was decided.
        bool PlanPhase()
        {
            craftPlanner.Plan(productionLedger, productionFacts, ledgerJob.Snapshots,
                blueprintProbe.ReservedAssemblerId, gameSeconds);
            refinePlanner.Plan(productionLedger, productionFacts, refineryFeeds, ingotRoomProbe, DiscoverYieldSample);
            // Keep the active sample's request alive. Before ore is spent (draining/ready) the demand must still
            // stand, so a vanished deficit cancels cheaply — but a FED sample runs to completion regardless: the
            // ore is already committed, and its own held output counts as ledger stock, so the learn would
            // otherwise close its reason-deficit mid-measure and abort itself with the evidence spent.
            var sampleCommitted = yieldSample.Phase == YieldPhase.Feeding || yieldSample.Phase == YieldPhase.Measuring;
            if (yieldSample.Active && (sampleCommitted || productionLedger.RefineDeficit(yieldSample.Ingot) >= 1.0)
                && !refinePlanner.NeedYield.Contains(yieldSample.Ore)) refinePlanner.NeedYield.Push(yieldSample.Ore);
            // While the sample runs, its ore's blocked lines misread the in-progress learn as a failure: "no
            // refinery" just means the OTHER refineries can't take it, and "need N to learn" fires because the
            // delivered sample sits ledger-protected in the owned refinery's input, leaving loose stock short.
            if (yieldSample.Active) DropStaleYieldLines(yieldSample.Ore);
            sampleReservedOres.Empty();
            foreach (var key in refinePlanner.NeedYield) sampleReservedOres.Push(key);
            foreach (var key in refinePlanner.UnderSampled) sampleReservedOres.Push(key);
            if (yieldSample.Active) sampleReservedOres.Push(yieldSample.Ore);
            discovery.Reconcile(craftPlanner.NeedBlueprint, refinePlanner.NeedYield);
            PlanBlueprint();
            PlanYield();
            production.Compose(craftPlanner, refinePlanner, discovery, blueprintProbe, yieldSample, markCheck);
            // Outranked and warming up: the plan ran for its cold start alone. It queues no real work and writes nothing,
            // only the dry cycle, and its discovery claims go, so a later resume plans afresh.
            if (leader != null)
            {
                discovery.Reset();
                blueprintProbe.Reset();
                yieldSample.Reset();
                QueueDryRun();
                return true;
            }
            PersistTrackedDisassembler();
            if (QueueDryRun()) return true;
            cycleRouting.Forget();
            if (craftPlanner.Actions.Count > 0)
            {
                craftJob.Rearm(craftPlanner, craftPlanner.Actions);
                driver.Enqueue(craftJob);
                activelyWorking = true;
            }
            driver.Enqueue(new SortJob(sortSources, cycleRouting, ExamineBudget, sortStart, lapNoRoute, lapUnreachable) { Full = full });
            sortPassStartSec = gameSeconds;
            return true;
        }

        // A method compiles on its first call, and the work a grid needs may not come up for hours after a start: a
        // ship docks, a stock runs low, bottles go empty. So the first plan after a compile queues one pass of every
        // job that moves nothing: each routed target, gas tank and one assembler take an action of every kind with
        // the engine call skipped, and the config save builds its text without writing it. Real work starts with the
        // next cycle, from a fresh count, since the dry claims leave the pools' and feeds' counts off for this one.
        // Returns whether it queued. A dry cycle cut short (a crash, a rescan) is queued once more, then given up, so a
        // fault in it can't keep real work from ever starting.
        bool QueueDryRun()
        {
            if (warmedUp) return false;
            if (++dryRuns > 2) { warmedUp = true; return false; }
            cycleRouting.Forget();
            dryFlush = true;
            dryActions.Empty();
            foreach (var s in ledgerJob.Snapshots)
            {
                for (var k = CraftActionKind.AppendAssembly; k <= CraftActionKind.Trim; k++)
                    dryActions.Push(new CraftAction { Kind = k, Assembler = s.State, Blueprint = "", Amount = 1, Item = "" });
                break;
            }
            dryCraft.Rearm(craftPlanner, dryActions);
            driver.Enqueue(dryCraft);
            driver.Enqueue(new BottleJob(sortSources, Stations(classifyCursor.Result.Tanks), ExamineBudget) { Dry = true, Full = full });
            driver.Enqueue(new SortJob(sortSources, cycleRouting, ExamineBudget, 0, new List<string>(), new List<UnreachableRoute>()) { Dry = true, Full = full });
            return true;
        }

        // The refine planner's stop condition: refined ingots must have somewhere to land, or they just strand in
        // refinery outputs. Any routed acceptor with room counts (a below-target reactor is a legitimate home).
        // Cheap local probes on the plan tick; the cycle's feeds are still unconstrained when this runs.
        bool IngotHasHome(string ingotKey)
        {
            if (cycleRouting == null) return false;
            var type = ItemKey.ToType(ingotKey);
            foreach (var target in cycleRouting.Route(Category.ForKey(ingotKey, categoryOverrides), Category.ForKey(ingotKey)))
                if (target.Accepts(type)) return true;
            return false;
        }

        // Write the tracking state only on the acquire/release edge — the flush re-parses and merge-rewrites the
        // managed sections, so a steady plan writes nothing.
        void PersistTrackedDisassembler()
        {
            if (craftPlanner.TrackedDisassembler == persistedDisassembler) return;
            persistedDisassembler = craftPlanner.TrackedDisassembler;
            flushPending = true;
        }

        void PlanBlueprint()
        {
            if (discovery.HasParkedBlueprint)
            {
                if (blueprintProbe.Item != discovery.ParkedBlueprint.Key) blueprintProbe.Reset();
                return;
            }
            if (!discovery.HasActive || discovery.Active.Kind != KnowledgeKind.Blueprint)
            {
                if (blueprintProbe.Phase != BlueprintPhase.Idle) blueprintProbe.Reset();
                return;
            }
            blueprintProbe.Plan(discovery.Active.Key, ledgerJob.Snapshots, craftPlanner.Actions);
            if (blueprintProbe.Phase == BlueprintPhase.Manual)
            {
                discovery.ParkBlueprint();
                discovery.Reconcile(craftPlanner.NeedBlueprint, refinePlanner.NeedYield);
            }
        }

        void ObserveBlueprint()
        {
            var result = blueprintProbe.Observe(assemblerStates, productionFacts);
            if (result == BlueprintObservation.None) return;
            if (result == BlueprintObservation.Manual)
            {
                blueprintProbe.MakeManual();
                discovery.ParkBlueprint();
                return;
            }
            var item = blueprintProbe.Item;
            var blueprint = blueprintProbe.Blueprint;
            productionFacts.Learn(item, blueprint);
            discovery.Resolve(KnowledgeKind.Blueprint, item);
            blueprintProbe.Reset();
            Log("learned blueprint " + item + " = " + blueprint);
            if (productionFacts.TakeDirty()) flushPending = true;
        }

        void PlanYield()
        {
            if (!discovery.HasActive || discovery.Active.Kind != KnowledgeKind.Yield)
            {
                if (yieldSample.Active) yieldSample.Reset();
                return;
            }
            if (!yieldSample.Active || yieldSample.Ore != discovery.Active.Key)
            {
                yieldSample.Reset();
                yieldSample.Begin(discovery.Active.Key, refinePlanner.YieldReason, refineryFeeds);
                return;
            }
            ManagedBlock refinery;
            if (!FindManaged(yieldSample.RefineryId, out refinery)) { yieldSample.Reset(); return; }
            ReadInventory(refinery.Input, yieldInput);
            ReadInventory(refinery.Output, yieldOutput);
            double oreInput;
            yieldInput.Get(yieldSample.Ore, out oreInput);
            var learned = yieldSample.Advance(refinery.Block.IsWorking, Total(yieldInput), oreInput, yieldOutput,
                DiscoverYieldSample, DiscoverYieldFloor, gameSeconds);
            if (!yieldSample.Active)
            {
                // Tampered evidence is worthless — abandon unrecorded (never persist uncertain knowledge); the
                // reset releases the refinery this same plan tick and the learn re-triggers while the deficit stands.
                Log("learn " + yieldSample.Ore + ": aborted - refinery disturbed");
                yieldSample.Reset();
                return;
            }
            foreach (var feed in refineryFeeds) yieldSample.Bind(feed);
            if (learned == null) return;
            var ore = yieldSample.Ore;
            productionFacts.LearnYield(ore, learned);
            discovery.Resolve(KnowledgeKind.Yield, ore);
            yieldSample.Reset();
            // The plan computed NeedYield/Blocked before this learn landed; drop the ore's now-stale entries so
            // the success doesn't render one cycle of missing-yield lines for a just-learned ore.
            refinePlanner.NeedYield.Remove(ore);
            DropStaleYieldLines(ore);
            Log("learned yield " + ore);
            if (productionFacts.TakeDirty()) flushPending = true;
        }

        void DropStaleYieldLines(string ore)
        {
            refinePlanner.Blocked.Remove(RefinePlanner.NoRefineryLine(ore));
            refinePlanner.Blocked.Remove(RefinePlanner.NeedOreLine(DiscoverYieldSample, ore));
        }

        bool OpenRefineryTakes(string key)
        {
            List<OreYield> outputs;
            return productionFacts.TryYields(key, out outputs) || !sampleReservedOres.Contains(key);
        }

        bool FindManaged(long id, out ManagedBlock found)
        {
            if (managedBlocks != null)
                foreach (var block in managedBlocks)
                    if (block.Block.EntityId == id) { found = block; return true; }
            found = default(ManagedBlock);
            return false;
        }

        void ReadInventory(IMyInventory inventory, Dictionary<string, double> into)
        {
            into.Empty();
            if (inventory == null) return;
            itemBuffer.Empty();
            inventory.GetItems(itemBuffer);
            foreach (var item in itemBuffer)
            {
                var key = ItemKey.Of(item.Type);
                double amount;
                into[key] = (into.Get(key, out amount) ? amount : 0.0) + (double)item.Amount;
            }
        }

        static double Total(Dictionary<string, double> items)
        {
            var total = 0.0;
            foreach (var amount in items.Values) total += amount;
            return total;
        }

        // Cycle phase 1: the GetBlocksOfType sweep (always-rescan) into the reused buffer, on its own tick so its
        // cost is isolated and followed by a back-off.
        void FetchBlocks()
        {
            blockBuffer.Empty();
            GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(blockBuffer);
        }

        // The build's last step: the reserve intakes after the feeds (acceptor order is the routing tie-break), the
        // routing index, and the swap of everything other ticks read; then the ledger and plan (or the learn sweep).
        void FinishBuild(ScanResult scan)
        {
            if (sortSources.Count > builtSources) sortSources.RemoveRange(builtSources, sortSources.Count - builtSources);
            AddReserveIntakes(scan);
            cycleRouting = new RoutingIndex(acceptors);
            var built = builtAssemblers;
            builtAssemblers = assemblerStates;
            assemblerStates = built;
            markCheck.Check(assemblerStates, craftPlanner.TrackedDisassembler);
            managedBlocks = scan.Managed;
            if (learnMode) StartLearnSweep(scan);
            else
            {
                ledgerJob.Rearm(sortSources, ledgerKinds, countPools, assemblerStates);
                stockStale = true;
                driver.Enqueue(ledgerJob);
                // An outranked PB plans only while warming up, to run the plan's first, slowest pass inside the
                // limiter's startup allowance; the plan leaves its work to the leader (PlanPhase).
                if (leader == null || !warmedUp)
                {
                    planJob.Rearm();
                    driver.Enqueue(planJob);   // the plan tick enqueues the craft actions (if any) and then the sort
                }
            }
            movesThisCycle = 0;
            lastManaged = scan.Managed.Count;
            lastDest = scan.Destinations.Count;
            destinations = scan.Destinations;
            RebuildCategoryRows();
            statusLcd = scan.StatusLcd;
            debugLcd = scan.DebugLcd;
            logLcd = scan.LogLcd;
            reserveLcds = scan.ReserveLcds;
            stockLcds = scan.StockLcds;
            lights = scan.Lights;
            BuildBottleSweep(scan.Tanks);
        }

        // Build the refill stations if a bottle sweep is due this cycle. bottleTanks stays null when no sweep is
        // due, when disabled, or when the grid has no gas tank — FinishSort then ends the cycle normally.
        void BuildBottleSweep(List<IMyGasTank> tanks)
        {
            bottleTanks = null;
            if (settings[BottleSweep] <= 0.0 || secsSinceBottleSweep < settings[BottleSweep] * 60.0 || tanks.Count == 0) return;
            bottleTanks = Stations(tanks);
            if (bottleTanks.Count == 0) bottleTanks = null;
        }

        List<IBottleTank> Stations(List<IMyGasTank> tanks)
        {
            var stations = new List<IBottleTank>(tanks.Count);
            foreach (var tank in tanks)
            {
                var inv = tank.GetInventory(0);
                if (inv != null) stations.Push(new RefillStation(tank, inv, itemBuffer));
            }
            return stations;
        }

        // A sort pass ended (finished or capped): stamp the due outputs it walked, carry its resume point, commit the
        // lap's diagnostics once every source has been visited, then queue the bottle sweep if one is armed this
        // cycle. The sweep reuses the sort's storage sources, so it rides the same one-transfer/tick budget. (Cycle
        // end is the scheduler's queue-drained check, not this handler's job.) A dry pass walked without harvesting
        // anything, so it stamps nothing and keeps the rotation, but commits the lap so that code has run too.
        void FinishSort(SortJob job)
        {
            var n = sortSources.Count;
            var walked = job.Dry ? 0 : job.SourcesFinished;
            // This pass walked SourcesFinished sources ending just before NextStart, wrapping.
            foreach (var due in dueOutputs)
                if (((due.Key - job.NextStart + walked) % n + n) % n < walked)
                    harvestedAt[due.Value] = gameSeconds;
            if (!job.Dry) sortStart = job.NextStart;
            lapFinished += walked;
            if (lapFinished >= sortSources.Count || job.Dry)
            {
                // Two channels because a missing route already shows on the panel as its category's ──no route──
                // row (a routed custom category has a row too: its token is an override), so only the unreachables
                // are shown. Those are keyed by target+category, so a container filtering only some categories
                // reports each. Each state is forgotten once a lap stops asserting it, re-firing if it returns.
                noRouteStates.Begin();
                unreachableStates.Begin();
                foreach (var cat in lapNoRoute) noRouteStates.Push("no route for " + cat + " - add [SHEPHERD:" + cat + "]");
                foreach (var u in lapUnreachable)
                    unreachableStates.Push("can't reach " + u.Target + " for " + u.Category + " - check conveyor/sorter");
                noRouteStates.Commit();
                unreachableStates.Commit();
                ResetLap();
            }
            if (job.Dry || bottleTanks == null || bottleTanks.Count == 0) return;
            driver.Enqueue(new BottleJob(sortSources, bottleTanks, ExamineBudget) { Full = full });
            activelyWorking = true;
            Log("refilling bottles (" + bottleTanks.Count + " tank" + (bottleTanks.Count == 1 ? "" : "s") + ")");
        }

        // The sweep is done: surface any stranded tanks, restart the sweep interval — unless it was the dry one.
        void FinishBottles(BottleJob job)
        {
            LogBottleStranded(job.StrandedTanks);
            bottleTanks = null;
            if (!job.Dry) secsSinceBottleSweep = 0.0;
        }

        // Log a refill tank the first time the Drain pass can't return its bottles to storage; cleared once a
        // later sweep places them, re-fires if it recurs.
        void LogBottleStranded(List<string> current)
        {
            bottleStates.Begin();
            foreach (var name in current)
                bottleStates.Push("bottles stuck in " + name + " - storage full/unreachable");
            bottleStates.Commit();
        }

        // A cycle drained: only a backlog's worth of moves keeps the fast cadence for the next one (more of it may
        // be waiting); a trickle or a clean pass settles to the idle cadence, which keeps up with a trickle.
        // The dry cycle moves nothing either, and the real first cycle after it should run as eagerly as a start.
        void EndCycle(bool eager)
        {
            cycleCount++;
            activelyWorking = movesThisCycle > 0 || learnMode || eager;
            // A learn sweep moves nothing, yet the preview should follow the user's rearranging promptly.
            backlog = movesThisCycle >= BacklogMoves || learnMode || eager;
        }

        // The Advanced PB Limiter calls Main with GracefulShutDown:: right before it would punish us. Stand down
        // hard: drop the in-flight sweep, hold every heavy call, and park until the offense
        // window ages out. A fresh scan resumes after the hold clears. A stand-down is pacing, not a fault, so
        // discovery operations survive it (their state is rebuilt into fresh feeds/drain modes every cycle) —
        // except a probe whose queue mutation may have been dropped with the in-flight jobs; it restarts from
        // selection.
        void HandleGracefulShutDown(string argument)
        {
            pacer.StandDown();
            ResetWork();
            ApplyCadence(pacer.NextCadence(false));
            Log("standing down (" + argument + ")");
        }

        // Hear the other SHEPHERD PBs, advertise when due, and report whether a live one outranks us. Every PB
        // advertises whatever its state, so each reaches the same verdict on its own. Entering stand-by drops the
        // cycle and discovery: the stand-by may last indefinitely, and the leader plans from its own view.
        bool StandingBy()
        {
            var grid = Me.CubeGrid;
            var rank = PeerElection.Rank((int)settings[Priority], grid.IsStatic, grid.GridSizeEnum == MyCubeSize.Large, pacer.Halted);
            // Before the first fetch the scope is empty and would drop every peer heard, so a PB compiled while docked
            // would lead the whole docked group until the next heartbeat.
            if (scope.Count == 0) ScanScopeAsHeavy();
            while (leadListener.HasPendingMessage)
            {
                var m = leadListener.AcceptMessage();
                // The tag is open to any script, and the engine may hand a PB its own broadcast.
                if (m.Source == IGC.Me || !(m.Data is MyTuple<long, long, string>)) continue;
                var ad = (MyTuple<long, long, string>)m.Data;
                // Compete only with a PB that would manage the same blocks — ours and inside our scope — so a guest's
                // SHEPHERD, or one beyond a [SHEPHERD-Split] connector, keeps managing its own side.
                var pb = GridTerminalSystem.GetBlockWithId(ad.Item2);
                var at = pb == null || pb.OwnerId != Me.OwnerId ? -1 : scope.IndexOf(pb.CubeGrid);
                if (at < 0) continue;
                if (election.Hear(m.Source, ad.Item1, ad.Item3, gameSeconds, at == 0) && !PeerElection.Outranks(ad.Item1, m.Source, rank, IGC.Me))
                    Log(ad.Item3 + " standing by");
            }
            if (election.TakeHeartbeat(gameSeconds))
            {
                lastBeat = true;
                IGC.SendBroadcastMessage(LeadChannel, new MyTuple<long, long, string>(rank, Me.EntityId, Me.CustomName + " on " + grid.CustomName), LeadRange);
                // A standing-by PB fetches nothing, so this is how it notices a split or an undock that frees it. A
                // warming one is mid-cycle, whose classify reads the scope and whose own probe watches the connectors.
                if (leader != null && !driver.HasWork) ScanScopeAsHeavy();
            }
            var lead = election.Leader(IGC.Me, rank, gameSeconds);
            if (lead != leader)
            {
                if (leader == null)
                {
                    ResetCycle();
                    if (learnMode) LeaveLearn("learn cancelled - standing by");
                }
                Log(lead != null ? "standing by - " + lead + " leads" : "resuming - no higher-priority SHEPHERD");
                leader = lead;
                standbyLines.Empty();
                if (lead != null) standbyLines.Push(lead + " leads");
                BuildEcho(null);
            }
            return leader != null;
        }

        // The full mid-cycle reset (crash / configReload): drop the work AND abandon discovery operations — a
        // crash may have originated in one, and a reload changes the facts/quotas they were planned against.
        void ResetCycle()
        {
            ResetWork();
            discovery.Reset();
            blueprintProbe.Reset();
            yieldSample.Reset();
        }

        // Drop every queued and in-flight job plus any partial classify, so nothing poisoned leaks into the
        // resumed cycle; the next allowed tick starts fresh.
        void ResetWork()
        {
            // An abandoned pass still got somewhere: resume there. Otherwise every pass cut short (a dock or undock,
            // a shrink, a stand-down, configReload) restarts at the same source, and the later ones are never reached.
            var pass = driver.Current as SortJob;
            if (pass != null) sortStart = pass.NextStart;
            driver.Clear();
            bottleTanks = null;
            classifyCursor.Reset();
            ResetLap();
            activelyWorking = backlog = false;
            // A queued probe's assembler append rides the CraftJob just dropped, and nothing re-issues it: the
            // phase only advances when that job completes, no observation runs before it does, and the planner
            // returns early on any non-Protecting phase. It would wait forever on a queue entry never made.
            if (blueprintProbe.Phase == BlueprintPhase.Queued) blueprintProbe.Reset();
        }

        void ResetLap()
        {
            lapNoRoute.Empty();
            lapUnreachable.Empty();
            lapFinished = 0;
        }

        // The one central log call: every diagnostic goes through here → the [SHEPHERD-LOG] rolling buffer. Keep
        // messages short and actionable — string literals ship verbatim under minify.
        void Log(string message)
        {
            var line = Clock.HmsCentis(CurrentTime()) + " " + message;
            messageLog.Push(line);
        }

        // DateTime.UtcNow (no local time zone on a server) shifted by utcOffsetHours. Read live at each use, so
        // the status clock and the log stamps share one source. Display only — never gates a sort/pace decision.
        DateTime CurrentTime() => DateTime.UtcNow.AddHours(settings[UtcOffset]);

        // A step threw. Never fast-loop a crashing step: drop the whole in-flight cycle (a fresh scan rebuilds
        // next cycle), arm the breaker back-off, and log. Repeated crashes with no clean run between park us;
        // a clean run closes the breaker.
        void HandleCrash(Exception e)
        {
            ResetCycle();
            pacer.NotifyCrash();
            Log("step failed: " + e.Message);
        }

        // Update100 is reserved for the safety parks; ordinary idle wakes at Update10 so a whole caught-up cycle
        // completes in seconds and new work (a ship docking mid-idle) is noticed promptly — idle runs are far too
        // cheap for the extra wakes to matter against the limiter's bars.
        void ApplyCadence(Cadence c)
        {
            var f = c == Cadence.Working ? UpdateFrequency.Update1
                  : c == Cadence.Parked ? UpdateFrequency.Update100
                  : UpdateFrequency.Update10;
            if (Runtime.UpdateFrequency != f) Runtime.UpdateFrequency = f;
        }

        // Parse Me.CustomData into the in-memory config: the [Reserve]/[Reserve:Name] quota pools (user-authored;
        // only a learn-mode save writes into them) and the managed [Categories], [Production], [Recipes], and [Yields] maps. Called at startup and on the
        // `configReload` command, never per cycle; a malformed line is logged and skipped, never thrown. Records the
        // managed-section signature so a later FlushConfig with no model change is a no-op. Returns a short summary.
        string ReloadConfig()
        {
            reservePools.Empty();
            reserveFillPools.Empty();
            reserveFills.Empty();
            categoryOverrides.Clear();
            productionFacts.ClearLearned();
            craftPlanner.TrackedDisassembler = 0;
            persistedDisassembler = 0;
            lastFlushSignature = "";
            for (var i = 0; i < settings.Length; i++) settings[i] = SettingRules[3 * i + 2];
            ApplyPacerSettings();
            var cd = Me.CustomData;
            // A fresh PB has empty Custom Data, which MyIni rejects — that is not an error, just "no config yet",
            // so say so plainly; the save tick then writes the settings with their defaults.
            if (string.IsNullOrEmpty(cd) || cd.Trim().Length == 0)
            {
                fillSettings = flushPending = true;
                return "no config (Custom Data empty)";
            }

            var doc = new MyIniDocument();
            string error;
            if (!doc.TryParse(cd, out error))
            {
                Log("bad Custom Data: " + error);
                return "config parse failed";
            }
            var store = new ConfigStore(doc, CategoriesSection, ProductionSection, RecipesSection, YieldsSection);

            var sections = new List<string>();
            store.SectionNames(sections);
            var quotaCount = 0;
            quotaStates.Begin();
            foreach (var section in sections)
            {
                string poolName;
                if (!TryReservePool(section, out poolName)) continue;
                var pool = GetOrAddPool(poolName);
                foreach (var pair in store.ReadSection(section))
                {
                    if (pair.Key == ReservePool.DockedKey)
                    {
                        if (!bool.TryParse(pair.Value, out pool.Docked)) Log("bad setting '" + pair.Key + "=" + pair.Value + "' in [" + section + "]");
                        continue;
                    }
                    Quota q;
                    if (Quota.TryParse(pair.Value, out q))
                    {
                        var key = Category.ShortTypeId(pair.Key);
                        pool.SetQuota(key, q);
                        quotaCount++;
                        // A is inert on ore (SHEPHERD can't produce ore) and on an ingot Limit quota (ingots are
                        // never disassembled). The quota still loads — its move semantics stay valid.
                        if (q.AutomaticProduction)
                        {
                            if (key.Starts("Ore/"))
                                quotaStates.Push("A inert on " + key + " - can't produce ore");
                            else if (q.Mode == QuotaMode.Limit && key.Starts("Ingot/"))
                                quotaStates.Push("A inert on " + key + "L - ingots never disassemble");
                        }
                    }
                    else Log("bad quota '" + pair.Key + "=" + pair.Value + "' in [" + section + "]");
                }
            }
            quotaStates.Commit();

            var overrideCount = 0;
            foreach (var pair in store.ReadSection(CategoriesSection))
            {
                if (Category.IsValidOverride(pair.Key, pair.Value)) { categoryOverrides.Set(pair.Key, pair.Value); overrideCount++; }
                else Log("bad category '" + pair.Key + "=" + pair.Value + "' in [" + CategoriesSection + "]");
            }

            // An unknown key is reported, not skipped, so a typo (Priority=) can't silently leave the default. A
            // number applies only when it parses and is in range: a failed parse would read as 0, and parkSim=0 or a
            // fill of 0 switches something off — a typo must never do that.
            foreach (var pair in store.ReadSection(SettingsSection))
            {
                var i = Array.IndexOf(SettingKeys, pair.Key);
                if (i < 0) { Log("unknown setting '" + pair.Key + "' in [" + SettingsSection + "]"); continue; }
                var n = 0;
                var b = false;
                var v = 0.0;
                var ok = i == Priority ? int.TryParse(pair.Value, out n)
                    : IsSwitch(i) ? bool.TryParse(pair.Value, out b)
                    : double.TryParse(pair.Value, out v) && v >= SettingRules[3 * i] && v <= SettingRules[3 * i + 1];
                if (ok) settings[i] = i == Priority ? n : IsSwitch(i) ? (b ? 1 : 0) : v;
                else Log("bad setting '" + pair.Key + "=" + pair.Value + "' in [" + SettingsSection + "]");
            }
            // Resuming below the park bar would let one sample park and the next resume, round and round.
            if (settings[ResumeSim] < settings[ParkSim])
            {
                Log("resumeSim below parkSim in [" + SettingsSection + "] - both default");
                settings[ParkSim] = Pacer.DefaultParkSim;
                settings[ResumeSim] = Pacer.DefaultResumeSim;
            }
            ApplyPacerSettings();
            // Only asks: this document is discarded, and the write rides the save tick like every other flush.
            SettingsEntries();
            if (store.AddMissing(SettingsSection, settingsEntries)) fillSettings = flushPending = true;

            // Re-seed the craft tracking state from disk: a recompile must tell OUR disassembler from a user's
            // deliberate Disassembly mode, and a hand-deleted entry must actually release the assembler.
            craftPlanner.TrackedDisassembler = 0;
            foreach (var pair in store.ReadSection(ProductionSection))
            {
                long id;
                if (pair.Key == DisassemblerKey && long.TryParse(pair.Value, out id))
                    craftPlanner.TrackedDisassembler = id;
            }
            persistedDisassembler = craftPlanner.TrackedDisassembler;

            var factCount = 0;
            foreach (var pair in store.ReadSection(RecipesSection))
            {
                if (productionFacts.LoadRecipe(pair.Key, pair.Value)) factCount++;
                else Log("bad recipe '" + pair.Key + "=" + pair.Value + "'");
            }
            foreach (var pair in store.ReadSection(YieldsSection))
            {
                if (productionFacts.LoadYield(pair.Key, pair.Value)) factCount++;
                else Log("bad yield '" + pair.Key + "=" + pair.Value + "'");
            }

            foreach (var pool in reservePools.Values) pool.RecomputeCategories(categoryOverrides);
            RebuildReserveFillEntries();
            lastFlushSignature = store.Signature();
            return "reloaded " + reservePools.Count + " pool" + (reservePools.Count == 1 ? "" : "s")
                 + ", " + quotaCount + " quotas, " + overrideCount + " overrides, " + factCount + " facts"
                 // Said outright, since a setting that didn't take shows only as behaviour that didn't change.
                 + "\npriority " + settings[Priority] + (On(FactionShare) ? ", factionShare" : "") + (On(DockedProduction) ? ", dockedProduction" : "")
                 + (On(DockedFuel) ? ", dockedFuel" : "")
                 + "\npeakMs " + settings[PeakMs] + ", parkSim " + settings[ParkSim] + ", resumeSim " + settings[ResumeSim]
                 + ", backoffSim " + settings[BackoffSim]
                 + "\nspikeMs " + settings[SpikeMs] + ", spikeWindowSeconds " + settings[SpikeWindow] + ", startupSeconds " + settings[Startup]
                 + "\nfill % " + settings[ReactorFill] + "/" + settings[GasGenFill] + "/" + settings[RefineryFill]
                 + ", bottles " + settings[BottleSweep] + " min, pass " + settings[SortPass] + " s, UTC" + (settings[UtcOffset] < 0 ? "" : "+") + settings[UtcOffset];
        }

        static bool IsSwitch(int setting) => setting > Priority && setting <= DockedFuel;

        bool On(int setting) => settings[setting] != 0;

        void ApplyPacerSettings()
        {
            pacer.SetBars(stats.BarMs = settings[PeakMs], settings[ParkSim], settings[ResumeSim], settings[BackoffSim]);
            pacer.SetSpike(settings[SpikeMs], settings[SpikeWindow]);
        }

        // The one writer of Me.CustomData: re-parse the live document (preserving the user's hand-authored sections
        // and comments), rewrite only the SHEPHERD-managed sections from the in-memory model, and write back only if
        // the managed set actually changed (dirty-gated on the signature). Its callers fire only on a change edge (a
        // learned fact, the tracked disassembler acquired or released), because the re-parse and re-serialize scale
        // with the whole Custom Data. A genuine parse failure aborts rather than clobbering the user's file.
        // Dry builds the text and writes and remembers nothing.
        void FlushConfig(bool dry)
        {
            var doc = new MyIniDocument();
            string error;
            if (!doc.TryParse(Me.CustomData, out error)) { Log("can't save config: " + error); return; }
            var store = new ConfigStore(doc, CategoriesSection, ProductionSection, RecipesSection, YieldsSection);
            var learned = learnCommit && !dry;
            // The override map changes only on leaving learn mode; rewriting it at any other flush would delete lines
            // added since the last reload — a hand edit, or another program's — before configReload could read them.
            if (learned) store.MergeSection(CategoriesSection, categoryOverrides.Entries);
            productionEntries.Empty();
            if (persistedDisassembler != 0)
                productionEntries.Push(new KeyValuePair<string, string>(DisassemblerKey, persistedDisassembler.ToString()));
            store.MergeSection(ProductionSection, productionEntries);
            productionFacts.RecipeEntries(productionEntries);
            store.MergeSection(RecipesSection, productionEntries);
            productionFacts.YieldEntries(productionEntries);
            store.MergeSection(YieldsSection, productionEntries);
            // The signature covers only the managed sections, so learned reserve lines force the write.
            if (learned) foreach (var line in learner.QuotaLines) store.SetItem(line.Section, line.Key, line.Value);
            if (!dry) learnCommit = false;
            SettingsEntries();
            var filled = (fillSettings || dry) && store.AddMissing(SettingsSection, settingsEntries);
            if (!dry) fillSettings = false;
            var signature = store.Signature();
            if (dry) { store.ToText(); return; }
            if (signature == lastFlushSignature && !learned && !filled) return;
            Me.CustomData = store.ToText();
            lastFlushSignature = signature;
        }

        // Every [Settings] key with the value in effect, in the guide's order. AddMissing writes only the keys the
        // user left out, so what lands in Custom Data is each one's default.
        void SettingsEntries()
        {
            settingsEntries.Empty();
            for (var i = 0; i < settings.Length; i++)
                // bool.ToString() is "False"; the guide and the parser's examples use lower case.
                settingsEntries.Push(new KeyValuePair<string, string>(SettingKeys[i], IsSwitch(i) ? (On(i) ? "true" : "false") : settings[i].ToString()));
        }

        // A [Reserve] section maps to the default pool (""), [Reserve:Name] to that named pool. The "Reserve"
        // keyword is matched case-insensitively; the name after ':' is verbatim (it must equal the tag's pool). A
        // section that merely shares the prefix (ReserveXYZ) is not a reserve section.
        static bool TryReservePool(string section, out string pool)
        {
            pool = null;
            if (section == null || !section.StartsAnyCase("Reserve")) return false;
            var after = section.Cut("Reserve".Length);
            if (after.Length == 0) { pool = ""; return true; }
            if (after[0] == ':') { pool = after.Cut(1); return true; }
            return false;
        }

        ReservePool GetOrAddPool(string name)
        {
            ReservePool pool;
            if (!reservePools.Get(name, out pool)) { pool = new ReservePool(name); reservePools[name] = pool; }
            return pool;
        }

        // Rebuild the status LCD's reserve entries — one per configured pool with quotas. Called on configReload
        // (the only time the pool set changes), so a status render just refills fractions and never allocates.
        void RebuildReserveFillEntries()
        {
            reserveFillPools.Empty();
            reserveFills.Empty();
            foreach (var pool in reservePools.Values)
            {
                if (pool.QuotaCount == 0) continue;
                reserveFillPools.Push(pool);
                reserveFills.Push(new CategoryFill(ReservePool.Label(pool.Name)));
            }
        }

        // One LCD panel's rendering, rotating status→debug→log→each levels LCD→each stock LCD. Its caller re-emits the
        // Echo pane once per run (SE clears it each run, and every Echo in a run appends) — only the STABLE welcome
        // banner, never the live profiler/log (those change every run and flash unreadably at Update1; their home is
        // the LCDs).
        static readonly string[] Panels = { "status", "debug", "log", "levels", "stock" };

        void RenderTick()
        {
            var levels = 3 + (reserveLcds?.Count ?? 0);
            var panels = levels + (stockLcds?.Count ?? 0);
            if (renderCursor >= panels) renderCursor = 0; // the scan dropped an LCD since the last render
            lastPanel = Panels[renderCursor < 3 ? renderCursor : renderCursor < levels ? 3 : 4];
            if (renderCursor == 0) RenderStatusLcd();
            else if (renderCursor == 1) RenderDebugLcd();
            else if (renderCursor == 2) RenderLogLcd();
            else if (renderCursor < levels) RenderLevelsLcd(reserveLcds[renderCursor - 3]);
            else RenderStockLcd(stockLcds[renderCursor - levels]);
            renderCursor = renderCursor + 1 >= panels ? 0 : renderCursor + 1;
        }

        // A stock LCD lists, for each category its tag names, every item of it held anywhere SHEPHERD manages, and for
        // a * the contents of the catch-all containers — the ledger's stock tally, so it costs no inventory walk of its own. While a tally is half-walked the panel keeps
        // its last frame. The keys are retaken lazily here rather than on the plan tick, which can already run long,
        // and cost nothing on a grid without a stock LCD.
        void RenderStockLcd(Destination lcd)
        {
            if (!ledgerJob.IsDone) return;
            if (stockStale)
            {
                stockStale = false;
                productionLedger.CollectKeys(stockKeys);
                stockKeys.SortOrdinal();
                stockCategories.Empty();
                foreach (var key in stockKeys) stockCategories.Push(Category.ForKey(key, categoryOverrides));
            }
            renderBuffer.Empty();
            foreach (var category in lcd.AcceptedCategories)
                LevelsView.Render(renderBuffer, category, stockKeys, stockCategories, productionLedger);
            if (lcd.IsCatchAll) LevelsView.Render(renderBuffer, null, stockKeys, stockCategories, productionLedger);
            WriteSurface(lcd.Block, renderBuffer.Length > 0 ? renderBuffer.ToString() : "name a category", true);
        }

        // A levels LCD shows the one pool its tag names, the same way a reserve container joins the pool its tag
        // names — so a bare tag means the default pool, never every pool (the status panel is the all-pools view).
        void RenderLevelsLcd(ReserveContainer lcd)
        {
            renderBuffer.Empty();
            ReservePool pool;
            if (reservePools.Get(lcd.Pool, out pool) && (pool.QuotaCount > 0 || pool.Containers > 0))
                LevelsView.Render(renderBuffer, pool);
            else renderBuffer.Put(ReservePool.Label(lcd.Pool)).Put(" · no quotas, no container");
            WriteSurface(lcd.Block, renderBuffer.ToString(), true);
        }

        // A standing-by PB's status, debug and log panels, one per tick, drawn only while the leader is on another
        // construct: the leader draws just its own construct's panels, so a docked ship's panels would otherwise
        // freeze on their last frame, still reading working. Status gets a stand-by notice — no fills or production
        // lines, which would be stale without a scan; debug and log stay themselves (this PB still runs every tick,
        // so its run times are live; its scan counts are from before it stood by). The panels are the ones this PB's
        // last scan found.
        void RenderStandingBy()
        {
            renderCursor = renderCursor >= 2 ? 0 : renderCursor + 1;
            lastPanel = Panels[renderCursor];
            if (renderCursor == 1) { RenderDebugLcd(); return; }
            if (renderCursor == 2) { RenderLogLcd(); return; }
            PaintLights(Color.Lime);
            if (statusLcd == null) return;
            StatusView.Render(renderBuffer, ScriptVersion, Me.CustomName, "standing by", Clock.Hms(CurrentTime()), null, null, standbyMessages);
            WriteSurface(statusLcd, renderBuffer.ToString(), true);
        }

        // The [SHEPHERD-DEBUG] profiler surface. Header + per-kind ms table: peak = worst single run · avg50 =
        // rolling mean over the last 50 runs (the limiter's average-bar window, but including GC-coincident runs
        // the limiter drops, so an upper bound) · thr xN P% = breach back-offs and their share of the window · wary
        // xN P% = the same for the sim park's wary stage; ftch = the GetBlocksOfType sweep;
        // scan = a classify batch; bld = a build step; xfer = the per-transfer cost; plan = the production-plan
        // tick; save = the Custom Data rewrite; cmd = a command run; othr = the cheap Step/idle ticks.
        void RenderDebugLcd()
        {
            Profiler.Render(renderBuffer, ScriptVersion, Me.CustomName, DebugState(), CadenceLabel(), cycleCount, lastManaged, lastDest, pacer.Backoffs, pacer.BackoffTime, pacer.Waries, pacer.WaryTime, pacer.SimInstant, pacer.SimAvg, stats);
            WriteSurface(debugLcd, renderBuffer.ToString(), true); // monospace → the table columns line up
        }

        string DebugState() => leader != null ? "standing by" : Health(true);

        // THROTTLED stays off the status panel — a breach back-off is common and expected (mostly limiter-dropped GC stalls), so it would just be noise there.
        string Health(bool debug) =>
            pacer.Halted ? "HALTED" : learnMode ? "learning" : debug && pacer.Throttled ? "THROTTLED" : activelyWorking ? "working" : "idle";

        // The wake rate currently in effect. Paired with the state word: THROTTLED U10 = a breach back-off, the sim
        // park's wary stage (the thr/wary shares tell which), a spike hold or a stand-down; THROTTLED U100 = a full
        // park (a second breach inside the window, the sim park); working U10 = a trickle, working U1 = a backlog,
        // idle U10 = caught up.
        string CadenceLabel()
        {
            var f = Runtime.UpdateFrequency;
            return (f & UpdateFrequency.Update100) != 0 ? "U100"
                 : (f & UpdateFrequency.Update10) != 0 ? "U10"
                 : (f & UpdateFrequency.Update1) != 0 ? "U1" : "U0";
        }

        // The [SHEPHERD-LOG] surface: rewrite only when the buffer changed, off the shared StringBuilder so a
        // steady-state render allocates only the one WriteText string.
        void RenderLogLcd()
        {
            if (logLcd == null || !messageLog.Dirty) return;
            messageLog.Render(renderBuffer);
            WriteSurface(logLcd, renderBuffer.ToString(), false);
            messageLog.ClearDirty();
        }

        // The [SHEPHERD] status LCD: the glanceable health view — version + health word + a clock liveness stamp
        // + per-category fill bars, then a rule and per-reserve-pool quota-fill bars — as ONE monospace text write
        // (not sprites). Category fills read live container volumes off the scan; reserve fills read the pools'
        // quota model. Runtime numbers stay on DEBUG.
        void RenderStatusLcd()
        {
            PaintLights(pacer.Halted || pacer.StandingDown ? Color.Red : learnMode || NeedsReading() ? Color.Yellow : Color.Lime);
            if (statusLcd == null) return;
            BuildCategoryFills();
            BuildReserveFills();
            StatusView.Render(renderBuffer, ScriptVersion, Me.CustomName, Health(false), Clock.Hms(CurrentTime()), categoryFills, reserveFills, statusMessages,
                markCheck.Controlled, craftPlanner.TrackedDisassembler != 0);
            WriteSurface(statusLcd, renderBuffer.ToString(), true);
        }

        // The [SHEPHERD] lights carry the status panel's verdict across a base: red when the owner must act, yellow
        // while nothing moves on purpose or the panel lists a state line. Write-on-change, as Color syncs to every
        // client. A PB that stops running can't paint, so its lights keep their last colour.
        void PaintLights(Color colour)
        {
            if (lights != null)
                foreach (var light in lights)
                    if (light.Color != colour) light.Color = colour;
        }

        // Crafting and refining lines are the panel's normal work, not something to read.
        bool NeedsReading()
        {
            foreach (var lines in statusMessages)
                if (lines != production.Actions && lines.Count > 0) return true;
            return false;
        }

        // Aggregate each category's fill across the containers that accept it: explicit acceptors if any, else a
        // catch-all overflow marker (the figure itself is on the catch-all row), else no route. Refills the reused arrays/instances in place (zero per-render
        // allocation). This is a coarse volume readout, not a placement decision, so no CanItemsBeAdded probe.
        void BuildCategoryFills()
        {
            foreach (var f in categoryFills) { f.Cur = 0.0; f.Max = 0.0; f.Explicit = false; }
            var catchCur = 0.0;
            var catchMax = 0.0;
            var haveCatchAll = false;

            if (destinations != null)
                foreach (var dest in destinations)
                {
                    var cur = (double)(float)dest.Inventory.CurrentVolume;
                    var max = (double)(float)dest.Inventory.MaxVolume;
                    foreach (var cat in dest.AcceptedCategories)
                    {
                        var f = RowOf(cat);
                        if (f == null) continue;
                        f.Cur += cur; f.Max += max; f.Explicit = true;
                    }
                    if (dest.IsCatchAll) { catchCur += cur; catchMax += max; haveCatchAll = true; }
                }

            var catchFraction = catchMax > 0.0 ? catchCur / catchMax : 0.0;
            foreach (var f in categoryFills)
            {
                f.Overflow = false;
                if (f == catchAllRow) { f.HasRoute = true; f.Fraction = catchFraction; }
                else if (f.Explicit) { f.HasRoute = true; f.Fraction = f.Max > 0.0 ? f.Cur / f.Max : 0.0; }
                else if (haveCatchAll) { f.HasRoute = true; f.Overflow = true; f.Fraction = catchFraction; }
                else { f.HasRoute = false; f.Fraction = 0.0; }
            }
        }

        CategoryFill RowOf(string cat)
        {
            foreach (var f in categoryFills) if (f.Name == cat) return f;
            return null;
        }

        // A custom category gets its own row: every token on a home destination and every [Categories] target, so
        // one that items are routed to but no container takes shows ──no route── like a built-in. Sorted, so renaming
        // containers never reshuffles the panel.
        void RebuildCategoryRows()
        {
            customCategories.Empty();
            if (destinations != null)
                foreach (var dest in destinations)
                    foreach (var cat in dest.AcceptedCategories) Categories.AddCustom(customCategories, cat);
            foreach (var pair in categoryOverrides.Entries) Categories.AddCustom(customCategories, pair.Value);
            customCategories.SortOrdinal();
            var builtIns = Categories.All.Length;
            categoryFills.RemoveRange(builtIns, categoryFills.Count - builtIns);
            foreach (var cat in customCategories) categoryFills.Push(new CategoryFill(cat));
            if (destinations != null)
                foreach (var dest in destinations)
                    if (dest.IsCatchAll) { categoryFills.Push(catchAllRow); break; }
        }

        // Refill each shown pool's quota fill (have/want; may exceed 1 when over-stocked). HasRoute=false marks a
        // pool with no finite target (only All-mode quotas), rendered as a marker rather than a bar. Zero-alloc:
        // the entries are fixed until configReload; only the fractions move.
        void BuildReserveFills()
        {
            for (var i = 0; i < reserveFillPools.Count; i++)
            {
                var f = reserveFills[i];
                double fraction;
                f.HasRoute = reserveFillPools[i].TryQuotaFill(out fraction);
                f.Fraction = fraction;
            }
        }

        // Write one text blob to a block's primary text surface. Text mode, not sprites — a single WriteText is
        // cheap and atomic, unlike a sprite DrawFrame. ContentType/Font are set write-on-change.
        static void WriteSurface(IMyTerminalBlock block, string text, bool monospace)
        {
            var surface = block as IMyTextSurface ?? (block as IMyTextSurfaceProvider)?.GetSurface(0);
            if (surface == null) return;
            if (surface.ContentType != ContentType.TEXT_AND_IMAGE) surface.ContentType = ContentType.TEXT_AND_IMAGE;
            if (monospace && surface.Font != "Monospace") surface.Font = "Monospace";
            surface.WriteText(text);
        }

        // Rebuild the standing Echo banner: a stable welcome + a pointer to the LCDs (or, standing by, who leads and
        // how to take over — a stand-by PB sharing its leader's construct draws no panel, so the pane is its voice)
        // + the command list, optionally followed by the most recent command result. Cached in echoText and
        // re-emitted verbatim each tick, so the pane never flashes. commandResult persists until the next command or stand-by edge. The pane
        // reads square brackets as colour markup, so none may reach it — not even inside a block or grid name.
        void BuildEcho(string commandResult)
        {
            echoText = ("SHEPHERD v" + ScriptVersion
                     + (leader != null ? " standing by\n" + leader
                                         + " leads\nTo lead: a higher priority= in\nthe Settings section of Custom Data\n\n"
                        : learnMode ? " learning\nItems stay put. Run learn again\nto save and resume sorting\n\n"
                        : " running\nLive info on tagged LCD panels\n\n")
                     + "Commands: configReload, learn, statsReset"
                     + (commandResult != null ? "\n\n" + commandResult : "")).Replace('[', '(').Replace(']', ')');
        }

        // A user ran a command: refresh the banner with its result and emit it now — this run is not a tick, so
        // the scheduled loop won't repaint the pane until next tick.
        void ShowCommandResult(string result)
        {
            BuildEcho(result);
            Echo(echoText);
        }

        // One managed block's share of the cycle: its machine feed, its drain sources (with their ledger kinds and
        // count pools), and its assembler adapter.
        void BuildBlock(ManagedBlock mb)
        {
            var kind = Roles.FeedKindOf(mb.Block);
            AddFeed(mb, kind);
            // A configured reserve keeps its loadout and sheds only over-target excess / non-quota items (KeepQuota,
            // with its pool model); an unconfigured reserve (no quota section yet) is protected wholesale (KeepAll) so
            // tagging a container never empties it before its quotas exist. A pool with no quota section still gets a
            // model, so its levels LCD can list what it holds.
            var pool = mb.IsReserve ? GetOrAddPool(mb.ReservePool) : null;
            var reversed = Roles.IsDisassembling(mb.Block);
            var mode = Roles.InputDrainFor(mb.Role, reversed);
            if (yieldSample.Active && yieldSample.RefineryId == mb.Block.EntityId)
                mode = yieldSample.Drains ? DrainMode.All : DrainMode.KeepAll;
            // A docked ship's fuel is topped up, never taken: what it holds leaves with it.
            if (mb.Docked && mb.Role == Role.Consumer) mode = DrainMode.KeepAll;
            if (pool != null) mode = pool.QuotaCount > 0 ? DrainMode.KeepQuota : DrainMode.KeepAll;
            AddSource(mb.Input, mode, mode == DrainMode.KeepQuota ? pool : null, LedgerKindFor(mb, kind), pool);
            if (mb.Output != null)
            {
                var id = mb.Block.EntityId;
                var outMode = blueprintProbe.OwnsOutput(id) || yieldSample.Holds && yieldSample.RefineryId == id
                    ? DrainMode.KeepAll : Roles.OutputDrainFor(reversed);
                if (outMode == DrainMode.All)
                {
                    double at;
                    if (harvestedAt.Get(id, out at) && gameSeconds - at < HarvestSeconds) outMode = DrainMode.ToDemand;
                    else dueOutputs[builtSources] = id;
                }
                AddSource(mb.Output, outMode, null, LedgerKind.Plain, null);
            }
            var assembler = mb.Block as IMyAssembler;
            if (assembler != null) builtAssemblers.Push(new AssemblerState(assembler, mb.Construct, mb.Mark, productionQueueBuffer, itemBuffer, upgradeBuffer));
        }

        // A demand-first MachineFeed for a fed machine (reactor / gas generator / refinery) with a fill target above
        // 0. Each such home machine also has its vanilla conveyor auto push/pull turned off (write-on-change) so the
        // game stops topping it past our target; a docked one keeps the pilot's setting, which would otherwise leave
        // with the ship. A 0% kind feeds nothing and keeps its conveyor (vanilla still supplies it).
        void AddFeed(ManagedBlock mb, FeedKind kind)
        {
            if (kind == FeedKind.None) return;
            var category = Roles.FeedCategoryFor(kind);
            if (category == null) return;
            var pct = FillPctFor(kind);
            if (pct <= 0.0) return;
            // Yield learning keeps its refinery's feed even when it is off: the sample reports the stop itself.
            var learning = yieldSample.Active && yieldSample.RefineryId == mb.Block.EntityId;
            if (!learning && !Roles.IsOperational(mb.Block)) return;
            if (!mb.Docked) Roles.DisableConveyorSystem(mb.Block);
            var target = learning && (yieldSample.Phase == YieldPhase.Ready || yieldSample.Phase == YieldPhase.Feeding)
                ? (double)(float)mb.Input.MaxVolume : pct / 100.0 * (double)(float)mb.Input.MaxVolume;
            // A refinery's open feed must not take an ore yield learning is waiting on: greedy-feeding it
            // burns the sample unmeasured AND holds the loose stock below the evidence floor forever (machine
            // inputs are ledger-protected). Scoped to in-demand ores so a modded processor's unknown fuel ore
            // still feeds with zero config when nothing is trying to learn it.
            var feed = new MachineFeed(mb.Input, mb.Block.EntityId, mb.Block.CustomName, category, target,
                kind == FeedKind.Refinery ? openRefineryTakes : null);
            if (learning) yieldSample.Bind(feed);
            acceptors.Push(feed);
            if (kind == FeedKind.Refinery) refineryFeeds.Push(feed);
        }

        // The sortSources list is itself the pool: rebind the instances we already have and only allocate for the
        // overflow, so a stable-size grid allocates zero InventorySource per cycle after warm-up.
        void AddSource(IMyInventory inventory, DrainMode mode, ReservePool drainPool, LedgerKind kind, ReservePool countPool)
        {
            if (builtSources < sortSources.Count) ((InventorySource)sortSources[builtSources]).Rebind(inventory, mode, drainPool);
            else sortSources.Push(new InventorySource(inventory, itemBuffer, mode, drainPool, categoryOverrides));
            builtSources++;
            ledgerKinds.Push(kind);
            countPools.Push(countPool);
        }

        // Add one demand-first ReserveIntake per reserve container whose pool has quotas. The intake ranks in the
        // demand tier and self-limits on the pool's per-item deficit, counted by the ledger walk that follows. Config
        // problems (a dual-role block, a container with no quota section, a section with no container) are collected
        // for transition logging so the user is told rather than silently ignored.
        void AddReserveIntakes(ScanResult scan)
        {
            reserveStates.Begin();
            shipPoolStates.Begin();
            foreach (var pool in reservePools.Values) pool.Containers = 0;
            homePools.Empty();
            foreach (var reserve in scan.Reserves)
            {
                if (reserve.DualRole) reserveStates.Push(reserve.Block.CustomName + ": reserve ignores its [SHEPHERD:] tag");
                var pool = GetOrAddPool(reserve.Pool);
                pool.Containers++;
                if (!reserve.Docked) homePools.Push(reserve.Pool);
                if (pool.QuotaCount > 0) acceptors.Push(new ReserveIntake(reserve.Inventory, pool, reserve.Block.CustomName));
            }

            // A ship's pool is the ship's business: unconfigured here, its docked containers are simply kept whole, and
            // a pool flagged docked=true is expected to lose its containers whenever the ship is away. Those notes go
            // to the log, not the panel.
            foreach (var pool in reservePools.Values)
            {
                if (pool.Containers > 0 && pool.QuotaCount == 0)
                    (homePools.Contains(pool.Name) ? reserveStates : shipPoolStates).Push("reserve " + ReservePool.Label(pool.Name) + " has no quota section");
                else if (pool.QuotaCount > 0 && pool.Containers == 0 && !pool.AllAutomatic())
                    (pool.Docked ? shipPoolStates : reserveStates).Push("reserve " + ReservePool.Label(pool.Name) + " quotas unused - no container");
            }
            ReservePool.SharedAll(reservePools, allFirstPool, reserveStates);
            reserveStates.Commit();
            shipPoolStates.Commit();
        }

        double FillPctFor(FeedKind kind) =>
            kind == FeedKind.Reactor ? settings[ReactorFill]
          : kind == FeedKind.GasGen ? settings[GasGenFill]
          : settings[RefineryFill];

        // A fed machine's input contents are spoken for (protected from disassembly surplus); a working refinery's
        // input ore additionally counts as expected ingot output. Assemblers and plain storage tally as ordinary
        // stock — their queued work is accounted from the queues themselves. A machine tagged catch-all stays a
        // machine here: its feed protection outranks a stock-LCD readout.
        static LedgerKind LedgerKindFor(ManagedBlock mb, FeedKind kind) =>
            kind == FeedKind.Refinery && Roles.IsOperational(mb.Block) ? LedgerKind.Refining
          : kind != FeedKind.None ? LedgerKind.Protected
          : mb.CatchAll ? LedgerKind.CatchAll : LedgerKind.Plain;
    }

    // The live IIniDocument over MyIni. Trivial by design — its correctness is by inspection, while the merge
    // logic it backs is unit-tested game-free (ConfigStore/Persist.cs). Empty or blank text parses to an empty
    // document (MyIni rejects empty, but the first-ever write must start from a blank doc to Set into).
    public class MyIniDocument : IIniDocument
    {
        readonly MyIni ini = new MyIni();
        readonly List<MyIniKey> keyScratch = new List<MyIniKey>();

        public bool TryParse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return true;
            MyIniParseResult parse;
            if (ini.TryParse(text, out parse)) return true;
            error = parse.Error;
            return false;
        }

        public void SectionNames(List<string> into) => ini.GetSections(into);

        public void KeyNames(string section, List<string> into)
        {
            keyScratch.Empty();
            ini.GetKeys(section, keyScratch);
            foreach (var key in keyScratch) into.Push(key.Name);
        }

        public string Get(string section, string key)
        {
            var value = ini.Get(section, key);
            return value.IsEmpty ? null : value.ToString();
        }

        public void Set(string section, string key, string value) => ini.Set(section, key, value);

        public void DeleteKey(string section, string key) => ini.Delete(section, key);

        public string ToText() => ini.ToString();
    }
}
