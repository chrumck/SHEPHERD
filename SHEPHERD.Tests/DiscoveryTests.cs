using System.Collections.Generic;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    public class DiscoveryTests
    {
        [Fact]
        public void RequestsAreDeduplicatedAndChosenOrdinally()
        {
            var coordinator = new DiscoveryCoordinator();
            coordinator.Reconcile(new List<string> { "Component/Z", "Component/A", "Component/A" },
                new List<string> { "Ore/X" });
            Assert.True(coordinator.HasActive);
            Assert.Equal(KnowledgeKind.Blueprint, coordinator.Active.Kind);
            Assert.Equal("Component/A", coordinator.Active.Key);
        }

        [Fact]
        public void ActiveRequestPersistsWhileReassertedAndCancelsWhenGone()
        {
            var coordinator = new DiscoveryCoordinator();
            coordinator.Reconcile(new List<string> { "Component/A" }, new List<string>());
            coordinator.Reconcile(new List<string> { "Component/A", "Component/B" }, new List<string>());
            Assert.Equal("Component/A", coordinator.Active.Key);
            coordinator.Reconcile(new List<string>(), new List<string> { "Ore/X" });
            Assert.Equal(KnowledgeKind.Yield, coordinator.Active.Kind);
            Assert.Equal("Ore/X", coordinator.Active.Key);
        }

        [Fact]
        public void ParkedBlueprintAllowsYieldAndRemainsVisible()
        {
            var coordinator = new DiscoveryCoordinator();
            var blueprints = new List<string> { "Component/A", "Component/B" };
            var yields = new List<string> { "Ore/X" };
            coordinator.Reconcile(blueprints, yields);
            coordinator.ParkBlueprint();
            coordinator.Reconcile(blueprints, yields);
            Assert.True(coordinator.HasParkedBlueprint);
            Assert.Equal("Component/A", coordinator.ParkedBlueprint.Key);
            Assert.Equal(KnowledgeKind.Yield, coordinator.Active.Kind);
        }

        [Fact]
        public void ResolveAndResetRemoveState()
        {
            var coordinator = new DiscoveryCoordinator();
            coordinator.Reconcile(new List<string> { "Component/A" }, new List<string>());
            coordinator.Resolve(KnowledgeKind.Blueprint, "Component/A");
            Assert.False(coordinator.HasActive);
            coordinator.Reconcile(new List<string> { "Component/A" }, new List<string>());
            coordinator.ParkBlueprint();
            coordinator.Reset();
            Assert.False(coordinator.HasActive);
            Assert.False(coordinator.HasParkedBlueprint);
        }
    }
}
