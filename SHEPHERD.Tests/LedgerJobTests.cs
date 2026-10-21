using System.Collections.Generic;
using IngameScript;
using VRage;
using VRage.Game.ModAPI.Ingame;
using Xunit;

namespace SHEPHERD.Tests
{
    public class LedgerJobTests
    {
        const string Plate = "Component/SteelPlate";
        const string Iron = "Ingot/Iron";
        const string IronOre = "Ore/Iron";
        const string StoneOre = "Ore/Stone";

        // A ledger-walk source: (key, amount) stacks. The sort-facing members are inert — LedgerJob only reads
        // Refresh/Count/KeyAt/AmountAt.
        sealed class Stock : ISortSource
        {
            readonly List<string> keys = new List<string>();
            readonly List<double> amounts = new List<double>();
            public int Refreshes;

            public Stock Add(string key, double amount) { keys.Add(key); amounts.Add(amount); return this; }

            public void Refresh() { Refreshes++; }
            public int Count => keys.Count;
            public string CategoryAt(int index) => "";
            public string KeyAt(int index) => keys[index];
            public double AmountAt(int index) => amounts[index];
            public bool ShouldDrain(int index) => true;
            public bool DemandOnly => false;
            public bool IsSameAs(ICategoryAcceptor acceptor) => false;
            public bool CanPlaceAt(int index, ICategoryAcceptor target) => false;
            public void Prepare(int index, ICategoryAcceptor target) { }
            public CommitResult Commit() => CommitResult.Refused;
        }

        sealed class Assembler : IAssemblerState
        {
            readonly List<QueueEntry> queue = new List<QueueEntry>();
            public long Id { get; set; }
            public string Name => "A" + Id;
            public int Construct { get; set; }
            public bool IsReceiver { get; set; } = true;
            public AssemblerMark Mark { get; set; } = AssemblerMark.Main;
            public bool CanCooperate => true;
            public bool Cooperative { get; set; }
            public void SetCooperative(bool on) => Cooperative = on;
            public string Model => "LargeAssembler";
            public double Speed { get; set; } = 1;
            public bool IsWorking => true;
            public bool IsOperational { get; set; } = true;
            public bool IsDisassembling { get; set; }

            public Assembler Queue(string blueprint, double amount)
            {
                queue.Add(new QueueEntry(blueprint, amount));
                return this;
            }

            public void QueueInto(List<QueueEntry> into)
            {
                into.Clear();
                into.AddRange(queue);
            }

            public void OutputInto(Dictionary<string, double> into) => into.Clear();

            public bool CanUse(string blueprint) => true;
            public void Append(string blueprint, double amount) => queue.Add(new QueueEntry(blueprint, amount));
            public double Trim(string blueprint, double amount) => 0;
            public void SetDisassembling(bool on) => IsDisassembling = on;
            public void ClearQueue() { }
        }

        sealed class Rig
        {
            public readonly ProductionLedger Ledger = new ProductionLedger();
            public readonly ProductionFacts Facts = new ProductionFacts();
            public readonly Dictionary<string, ReservePool> Pools = new Dictionary<string, ReservePool>();
            public readonly List<ISortSource> Sources = new List<ISortSource>();
            public readonly List<LedgerKind> Kinds = new List<LedgerKind>();
            public readonly List<ReservePool> CountPools = new List<ReservePool>();
            public readonly List<IAssemblerState> Assemblers = new List<IAssemblerState>();

            public Rig Source(Stock stock, LedgerKind kind)
            {
                Sources.Add(stock);
                Kinds.Add(kind);
                CountPools.Add(null);
                return this;
            }

            public Rig Reserve(Stock stock, string pool)
            {
                Sources.Add(stock);
                Kinds.Add(LedgerKind.Plain);
                CountPools.Add(Pool(pool));
                return this;
            }

            public ReservePool Pool(string name)
            {
                ReservePool p;
                if (!Pools.TryGetValue(name, out p)) { p = new ReservePool(name); Pools[name] = p; }
                return p;
            }

            public Rig Quota(string pool, string key, string quota)
            {
                var p = Pool(pool);
                IngameScript.Quota q;
                Assert.True(IngameScript.Quota.TryParse(quota, out q));
                p.SetQuota(key, q);
                return this;
            }

            public LedgerJob Job(int budget)
            {
                var job = new LedgerJob(Ledger, Facts, Pools, budget);
                job.Rearm(Sources, Kinds, CountPools, Assemblers);
                return job;
            }

            public LedgerJob Run(int budget)
            {
                var job = Job(budget);
                var guard = 0;
                while (job.Step() != StepResult.Done) Assert.True(++guard < 1000);
                return job;
            }
        }

