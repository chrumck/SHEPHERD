using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class LedgerTests
    {
        const string Plate = "Component/SteelPlate";
        const string Iron = "Ingot/Iron";
        const string Stone = "Ore/Stone";

        static ReservePool Pool(string name, string key, string quota)
        {
            var pool = new ReservePool(name);
            Quota q;
            Assert.True(Quota.TryParse(quota, out q));
            pool.SetQuota(key, q);
            return pool;
        }

        static ProductionLedger Build(string key, string quota, double stock)
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("P", key, quota));
            ledger.AddStock(key, stock);
            return ledger;
        }

        [Fact]
        public void MinAutoAssemblesBelowNeverDisassemblesAbove()
        {
            var ledger = Build(Plate, "100MA", 30);
            Assert.Equal(70, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));

            ledger = Build(Plate, "100MA", 500);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void LimitAutoDisassemblesAboveNeverAssemblesBelow()
        {
            var ledger = Build(Plate, "100LA", 30);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));

            ledger = Build(Plate, "100LA", 500);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(400, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void ExactAutoDoesBoth()
        {
            var ledger = Build(Plate, "100A", 30);
            Assert.Equal(70, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));

            ledger = Build(Plate, "100A", 500);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(400, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void NonAutomaticQuotaProducesNoAction()
        {
            var ledger = Build(Plate, "100", 30);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void IngotDeficitIsRefineNeverAssembly()
        {
            var ledger = Build(Iron, "100MA", 30);
            Assert.Equal(0, ledger.AssemblyDeficit(Iron));
            Assert.Equal(70, ledger.RefineDeficit(Iron));
        }

        [Fact]
        public void RefiningUsesAggregateFloorAndAutomaticCap()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Iron, "100MA"));
            ledger.AddQuotas(Pool("B", Iron, "200M"));
            ledger.AddStock(Iron, 50);
            Assert.Equal(100, ledger.RefineDeficit(Iron));
            ledger.AddIncoming(Iron, 100);
            Assert.Equal(0, ledger.RefineDeficit(Iron));
        }

        [Fact]
        public void IngotsNeverDisassemble()
        {
            var ledger = Build(Iron, "100A", 500);
            Assert.Equal(0, ledger.DisassemblySurplus(Iron));
            ledger = Build(Iron, "100LA", 500);
            Assert.Equal(0, ledger.DisassemblySurplus(Iron));    // LA behaves as L
            Assert.Equal(0, ledger.RefineDeficit(Iron));
        }

        [Fact]
        public void OreNeverGetsAction()
        {
            var ledger = Build(Stone, "100A", 30);
            Assert.Equal(0, ledger.AssemblyDeficit(Stone));
            Assert.Equal(0, ledger.RefineDeficit(Stone));
            ledger = Build(Stone, "100A", 500);
            Assert.Equal(0, ledger.DisassemblySurplus(Stone));
        }

        [Fact]
        public void RefineDeficitZeroForNonIngot()
        {
            var ledger = Build(Plate, "100A", 30);
            Assert.Equal(0, ledger.RefineDeficit(Plate));
        }

        [Fact]
        public void MultiplePoolsSumTargetsAgainstSingleStock()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Plate, "100MA"));
            ledger.AddQuotas(Pool("B", Plate, "150MA"));
            ledger.AddStock(Plate, 60);
            Assert.Equal(190, ledger.AssemblyDeficit(Plate));    // 250 combined target, stock counted once
        }

        [Fact]
        public void AutomaticProductionIsCappedAgainstEveryProductionTarget()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Plate, "100A"));
            ledger.AddQuotas(Pool("B", Plate, "200"));
            ledger.AddStock(Plate, 50);
            Assert.Equal(100, ledger.AssemblyDeficit(Plate));
            ledger.AddIncoming(Plate, 100);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));

            ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Plate, "100A"));
            ledger.AddQuotas(Pool("B", Plate, "200"));
            ledger.AddStock(Plate, 250);
            Assert.Equal(50, ledger.AssemblyDeficit(Plate));

            ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Plate, "100A"));
            ledger.AddQuotas(Pool("B", Plate, "200"));
            ledger.AddStock(Plate, 500);
            Assert.Equal(200, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void QueuedWorkCountsBeforeAppendingMore()
        {
            var ledger = Build(Plate, "100A", 30);
            ledger.AddIncoming(Plate, 50);                       // live assembly queue
            Assert.Equal(20, ledger.AssemblyDeficit(Plate));
            ledger.AddIncoming(Plate, 20);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void IncomingWorkDoesNotCreatePhysicalDisassemblySurplus()
        {
            var ledger = Build(Plate, "100A", 100);
            ledger.AddIncoming(Plate, 100);
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void InProgressRefiningCountsAsIncoming()
        {
            var ledger = Build(Iron, "100MA", 40);
            ledger.AddIncoming(Iron, 35);                        // expected from ore already in refineries
            Assert.Equal(25, ledger.RefineDeficit(Iron));
        }

        [Fact]
        public void MachineHeldStockNeverSatisfiesAQuota()
        {
            var ledger = Build(Iron, "100MA", 40);               // 30 of the 40 sit inside a fed reactor
            ledger.AddProtected(Iron, 30);
            Assert.Equal(90, ledger.RefineDeficit(Iron));

            ledger = Build(Plate, "100A", 100);
            ledger.AddProtected(Plate, 25);
            Assert.Equal(25, ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void DisassemblyQueueCountsAsOutgoing()
        {
            var ledger = Build(Plate, "100A", 500);
            ledger.AddOutgoing(Plate, 150);                      // already queued for disassembly
            Assert.Equal(250, ledger.DisassemblySurplus(Plate));
            ledger.AddOutgoing(Plate, 300);
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));   // net at 50 -> nothing more to disassemble
            // Never a pre-emptive counter-assembly against a running disassembly queue: the deficit tracks
            // PHYSICAL stock only, so compensation starts when stock actually falls below the floor.
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            ledger.AddStock(Plate, -430);                        // the queue really consumed stock to 70
            Assert.Equal(30, ledger.AssemblyDeficit(Plate));
        }

        [Fact]
        public void AllQuotaSuppressesDisassembly()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("A", Plate, "100A"));
            ledger.AddQuotas(Pool("B", Plate, "All"));
            ledger.AddStock(Plate, 5000);
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void ProtectedDemandShieldsSurplus()
        {
            var ledger = Build(Plate, "100A", 500);
            ledger.AddProtected(Plate, 350);                     // machine inputs / queued requirements
            Assert.Equal(50, ledger.DisassemblySurplus(Plate));
            ledger.AddProtected(Plate, 100);
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void ExactBoundaryIsADeadband()
        {
            var ledger = Build(Plate, "100A", 100);
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void UnknownKeyIsInert()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));
            Assert.Equal(0, ledger.RefineDeficit(Iron));
            Assert.Equal(0, ledger.DisassemblySurplus(Plate));
        }

        [Fact]
        public void BeginResetsForANewPlan()
        {
            var ledger = Build(Plate, "100A", 30);
            Assert.Equal(70, ledger.AssemblyDeficit(Plate));
            ledger.Begin();
            Assert.Equal(0, ledger.AssemblyDeficit(Plate));      // stale entries gone
            ledger.AddQuotas(Pool("P", Plate, "100A"));
            ledger.AddStock(Plate, 90);
            Assert.Equal(10, ledger.AssemblyDeficit(Plate));     // pooled entries fully reset
        }

        [Fact]
        public void CollectKeysListsEveryEntry()
        {
            var ledger = new ProductionLedger();
            ledger.Begin();
            ledger.AddQuotas(Pool("P", Plate, "100A"));
            ledger.AddStock(Iron, 5);
            var keys = new List<string>();
            ledger.CollectKeys(keys);
            Assert.Equal(2, keys.Count);
            Assert.Contains(Plate, keys);
            Assert.Contains(Iron, keys);
        }
    }
}
