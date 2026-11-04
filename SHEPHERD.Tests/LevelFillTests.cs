using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    // Roles.LevelShare: the volume one machine of a group takes from a stack so the group ends at one common fill.
    public class LevelFillTests
    {
        static double Share(double[] fills, double[] targets, double volume, int member) =>
            Roles.LevelShare(new List<double>(fills), new List<double>(targets), volume, fills[member], targets[member]);

        [Fact]
        public void TwoEmptyMachinesEndAtTheSameFill()
        {
            var first = Share(new[] { 0.0, 0.0 }, new[] { 100.0, 100.0 }, 100.0, 0);
            Assert.Equal(50.0, first, 6);
            // The rest of the stack, poured after the first machine took its share, levels the second one with it.
            var second = Share(new[] { 0.0, 0.5 }, new[] { 100.0, 100.0 }, 100.0 - first, 0);
            Assert.Equal(50.0, second, 6);
        }

        [Fact]
        public void SharesFollowEachMachinesTarget()
        {
            Assert.Equal(50.0, Share(new[] { 0.0, 0.0 }, new[] { 100.0, 200.0 }, 150.0, 0), 6);
            Assert.Equal(100.0, Share(new[] { 0.0, 0.0 }, new[] { 100.0, 200.0 }, 150.0, 1), 6);
        }

        [Fact]
        public void TheEmptiestCatchesUpBeforeTheOthersRise()
        {
            Assert.Equal(30.0, Share(new[] { 0.0, 0.6 }, new[] { 100.0, 100.0 }, 30.0, 0), 6);
            Assert.Equal(80.0, Share(new[] { 0.0, 0.6 }, new[] { 100.0, 100.0 }, 100.0, 0), 6);
            Assert.Equal(20.0, Share(new[] { 0.0, 0.6 }, new[] { 100.0, 100.0 }, 100.0, 1), 6);
        }

        [Fact]
        public void PlentyFillsToTheTargetAndNoFurther()
        {
            Assert.Equal(80.0, Share(new[] { 0.2, 0.5 }, new[] { 100.0, 100.0 }, 1000.0, 0), 6);
        }

        [Fact]
        public void ASmallStackIsNotShavedAcrossEveryMachine()
        {
            var fills = new double[10];
            var targets = new double[10];
            for (var i = 0; i < 10; i++) targets[i] = 100.0;
            Assert.Equal(5.0, Share(fills, targets, 5.0, 0), 6);
            // Fifty fills five machines to the minimum share each, not ten machines to half of it.
            Assert.Equal(100.0 * Roles.MinShare, Share(fills, targets, 50.0, 0), 6);
        }

        [Fact]
        public void AMachineAboveTheLevelGetsNothing()
        {
            Assert.Equal(0.0, Share(new[] { 0.0, 0.5 }, new[] { 100.0, 100.0 }, 20.0, 1), 6);
        }

        [Fact]
        public void NothingToPourCostsOneStep()
        {
            // A dry claim by a member that isn't the lowest passes 0: the level stays at the lowest fill.
            Assert.Equal(0.0, Share(new[] { 0.0, 0.0, 0.0 }, new[] { 100.0, 100.0, 100.0 }, 0.0, 2), 6);
            Assert.Equal(0.0, Share(new[] { 0.1, 0.3, 0.5 }, new[] { 100.0, 100.0, 100.0 }, 0.0, 0), 6);
        }

        [Fact]
        public void NoEligibleMachineGetsNothing()
        {
            Assert.Equal(0.0, Roles.LevelShare(new List<double>(), new List<double>(), 50.0, 0.0, 100.0), 6);
        }
    }
}