        [Fact]
        public void ReserveStockCountsIntoItsPoolOnlyWhenTheWalkFinishes()
        {
            var rig = new Rig()
                .Reserve(new Stock().Add(Plate, 20).Add(Iron, 5), "P")
                .Source(new Stock().Add(Plate, 300), LedgerKind.Plain)
                .Reserve(new Stock().Add(Plate, 10), "P")
                .Quota("P", Plate, "100");
            var pool = rig.Pools["P"];
            pool.SetCount(Plate, 77);
            var job = rig.Job(1);
            job.Step();
            Assert.Equal(77, pool.Count(Plate));           // mid-walk: the previous cycle's count, whole
            var guard = 0;
            while (job.Step() != StepResult.Done) Assert.True(++guard < 1000);
            Assert.Equal(30, pool.Count(Plate));           // reserves only, and only the quota item
            Assert.Equal(0, pool.Count(Iron));
        }

        [Fact]
        public void UnconfiguredReserveCountsEverythingItHolds()
        {
            var rig = new Rig().Reserve(new Stock().Add(Plate, 20).Add(Iron, 5), "Q");
            rig.Run(100);
            Assert.Equal(20, rig.Pools["Q"].Count(Plate));
            Assert.Equal(5, rig.Pools["Q"].Count(Iron));
        }

