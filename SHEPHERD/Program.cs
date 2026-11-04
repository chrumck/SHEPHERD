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
    // At most one transfer and one panel render per tick: the limiter charges a run's total.
    public partial class Program : MyGridProgram
    {
        #region mdk preserve
        // SHEPHERD inventory sorter. Copyright (C) 2026 Tomasz Dobrowolski. GPL-3.0-or-later, without any
        // warranty. Source and full license: github.com/chrumck/SHEPHERD
        // Setup, tags, and categories: README.md.
        // Settings live in this programmable block's Custom Data, [Settings] section (README "Settings"),
        // so a script update never resets them.
        #endregion

        const string ScriptVersion = "1.0.46";

        // Each item examined and each source finished draws it down, so one Step() is bounded whatever the grid size.
        // Higher shortens the idle full-scan cycle but pushes on both the peak and the average bar.
        const int ExamineBudget = 100;

        // Bounds each classify tick so a scan run can't grow past the peak bar with grid size.
        const int ClassifyBudget = 200;
        const double DiscoverYieldFloor = 100.0;
        const double DiscoverYieldSample = 110.0;

        // The probe is cheap, so its interval is set by how long a stale scope is tolerable; the cooldown bounds a
        // grid dismantled block by block, where every probe would otherwise discard the pass that just restarted.
        const double ScopeProbeSeconds = 2.0;
        const double ScopeTripCooldownSeconds = 10.0;

        // On a large conveyor network transfers sit just over the peak bar every few seconds; logging each back-off
        // pushed every other message out of the log.
        const double BackoffLogQuietSeconds = 60.0;
        const double StartupLogSeconds = 180.0;    // how long after a start every run over the peak bar is logged
        const double RunGapLogSeconds = 5.0;       // 3x the slowest cadence's gap
        // First calls compile for tens of ms each and spike pacing allows only two long runs per window, so a start's
        // packed run takes steps up to this to fit several cold calls in one run.
        const double PackMs = 150.0;
        // Well inside the limiter's per-PB startup allowance (about a minute), so the cold first calls land in it.
        const double DefaultStartupSeconds = 45.0;

        // Fewer moves are a trickle the idle cadence keeps under half the limiter's average bar even at the heaviest
        // transfers; past it, Update1's cheap ticks are what dilute the average.
        const int BacklogMoves = 8;

        // Unless a demand target wants the stack: one transfer moves a whole stack, so waiting turns a move every
        // cycle into one per interval.
        const double HarvestSeconds = 15.0;

        // The limiter passes this (::-1 peak, ::-2 average) at its last offense before punishing.
        const string GracefulShutDownArg = "GracefulShutDown::";

        const int LogCapacity = 30;

        // Across connectors because a PB manages its docked grids too; a receiver narrows that to the PBs that would
        // manage the same blocks (StandingBy).
        const string LeadChannel = "SHEPHERD";
        const TransmissionDistance LeadRange = TransmissionDistance.ConnectedConstructs;

        // The sections the writer owns. [Production] holds the one disassembler SHEPHERD switched, so a recompile can
        // tell it from a user's own Disassembly mode. The user's sections and comments are left untouched, except that
        // a learn save sets the learned keys in [Reserve...].
        const string CategoriesSection = "Categories";
        const string ProductionSection = "Production";
        const string RecipesSection = "Recipes";
        const string YieldsSection = "Yields";
        const string DisassemblerKey = "disassembler";
        // The user's section; SHEPHERD only adds the keys it lacks, so every setting is listed in Custom Data.
        const string SettingsSection = "Settings";
        // In the README's order, which AddMissing keeps when it appends missing keys. Each rule is {low, high,
        // default}; priority is any whole number and the three switches are true/false.
        static readonly string[] SettingKeys = ("utcOffsetHours,reactorFillPct,gasGenFillPct,refineryFillPct,bottleSweepMinutes,"
            + "sortPassSeconds,priority,factionShare,dockedProduction,dockedFuel,peakMs,parkSim,resumeSim,backoffSim,spikeMs,"
            + "spikeWindowSeconds,startupSeconds,statusWrapChars").Split(',');
        const int UtcOffset = 0, ReactorFill = 1, GasGenFill = 2, RefineryFill = 3, BottleSweep = 4, SortPass = 5, Priority = 6,
            FactionShare = 7, DockedProduction = 8, DockedFuel = 9, PeakMs = 10, ParkSim = 11, ResumeSim = 12, BackoffSim = 13,
            SpikeMs = 14, SpikeWindow = 15, Startup = 16, StatusWrap = 17;
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
            0, 1e6, DefaultStartupSeconds,
            0, 1000, 40
        };

        readonly List<IMyTerminalBlock> blockBuffer = new List<IMyTerminalBlock>();
        // Its own buffer, not blockBuffer's: the classify cursor iterates blockBuffer across ticks, so re-counting
        // into it mid-pass would shift the list under the cursor.
        readonly List<IMyTerminalBlock> scopeBuffer = new List<IMyTerminalBlock>();
        readonly List<IMyShipConnector> connectorBuffer = new List<IMyShipConnector>();
        // Rebuilt at each fetch, and on each heartbeat while standing by, when no fetch runs.
        readonly ManagedScope scope = new ManagedScope();
        // The tripwire's reading, kept apart from scope, which the in-flight classify reads.
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
        // Unknown-yield ores that yield learning waits on, withheld from open greedy feeds so the sample can build up.
        // Empty without demand, so unknown ores (e.g. a modded processor's fuel) feed freely.
        readonly HashSet<string> sampleReservedOres = new HashSet<string>();
        readonly ProductionLedger productionLedger = new ProductionLedger();
        readonly LedgerJob ledgerJob;    // pooled: re-armed per cycle, never reallocated
        readonly CraftPlanner craftPlanner = new CraftPlanner();
        readonly CraftJob craftJob = new CraftJob();
        readonly RefinePlanner refinePlanner = new RefinePlanner();
        readonly DiscoveryCoordinator discovery = new DiscoveryCoordinator();
        readonly BlueprintProbe blueprintProbe = new BlueprintProbe();
        readonly MarkCheck markCheck = new MarkCheck();
        readonly YieldSample yieldSample = new YieldSample();
        readonly Dictionary<string, double> yieldInput = new Dictionary<string, double>();
        readonly Dictionary<string, double> yieldOutput = new Dictionary<string, double>();
        readonly List<IRefineryFeed> refineryFeeds = new List<IRefineryFeed>();
        readonly LevelFill[] levelFills = new LevelFill[4];   // by FeedKind, fresh each build
        readonly Func<string, bool> ingotRoomProbe;   // bound once, so a plan allocates no delegate
        readonly PhaseJob planJob;
        bool planHalf;      // the plan's craft half ran; the refine half is next
        readonly ProductionStatus production;
        readonly List<KeyValuePair<string, string>> productionEntries = new List<KeyValuePair<string, string>>();
        readonly List<KeyValuePair<string, string>> settingsEntries = new List<KeyValuePair<string, string>>();
        // Set by a reload that found keys missing. The fill uses the values that reload read, so only the flush right
        // after it may fill: a later flush (a learned fact) could re-add a line the user has since deleted.
        bool fillSettings;
        RoutingIndex cycleRouting;       // built at the build phase, consumed when the plan tick enqueues the sort
        List<ManagedBlock> managedBlocks;
        long persistedDisassembler;      // the tracking state as last written, so a flush fires only on the edge
        readonly double[] settings = new double[18];
        readonly PeerElection election = new PeerElection();
        readonly IMyBroadcastListener leadListener;
        string leader;                   // the live peer this PB stands by for; null while it does the work
        // Until the dry cycle has run, an outranked PB keeps working: each path's slow first run (its methods compile)
        // is free inside the limiter's startup allowance, but a stand-by would defer it to a resume long after.
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

        readonly ClassifyCursor classifyCursor = new ClassifyCursor();
        readonly CycleDriver driver = new CycleDriver();
        readonly PhaseJob fetchJob;      // the phase jobs are pooled: re-armed per cycle, never reallocated
        readonly PhaseJob classifyJob;
        readonly PhaseJob buildJob;
        List<IBottleTank> bottleTanks;
        // Seeded past any interval so the first sweep is due on startup: users expect bottles filled right away.
        double secsSinceBottleSweep = 1e9;
        bool activelyWorking;      // true while a sweep is moving items — the health word; a clean pass clears it
        bool backlog;              // moves are queueing faster than a trickle — the only thing that earns Update1
        // Outputs due this cycle map sort-source index → processor id. A due output is stamped only once a sort pass
        // has walked it, so a capped pass that never reached it leaves it due.
        readonly Dictionary<long, double> harvestedAt = new Dictionary<long, double>();
        readonly Dictionary<int, long> dueOutputs = new Dictionary<int, long>();
        RunKind lastTickKind;      // what last tick did → THIS tick's LastRunTimeMs is that kind's cost (one tick late)
        // What last tick did beyond its kind, for the slow and throttling lines: only the job tells our own cold path
        // from an outside stall in a catch-all othr run.
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
        // One StateChannel per recompute cadence, so no channel prunes another's states. Every channel logs entry
        // edges; only those in statusMessages also render.
        readonly StateChannel learnStates;     // each learn sweep: the preview of what leaving the mode would save
        readonly StateChannel learnSkips;      // each learn sweep: tagged containers that don't teach, and why (log-only)
        readonly StateChannel noRouteStates;   // sort lap; log-only, since the category row already shows ──no route──
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
        // Keyed by pool name ("" = the bare pool). Read only at startup and on configReload, never per cycle.
        readonly Dictionary<string, ReservePool> reservePools = new Dictionary<string, ReservePool>();
        // Read only at startup and on configReload, never per cycle; memory is the source of truth.
        readonly CategoryOverrides categoryOverrides = new CategoryOverrides();
        // Signature of the managed sections as last read/written, so a flush that would change nothing is skipped.
        string lastFlushSignature = "";
        bool flushPending;         // a managed-section change awaiting its own Save tick
        // In memory only: a recompile leaves learn mode and drops what was not saved, but the arrangement is still in
        // the containers to teach again.
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
        int renderCursor;          // rotates the panels so at most one is built per tick
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
        // The fill rows change only at a cycle's build and are refilled in place, so a status render allocates only
        // the final WriteText string.
        List<Destination> destinations;
        readonly List<CategoryFill> categoryFills = new List<CategoryFill>();
        readonly List<string> customCategories = new List<string>();
        // The last row while any [SHEPHERD:*] container exists: the summed catch-all fill, the one figure every
        // category without its own container shares.
        readonly CategoryFill catchAllRow = new CategoryFill("catch-all");
        // The pool set changes only on configReload, so the entries are rebuilt there and a render only refills them.
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

            // The cold first runs must be over within the limiter's startup allowance.
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

            // The next tick reads this run's ms, so book the previous tick's sample now, else a command's cost lands
            // on whatever the last tick did.
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
                cycleCount = 0; // else lifetime cycles beside a just-zeroed uptime reads as nonsense
                ShowCommandResult("run stats reset");
            }
            // A bare run (a timer or button with no argument) is not a mistyped command.
            else if (!string.IsNullOrEmpty(argument)) ShowCommandResult("unrecognized command");
            // TimeSinceLastRun on the next tick counts from THIS run, so the wall clock must too — else the sim
            // estimate's game/wall intervals diverge and a command run reads as a spurious lag dip.
            lastRunWallClock = DateTime.UtcNow;
            runInstructions = Runtime.CurrentInstructionCount;
        }

        // The CycleDriver steps one job per run, so no two expensive operations share a run.
        void Tick()
        {
            // A PB can't read its own in-run time, so this is the previous tick's cost.
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
            lastTickKind = RunKind.Other;

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

            // Only on a plain cheap tick: a heavy tick keeps its headroom for a GC pause, and the plan and save rows
            // measure only themselves. Until every panel has drawn once, each first render is a slow run of its own,
            // so they ride any run with room. A warming PB leaves the panels to a leader on its own construct.
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

        // Log only, never the status panel: throttling is common (mostly GC stalls the limiter drops). Only an
        // escalation and the first back-off after a quiet spell are logged, else routine ones bury every other
        // message. prevKind names the run that armed it, since a back-off arms only in Observe off lastRunMs. Every
        // tick calls it, standing by too, so the counters never lag. The wall seconds since start, as the limiter's
        // startup allowance counts them, tell which cold run missed that allowance.
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

        // A stall the server can absorb, and far from the instruction cap, which aborts a script outright: room for
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
                // Not between the plan's halves: a blueprint learned there would leave the craft half's needs stale.
                if (blueprintProbe.NeedsObservation && !pacer.Halted && !pacer.Throttled && !planHalf)
                {
                    discoveryObserveTurn = !discoveryObserveTurn;
                    if (discoveryObserveTurn) { lastJob = "probe"; ObserveBlueprint(); return true; }
                }
                // A back-off or stand-down holds the scan and save too, not just transfers. A start's run that began
                // allowed goes on past its first heavy call, the election's scope scan included.
                if (!pacer.AllowHeavy && !(starting && heavyRun && Room())) return false;

                // The rewrite re-serializes the whole Custom Data, so it gets a tick of its own and the refractory,
                // except in a start, which the limiter doesn't check.
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
                    // A sweep keeps the fast cadence throughout but never counts as moves: EndCycle's idle decision
                    // follows the sort alone.
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
                        // An allocation burst, armed like a heavy call so no two batches (nor a batch and a transfer)
                        // fire back-to-back.
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
                        // A dead target costs a transfer but is no progress, so a stall-only cycle parks at idle
                        // rather than hammering it at the fast cadence.
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
                        // No refractory: find and its commit run back-to-back; only the commit is heavy.
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
                    // A completion handler that enqueues a follow-up (bottles) keeps the cycle open.
                    if (driver.LastCompleted != null && !driver.HasWork) EndCycle(dryEnd);
                }
                // A method compiles on its first call, so a start's first steps are slow; several share a run for fewer
                // runs over the spike bar. Never past a step that found or moved items (one transfer per run), except
                // in the dry sweep, whose moves skip the engine call.
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

        // The election runs ahead of the tick's heavy gate, so its scans take the gate themselves: a cold scan inside a
        // spike hold, or sharing a run with the first fetch, can be the third run over 10 ms that punishes. Until an
        // allowed tick the PB has no work to lead with anyway.
        void ScanScopeAsHeavy()
        {
            if (!pacer.AllowHeavy) return;
            RefreshScope(scope);
            lastTickKind = RunKind.Fetch;
            lastJob = "scope";
            pacer.NotifyHeavyFired();
        }

        // A construct count change trips too: a small ship's undock is under the shrink bar, yet every transfer to it
        // would fail, and a new dock is picked up without waiting out the pass. A trip keeps discovery state, like a
        // stand-down: those operations re-validate against every fresh scan.
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

        // Batched so its cost stays flat as the grid grows; what other ticks read between the steps changes only on
        // the last.
        bool BuildPhase()
        {
            var scan = classifyCursor.Result;
            var managed = scan.Managed;
            if (buildCursor == 0)
            {
                acceptors.Empty();
                foreach (var dest in scan.Destinations) acceptors.Push(dest);
                refineryFeeds.Empty();
                for (var k = 0; k < 4; k++) levelFills[k] = new LevelFill();
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

        // Craft actions go ahead of the sort, so a planned queue change lands the same cycle it was decided. Each planner
        // walks every item key the ledger saw, so a base holding many kinds of item takes the two in separate runs, or
        // together they pass the peak bar.
        bool PlanPhase()
        {
            if (!planHalf)
            {
                craftPlanner.Plan(productionLedger, productionFacts, ledgerJob.Snapshots,
                    blueprintProbe.ReservedAssemblerId, gameSeconds);
                planHalf = true;
                return false;
            }
            planHalf = false;
            refinePlanner.Plan(productionLedger, productionFacts, refineryFeeds, ingotRoomProbe, DiscoverYieldSample);
            // Before ore is spent a vanished deficit cancels the sample cheaply, but a fed one runs to completion: its
            // held output counts as ledger stock and would otherwise close its own deficit mid-measure.
            var sampleCommitted = yieldSample.Phase == YieldPhase.Feeding || yieldSample.Phase == YieldPhase.Measuring;
            if (yieldSample.Active && (sampleCommitted || productionLedger.RefineDeficit(yieldSample.Ingot) >= 1.0)
                && !refinePlanner.NeedYield.Contains(yieldSample.Ore)) refinePlanner.NeedYield.Push(yieldSample.Ore);
            // While the sample runs its ore's blocked lines are false: the other refineries can't take it, and the
            // delivered sample sits protected in the owned refinery's input, leaving loose stock short.
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

        // A method compiles on its first call, and the work a grid needs may come up hours after a start, past the
        // limiter's startup allowance. So every path runs once now with the engine calls skipped. Real work starts
        // next cycle, from a fresh count, since the dry claims leave this cycle's counts off. A dry cycle cut short is
        // queued once more, then given up, so a fault in it can't keep real work from ever starting.
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

        // Ingots with nowhere to land strand in refinery outputs; any routed acceptor with room counts. The cycle's
        // feeds are still unconstrained when this runs.
        bool IngotHasHome(string ingotKey)
        {
            if (cycleRouting == null) return false;
            var type = ItemKey.ToType(ingotKey);
            foreach (var target in cycleRouting.Route(Category.ForKey(ingotKey, categoryOverrides), Category.ForKey(ingotKey)))
                if (target.Accepts(type)) return true;
            return false;
        }

        // Only on the acquire/release edge: a flush rewrites the whole Custom Data.
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
                // Never persist uncertain knowledge; the learn re-triggers while the deficit stands.
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
            // The plan computed these before the learn landed; else one cycle shows missing-yield lines for it.
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

        void FetchBlocks()
        {
            blockBuffer.Empty();
            GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(blockBuffer);
        }

        // The reserve intakes go after the feeds: acceptor order is the routing tie-break.
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
                // An outranked PB plans only while warming up, so the plan's slow first pass lands in the limiter's
                // startup allowance.
                if (leader == null || !warmedUp)
                {
                    planJob.Rearm();
                    driver.Enqueue(planJob);   // it enqueues the craft actions and the sort
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

        // Null bottleTanks means no sweep this cycle, so FinishSort ends the cycle normally.
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

        // The lap's diagnostics commit only once every source has been visited. A dry pass harvested nothing, so it
        // stamps nothing and keeps the rotation, but commits the lap so that code has run too.
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
                // Two channels because a missing route already shows as its category's ──no route── row. Unreachables
                // are keyed by target and category, so a container filtering only some categories reports each.
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

        void FinishBottles(BottleJob job)
        {
            LogBottleStranded(job.StrandedTanks);
            bottleTanks = null;
            if (!job.Dry) secsSinceBottleSweep = 0.0;
        }

        void LogBottleStranded(List<string> current)
        {
            bottleStates.Begin();
            foreach (var name in current)
                bottleStates.Push("bottles stuck in " + name + " - storage full/unreachable");
            bottleStates.Commit();
        }

        // Only a backlog keeps the fast cadence, as more may be waiting. The dry cycle moves nothing, yet the first real
        // cycle after it should run as eagerly as a start.
        void EndCycle(bool eager)
        {
            cycleCount++;
            activelyWorking = movesThisCycle > 0 || learnMode || eager;
            // A learn sweep moves nothing, yet the preview should follow the user's rearranging promptly.
            backlog = movesThisCycle >= BacklogMoves || learnMode || eager;
        }

        // The limiter's last warning: drop the work, hold every heavy call and park until the offense window ages out.
        // A stand-down is pacing, not a fault, so discovery survives it; its state is rebuilt into fresh feeds every
        // cycle.
        void HandleGracefulShutDown(string argument)
        {
            pacer.StandDown();
            ResetWork();
            ApplyCadence(pacer.NextCadence(false));
            Log("standing down (" + argument + ")");
        }

        // Every PB advertises whatever its state, so each reaches the same verdict on its own. Entering stand-by drops
        // the cycle and discovery: it may last indefinitely, and the leader plans from its own view.
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

        // Discovery goes too: a crash may have come from it, and a reload changes the facts it was planned against.
        void ResetCycle()
        {
            ResetWork();
            discovery.Reset();
            blueprintProbe.Reset();
            yieldSample.Reset();
        }

        void ResetWork()
        {
            // Else every pass cut short restarts at the same source, and the later ones are never reached.
            var pass = driver.Current as SortJob;
            if (pass != null) sortStart = pass.NextStart;
            driver.Clear();
            planHalf = false;
            bottleTanks = null;
            classifyCursor.Reset();
            ResetLap();
            activelyWorking = backlog = false;
            // A queued probe's append rode the CraftJob just dropped and nothing re-issues it, so it would wait forever.
            if (blueprintProbe.Phase == BlueprintPhase.Queued) blueprintProbe.Reset();
        }

        void ResetLap()
        {
            lapNoRoute.Empty();
            lapUnreachable.Empty();
            lapFinished = 0;
        }

        // Keep messages short: string literals ship verbatim under minify.
        void Log(string message)
        {
            var line = Clock.HmsCentis(CurrentTime()) + " " + message;
            messageLog.Push(line);
        }

        // A server has no useful local time zone. Display only: never gates a decision.
        DateTime CurrentTime() => DateTime.UtcNow.AddHours(settings[UtcOffset]);

        // Never fast-loop a crashing step: repeated crashes with no clean run between park us.
        void HandleCrash(Exception e)
        {
            ResetCycle();
            pacer.NotifyCrash();
            Log("step failed: " + e.Message);
        }

        // Update100 is only for the safety parks: idle at Update10 notices new work (a ship docking) in seconds, and
        // idle runs are too cheap for the extra wakes to matter against the limiter's bars.
        void ApplyCadence(Cadence c)
        {
            var f = c == Cadence.Working ? UpdateFrequency.Update1
                  : c == Cadence.Parked ? UpdateFrequency.Update100
                  : UpdateFrequency.Update10;
            if (Runtime.UpdateFrequency != f) Runtime.UpdateFrequency = f;
        }

        // Only at startup and on configReload, never per cycle; a malformed line is logged and skipped, never thrown.
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
                 + ", bottles " + settings[BottleSweep] + " min, pass " + settings[SortPass] + " s, UTC" + (settings[UtcOffset] < 0 ? "" : "+") + settings[UtcOffset]
                 + ", wrap " + settings[StatusWrap];
        }

        static bool IsSwitch(int setting) => setting > Priority && setting <= DockedFuel;

        bool On(int setting) => settings[setting] != 0;

        void ApplyPacerSettings()
        {
            pacer.SetBars(stats.BarMs = settings[PeakMs], settings[ParkSim], settings[ResumeSim], settings[BackoffSim]);
            pacer.SetSpike(settings[SpikeMs], settings[SpikeWindow]);
        }

        // The one writer of Me.CustomData. It re-parses the live document so the user's sections and comments survive,
        // and a parse failure aborts rather than clobbering the user's file. Dry builds the text and keeps nothing.
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

        // AddMissing writes only the keys the user left out, so what lands in Custom Data is each one's default.
        void SettingsEntries()
        {
            settingsEntries.Empty();
            for (var i = 0; i < settings.Length; i++)
                // bool.ToString() is "False"; the README uses lower case.
                settingsEntries.Push(new KeyValuePair<string, string>(SettingKeys[i], IsSwitch(i) ? (On(i) ? "true" : "false") : settings[i].ToString()));
        }

        // The name after ':' is verbatim: it must equal the tag's pool.
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

        // Only configReload changes the pool set, so a status render just refills fractions and never allocates.
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

        // Reads the ledger's stock tally, so it walks no inventory; a half-walked tally keeps the last frame. The keys
        // are retaken here, not on the plan tick, which can already run long.
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

        // A bare tag means the default pool, as for a reserve container, never every pool: the status panel is the
        // all-pools view.
        void RenderLevelsLcd(ReserveContainer lcd)
        {
            renderBuffer.Empty();
            ReservePool pool;
            if (reservePools.Get(lcd.Pool, out pool) && (pool.QuotaCount > 0 || pool.Containers > 0))
                LevelsView.Render(renderBuffer, pool);
            else renderBuffer.Put(ReservePool.Label(lcd.Pool)).Put(" · no quotas, no container");
            WriteSurface(lcd.Block, renderBuffer.ToString(), true);
        }

        // Drawn only while the leader is on another construct: the leader draws just its own construct's panels, so a
        // docked ship's would freeze reading working. Status shows no fills or production lines, stale without a scan.
        void RenderStandingBy()
        {
            renderCursor = renderCursor >= 2 ? 0 : renderCursor + 1;
            lastPanel = Panels[renderCursor];
            if (renderCursor == 1) { RenderDebugLcd(); return; }
            if (renderCursor == 2) { RenderLogLcd(); return; }
            PaintLights(Color.Lime);
            if (statusLcd == null) return;
            StatusView.Render(renderBuffer, ScriptVersion, Me.CustomName, "standing by", Clock.Hms(CurrentTime()), null, null, standbyMessages,
                -1, false, (int)settings[StatusWrap]);
            WriteSurface(statusLcd, renderBuffer.ToString(), true);
        }

        void RenderDebugLcd()
        {
            Profiler.Render(renderBuffer, ScriptVersion, Me.CustomName, DebugState(), CadenceLabel(), cycleCount, lastManaged, lastDest, pacer.Backoffs, pacer.BackoffTime, pacer.Waries, pacer.WaryTime, pacer.SimInstant, pacer.SimAvg, stats);
            WriteSurface(debugLcd, renderBuffer.ToString(), true); // monospace → the table columns line up
        }

        string DebugState() => leader != null ? "standing by" : Health(true);

        // A breach back-off is common (mostly GC stalls the limiter drops), so THROTTLED would be noise on the status panel.
        string Health(bool debug) =>
            pacer.Halted ? "HALTED" : learnMode ? "learning" : debug && pacer.Throttled ? "THROTTLED" : activelyWorking ? "working" : "idle";

        string CadenceLabel()
        {
            var f = Runtime.UpdateFrequency;
            return (f & UpdateFrequency.Update100) != 0 ? "U100"
                 : (f & UpdateFrequency.Update10) != 0 ? "U10"
                 : (f & UpdateFrequency.Update1) != 0 ? "U1" : "U0";
        }

        void RenderLogLcd()
        {
            if (logLcd == null || !messageLog.Dirty) return;
            messageLog.Render(renderBuffer);
            WriteSurface(logLcd, renderBuffer.ToString(), false);
            messageLog.ClearDirty();
        }

        void RenderStatusLcd()
        {
            PaintLights(pacer.Halted || pacer.StandingDown ? Color.Red : learnMode || NeedsReading() ? Color.Yellow : Color.Lime);
            if (statusLcd == null) return;
            BuildCategoryFills();
            BuildReserveFills();
            StatusView.Render(renderBuffer, ScriptVersion, Me.CustomName, Health(false), Clock.Hms(CurrentTime()), categoryFills, reserveFills, statusMessages,
                markCheck.Controlled, craftPlanner.TrackedDisassembler != 0, (int)settings[StatusWrap]);
            WriteSurface(statusLcd, renderBuffer.ToString(), true);
        }

        // Write-on-change, as Color syncs to every client.
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

        // A coarse volume readout, not a placement decision, so no CanItemsBeAdded probe.
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

        // [Categories] targets get rows too, so one that no container takes shows ──no route── like a built-in. Sorted,
        // so renaming containers never reshuffles the panel.
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

        // HasRoute=false marks a pool with only All quotas, rendered as a marker rather than a bar.
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

        // Text, not sprites: a single WriteText is cheap and atomic, unlike a sprite DrawFrame.
        static void WriteSurface(IMyTerminalBlock block, string text, bool monospace)
        {
            var surface = block as IMyTextSurface ?? (block as IMyTextSurfaceProvider)?.GetSurface(0);
            if (surface == null) return;
            if (surface.ContentType != ContentType.TEXT_AND_IMAGE) surface.ContentType = ContentType.TEXT_AND_IMAGE;
            if (monospace && surface.Font != "Monospace") surface.Font = "Monospace";
            surface.WriteText(text);
        }

        // Stable, re-emitted verbatim once per run: SE clears the pane each run and every Echo appends, so live text
        // would flash. A stand-by PB on its leader's construct draws no panel, so the pane is its voice. The pane reads
        // square brackets as colour markup, so none may reach it, not even inside a block or grid name.
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

        // A command run is not a tick, so the loop won't repaint the pane until the next one.
        void ShowCommandResult(string result)
        {
            BuildEcho(result);
            Echo(echoText);
        }

        void BuildBlock(ManagedBlock mb)
        {
            var kind = Roles.FeedKindOf(mb.Block);
            AddFeed(mb, kind);
            // An unconfigured reserve is kept whole, so tagging a container never empties it before its quotas exist;
            // it still gets a pool, so its levels LCD can list what it holds.
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

        // A fed home machine loses its conveyor push/pull so the game stops topping it past our target; a docked one
        // keeps the pilot's setting, which would leave with the ship. A 0% kind keeps its conveyor.
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
            // Greedy-feeding an ore yield learning waits on burns the sample unmeasured and holds loose stock below
            // the evidence floor forever, since machine inputs are ledger-protected.
            var feed = new MachineFeed(mb.Input, mb.Block.EntityId, mb.Block.CustomName, category, target,
                kind == FeedKind.Refinery ? openRefineryTakes : null, learning ? null : levelFills[(int)kind]);
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

        // An intake self-limits on its pool's deficit, counted by the ledger walk that follows.
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

        // A fed machine's input is spoken for, so it is never disassembly surplus; a working refinery's ore also counts
        // as incoming ingots. A machine tagged catch-all stays a machine: feed protection outranks a stock-LCD readout.
        static LedgerKind LedgerKindFor(ManagedBlock mb, FeedKind kind) =>
            kind == FeedKind.Refinery && Roles.IsOperational(mb.Block) ? LedgerKind.Refining
          : kind != FeedKind.None ? LedgerKind.Protected
          : mb.CatchAll ? LedgerKind.CatchAll : LedgerKind.Plain;
    }

    // Trivial by design: the merge logic it backs is tested game-free. Blank text parses to an empty document, since
    // MyIni rejects it but the first write must start from a blank doc.
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
