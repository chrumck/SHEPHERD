using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // Which blocks one SHEPHERD manages: its own construct (home, index 0) plus every construct docked to it through a
    // chain of connected connectors, minus whatever lies behind a [SHEPHERD-Split] connector, minus blocks another
    // player owns. Rebuilt from the live connectors each scan, so a dock, an undock or a retag shows on the next one.
    // Constructs, not grids: rotor and piston subgrids belong to the construct they hang off.
    public sealed partial class ManagedScope
    {
        readonly List<IMyCubeGrid> constructs = new List<IMyCubeGrid>(); // one grid per construct, home first
        readonly List<IMyCubeGrid> edgeA = new List<IMyCubeGrid>();
        readonly List<IMyCubeGrid> edgeB = new List<IMyCubeGrid>();     // parallel to edgeA
        long owner;
        bool factionShare;

        public bool DockedProduction { get; private set; }
        public bool DockedFuel { get; private set; }
        public int Count => constructs.Count;

        public void Build(List<IMyShipConnector> connectors, IMyCubeGrid home, long ownerId, bool shareWithFaction,
            bool dockedProduction, bool dockedFuel)
        {
            constructs.Clear();
            edgeA.Clear();
            edgeB.Clear();
            owner = ownerId;
            factionShare = shareWithFaction;
            DockedProduction = dockedProduction;
            DockedFuel = dockedFuel;
            constructs.Add(home);
            foreach (var c in connectors)
            {
                var other = c.OtherConnector;
                if (c.Status != MyShipConnectorStatus.Connected || other == null
                    || Tags.Parse(c.CustomName).IsSplit || Tags.Parse(other.CustomName).IsSplit) continue;
                edgeA.Add(c.CubeGrid);
                edgeB.Add(other.CubeGrid);
            }
            // Each pass admits the far end of every edge with exactly one end in scope; a pass admitting nothing ends
            // the walk, so there are at most as many passes as edges.
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < edgeA.Count; i++)
                {
                    var inA = IndexOf(edgeA[i]) >= 0;
                    if (inA == IndexOf(edgeB[i]) >= 0) continue;
                    constructs.Add(inA ? edgeB[i] : edgeA[i]);
                    grew = true;
                }
            }
        }

        // 0 = home, above 0 = docked, -1 = outside the scope (or no scope built yet).
        public int IndexOf(IMyCubeGrid grid)
        {
            for (var i = 0; i < constructs.Count; i++)
                if (grid.IsSameConstructAs(constructs[i])) return i;
            return -1;
        }

        // An unowned block is open to every player anyway, so it is managed wherever it sits.
        public bool Owns(IMyTerminalBlock block) =>
            block.OwnerId == 0 || block.OwnerId == owner
            || factionShare && block.GetUserRelationToOwner(owner) == MyRelationsBetweenPlayerAndBlock.FactionShare;
    }
}