        [Fact]
        public void StockTalliesAcrossSourcesIntoDeficits()
        {
            var rig = new Rig()
                .Source(new Stock().Add(Plate, 20), LedgerKind.Plain)
                .Source(new Stock().Add(Plate, 10).Add(Iron, 5), LedgerKind.Plain)
                .Quota("P", Plate, "100A");
            rig.Run(100);
            Assert.Equal(70, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void CatchAllStockIsAlsoTalliedAsCaught()
        {
            var rig = new Rig()
                .Source(new Stock().Add(Plate, 20), LedgerKind.Plain)
                .Source(new Stock().Add(Plate, 5).Add(Iron, 7), LedgerKind.CatchAll)
                .Quota("P", Plate, "100A");
            rig.Run(100);
            Assert.Equal(25, rig.Ledger.Stock(Plate));
            Assert.Equal(5, rig.Ledger.Caught(Plate));
            Assert.Equal(7, rig.Ledger.Caught(Iron));
            Assert.Equal(75, rig.Ledger.AssemblyDeficit(Plate)); // still plain stock, not protected
        }

        [Fact]
        public void AssemblerSnapshotCarriesQueueAndSpeed()
        {
            var rig = new Rig();
            rig.Assemblers.Add(new Assembler { Id = 1, Speed = 5 }.Queue("SteelPlate", 12));
            var job = rig.Run(100);
            var snapshot = Assert.Single(job.Snapshots);
            Assert.Equal(12, snapshot.QueueAmount);
            Assert.Equal(5, snapshot.Speed);
        }

        [Fact]
        public void BudgetBoundsAStepAndTheWalkResumes()
        {
            var stock = new Stock().Add(Plate, 10).Add(Plate, 20).Add(Plate, 30);
            var rig = new Rig().Source(stock, LedgerKind.Plain).Quota("P", Plate, "100A");
            var job = rig.Job(1);
            Assert.Equal(StepResult.Scanned, job.Step());
            Assert.False(job.IsDone);
            var guard = 0;
            while (job.Step() != StepResult.Done) Assert.True(++guard < 100);
            Assert.True(job.IsDone);
            Assert.Equal(40, rig.Ledger.AssemblyDeficit(Plate));    // 100 - 60, no double count across resumes
            Assert.True(stock.Refreshes > 1);                        // re-snapshotted on every resumed step
        }

        [Fact]
        public void RefineryInputCountsExpectedOutputAsIncoming()
        {
            var rig = new Rig()
                .Source(new Stock().Add(IronOre, 100), LedgerKind.Refining)
                .Quota("P", Iron, "100MA");
            rig.Run(100);
            Assert.Equal(30, rig.Ledger.RefineDeficit(Iron));       // 100 - 100 ore x 0.7
        }

        [Fact]
        public void MultiOutputOreFeedsEveryDeficit()
        {
            var rig = new Rig()
                .Source(new Stock().Add(StoneOre, 1000), LedgerKind.Refining)
                .Quota("P", Iron, "100MA")
                .Quota("P", "Ingot/Silicon", "10MA");
            rig.Run(100);
            Assert.Equal(70, rig.Ledger.RefineDeficit(Iron));                // 100 - 1000 x 0.03
            Assert.Equal(6, rig.Ledger.RefineDeficit("Ingot/Silicon"));      // 10 - 1000 x 0.004
        }

        [Fact]
        public void UnknownOreContributesNoExpectedOutput()
        {
            var rig = new Rig()
                .Source(new Stock().Add("Ore/Modium", 500), LedgerKind.Refining)
                .Quota("P", Iron, "100MA");
            rig.Run(100);
            Assert.Equal(100, rig.Ledger.RefineDeficit(Iron));
        }

        [Fact]
        public void FedInputContentsAreProtectedFromDisassembly()
        {
            var rig = new Rig()
                .Source(new Stock().Add(Plate, 400), LedgerKind.Plain)
                .Source(new Stock().Add(Plate, 200), LedgerKind.Protected)
                .Quota("P", Plate, "100A");
            rig.Run(100);
            Assert.Equal(300, rig.Ledger.DisassemblySurplus(Plate)); // 600 stock - 100 retained - 200 protected
        }

        [Fact]
        public void AssemblyQueueCountsAsIncoming()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Source(new Stock().Add(Plate, 30), LedgerKind.Plain);
            rig.Assemblers.Add(new Assembler().Queue("SteelPlate", 50));      // convention reverse map
            rig.Run(100);
            Assert.Equal(20, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void SwitchedOffAssemblerQueueIsNotIncoming()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Source(new Stock().Add(Plate, 30), LedgerKind.Plain);
            rig.Assemblers.Add(new Assembler { IsOperational = false }.Queue("SteelPlate", 50));
            rig.Run(100);
            Assert.Equal(70, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void SwitchedOffDisassemblyQueueStillCountsAsOutgoing()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Source(new Stock().Add(Plate, 500), LedgerKind.Plain);
            rig.Assemblers.Add(new Assembler { IsDisassembling = true, IsOperational = false }.Queue("SteelPlate", 150));
            rig.Run(100);
            Assert.Equal(250, rig.Ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void DisassemblyQueueCountsAsOutgoing()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Source(new Stock().Add(Plate, 500), LedgerKind.Plain);
            rig.Assemblers.Add(new Assembler { IsDisassembling = true }.Queue("SteelPlate", 150));
            rig.Run(100);
            Assert.Equal(250, rig.Ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void ExceptionBlueprintQueueAttributesThroughTheBakedMap()
        {
            var rig = new Rig().Quota("P", "AmmoMagazine/Missile200mm", "10A");
            rig.Assemblers.Add(new Assembler().Queue("Position0100_Missile200mm", 4));
            rig.Run(100);
            Assert.Equal(6, rig.Ledger.AssemblyDeficit("AmmoMagazine/Missile200mm"));
        }

        [Fact]
        public void UnknownBlueprintLandsOnAPhantomKeyAndDisturbsNothing()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Source(new Stock().Add(Plate, 30), LedgerKind.Plain);
            rig.Assemblers.Add(new Assembler().Queue("SomeModdedThing_Mk2", 999));
            rig.Run(100);
            Assert.Equal(70, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void EveryPoolsQuotasApply()
        {
            var rig = new Rig()
                .Source(new Stock().Add(Plate, 60), LedgerKind.Plain)
                .Quota("A", Plate, "100MA")
                .Quota("B", Plate, "150MA");
            rig.Run(100);
            Assert.Equal(190, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void AssemblersDrawTheBudgetOnePerUnit()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            rig.Assemblers.Add(new Assembler().Queue("SteelPlate", 10));
            rig.Assemblers.Add(new Assembler().Queue("SteelPlate", 20));
            var job = rig.Job(1);
            Assert.Equal(StepResult.Scanned, job.Step());            // first assembler only
            Assert.Equal(StepResult.Done, job.Step());               // second assembler + quotas + finish
            Assert.Equal(70, rig.Ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void RearmStartsAFreshPlan()
        {
            var stock = new Stock().Add(Plate, 30);
            var rig = new Rig().Source(stock, LedgerKind.Plain).Quota("P", Plate, "100A");
            rig.Run(100);
            Assert.Equal(70, rig.Ledger.AssemblyDeficit(Plate));

            stock.Add(Plate, 60);
            rig.Run(100);
            Assert.Equal(10, rig.Ledger.AssemblyDeficit(Plate));     // re-counted, not accumulated
        }

        [Fact]
        public void FreshJobIsNotDoneUntilStepped()
        {
            var rig = new Rig().Quota("P", Plate, "100A");
            var job = rig.Job(100);
            Assert.False(job.IsDone);
            Assert.Equal(StepResult.Done, job.Step());
            Assert.True(job.IsDone);
            Assert.Equal(StepResult.Done, job.Step());               // idempotent once done
        }
    }
}
