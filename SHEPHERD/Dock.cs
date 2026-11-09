using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // Rebuilt from the live connectors each scan, so a dock, an undock or a retag shows on the next one. Constructs,
    // not grids: rotor and piston subgrids belong to the construct they hang off.
    public partial class ManagedScope
    {
        readonly List<IMyCubeGrid> constructs = new List<IMyCubeGrid>(); // one grid per construct, home first
        readonly List<IMyCubeGrid> edgeA = new List<IMyCubeGrid>();
        readonly List<IMyCubeGrid> edgeB = new List<IMyCubeGrid>();     // parallel to edgeA
        long owner;
        bool factionShare;

        public bool DockedProduction;
        public bool DockedFuel;
        public bool AutoUnload;
        // The ships unloadShip marked: each leaves once a sort lap has walked its cargo, or when it undocks.
        public readonly List<IMyCubeGrid> Unloading = new List<IMyCubeGrid>();
        public int Count => constructs.Count;

        public void Build(List<IMyShipConnector> connectors, IMyCubeGrid home, long ownerId, bool shareWithFaction,
            bool dockedProduction, bool dockedFuel, bool autoUnload)
        {
            constructs.Empty();
            edgeA.Empty();
            edgeB.Empty();
            owner = ownerId;
            factionShare = shareWithFaction;
            DockedProduction = dockedProduction;
            DockedFuel = dockedFuel;
            AutoUnload = autoUnload;
            constructs.Push(home);
            foreach (var c in connectors)
            {
                var other = c.OtherConnector;
                if (c.Status != MyShipConnectorStatus.Connected || other == null
                    || Tags.Parse(c.CustomName).IsSplit || Tags.Parse(other.CustomName).IsSplit) continue;
                edgeA.Push(c.CubeGrid);
                edgeB.Push(other.CubeGrid);
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
                    constructs.Push(inA ? edgeB[i] : edgeA[i]);
                    grew = true;
                }
            }
            for (var i = Unloading.Count - 1; i >= 0; i--)
                if (IndexOf(Unloading[i]) < 1) Unloading.RemoveAt(i);
        }

        // Marks every ship docked now, an unloaded one again too. A station's cargo unloads anyway.
        public int MarkShips()
        {
            Unloading.Empty();
            for (var i = 1; i < constructs.Count; i++)
                if (!constructs[i].IsStatic) Unloading.Push(constructs[i]);
            return Unloading.Count;
        }

        // For a docked construct: a ship's cargo is the pilot's until unloadShip marks it, unless autoUnloadShip.
        public bool KeepsCargo(int at)
        {
            if (AutoUnload || constructs[at].IsStatic) return false;
            foreach (var g in Unloading)
                if (g.IsSameConstructAs(constructs[at])) return false;
            return true;
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
