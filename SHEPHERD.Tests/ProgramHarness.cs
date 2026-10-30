using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using IngameScript;
using Moq;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace SHEPHERD.Tests
{
    // Runs the real Program offline, created the way the game creates a script: an uninitialized instance, the
    // IMyGridProgram members injected, then the constructor. Ticks advance game time by the wake rate the script last
    // asked for, so its timers (refractory, holds, heartbeats) run as in game, just without the wall-clock waits.
    public sealed class ProgramHarness
    {
        public readonly Program Program;
        readonly Mock<IMyProgrammableBlock> me = new Mock<IMyProgrammableBlock>();
        readonly Mock<IMyGridProgramRuntimeInfo> runtime = new Mock<IMyGridProgramRuntimeInfo>();
        readonly Mock<IMyGridTerminalSystem> terminal = new Mock<IMyGridTerminalSystem>();
        readonly Mock<IMyIntergridCommunicationSystem> igc = new Mock<IMyIntergridCommunicationSystem>();
        readonly Mock<IMyBroadcastListener> listener = new Mock<IMyBroadcastListener>();
        readonly Queue<MyIGCMessage> inbox = new Queue<MyIGCMessage>();
        readonly List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
        readonly List<IMyShipConnector> connectors = new List<IMyShipConnector>();
        readonly Dictionary<long, IMyTerminalBlock> byId = new Dictionary<long, IMyTerminalBlock>();
        readonly List<Action<double>> beforeTick = new List<Action<double>>();
        long nextId = 100;
        double gameSeconds;
        double pausedSeconds;

        public const long Owner = 7;
        public readonly IMyCubeGrid Home;

        // Every fetch throws while set, as a stand-in for any crash inside a cycle step.
        public bool FailFetch;
        // What the script reads as its previous run's time.
        public double RunMs = 0.05;
        // What the script reads as its instruction count so far in a run: past a quarter of the cap, so a start packs
        // no extra steps into a run unless a test lowers it.
        public int Instructions = 20000;
        public string Echo { get; private set; }
        // SE appends every Echo of a run to its detail pane, so more than one shows the banner twice.
        public int MostEchoesInARun { get; private set; }
        int echoesThisRun;
        // The script's latest broadcast on the election tag: rank, PB entity id, label.
        public MyTuple<long, long, string> LastHeartbeat { get; private set; }

        public ProgramHarness(IMyCubeGrid home, string pbName, string customData)
        {
            Home = home;
            me.SetupGet(b => b.CustomName).Returns(pbName);
            me.SetupProperty(b => b.CustomData, customData);
            me.SetupGet(b => b.CubeGrid).Returns(home);
            me.SetupGet(b => b.OwnerId).Returns(Owner);
            me.SetupGet(b => b.EntityId).Returns(1);
            byId[1] = me.Object;
            blocks.Add(me.Object);

            runtime.SetupProperty(r => r.UpdateFrequency, UpdateFrequency.None);
            runtime.SetupGet(r => r.LastRunTimeMs).Returns(() => RunMs);
            runtime.SetupGet(r => r.MaxInstructionCount).Returns(50000);
            runtime.SetupGet(r => r.CurrentInstructionCount).Returns(() => Instructions);
            runtime.SetupGet(r => r.TimeSinceLastRun).Returns(() => TimeSpan.FromSeconds(WakeSeconds() + pausedSeconds));

            terminal.Setup(t => t.GetBlocksOfType<IMyTerminalBlock>(It.IsAny<List<IMyTerminalBlock>>(), It.IsAny<Func<IMyTerminalBlock, bool>>()))
                .Callback<List<IMyTerminalBlock>, Func<IMyTerminalBlock, bool>>((list, collect) =>
                {
                    if (FailFetch) throw new InvalidOperationException("fetch failed");
                    list.Clear();
                    foreach (var b in blocks) if (collect == null || collect(b)) list.Add(b);
                });
            terminal.Setup(t => t.GetBlocksOfType(It.IsAny<List<IMyShipConnector>>(), It.IsAny<Func<IMyShipConnector, bool>>()))
                .Callback<List<IMyShipConnector>, Func<IMyShipConnector, bool>>((list, collect) =>
                {
                    list.Clear();
                    foreach (var c in connectors) if (collect == null || collect(c)) list.Add(c);
                });
            terminal.Setup(t => t.GetBlockWithId(It.IsAny<long>()))
                .Returns<long>(id => { IMyTerminalBlock b; return byId.TryGetValue(id, out b) ? b : null; });

            listener.SetupGet(l => l.HasPendingMessage).Returns(() => inbox.Count > 0);
            listener.Setup(l => l.AcceptMessage()).Returns(() => inbox.Dequeue());
            igc.SetupGet(i => i.Me).Returns(1001);
            igc.Setup(i => i.RegisterBroadcastListener(It.IsAny<string>())).Returns(listener.Object);
            igc.Setup(i => i.SendBroadcastMessage(It.IsAny<string>(), It.IsAny<MyTuple<long, long, string>>(), It.IsAny<TransmissionDistance>()))
                .Callback<string, MyTuple<long, long, string>, TransmissionDistance>((tag, data, range) => LastHeartbeat = data);

            Program = (Program)FormatterServices.GetUninitializedObject(typeof(Program));
            var gp = (Sandbox.ModAPI.IMyGridProgram)Program;
            gp.Runtime = runtime.Object;
            gp.Me = me.Object;
            gp.GridTerminalSystem = terminal.Object;
            gp.IGC_ContextGetter = () => igc.Object;
            gp.Echo = text => { Echo = text; if (++echoesThisRun > MostEchoesInARun) MostEchoesInARun = echoesThisRun; };
            typeof(Program).GetConstructor(Type.EmptyTypes).Invoke(Program, null);
        }

        public string CustomData => me.Object.CustomData;

        public UpdateFrequency Frequency => runtime.Object.UpdateFrequency;

        // Transfers out of AddStock containers and AddTank refills attempted, moved or not.
        public int EngineWrites;

        double WakeSeconds()
        {
            var f = runtime.Object.UpdateFrequency;
            return (f & UpdateFrequency.Update1) != 0 ? 1 / 60.0 : (f & UpdateFrequency.Update10) != 0 ? 10 / 60.0 : 100 / 60.0;
        }

        public static IMyCubeGrid Grid(string name, bool isStatic, bool large)
        {
            var g = new Mock<IMyCubeGrid>();
            g.SetupGet(x => x.CustomName).Returns(name);
            g.SetupGet(x => x.IsStatic).Returns(isStatic);
            g.SetupGet(x => x.GridSizeEnum).Returns(large ? MyCubeSize.Large : MyCubeSize.Small);
            g.Setup(x => x.IsSameConstructAs(It.IsAny<IMyCubeGrid>())).Returns<IMyCubeGrid>(other => other == g.Object);
            return g.Object;
        }

        // A text panel; every text written to it is appended to the returned list.
        public List<string> AddPanel(string name, IMyCubeGrid grid = null)
        {
            var frames = new List<string>();
            var panel = new Mock<IMyTextPanel>();
            Place(panel, name, grid);
            panel.SetupProperty(p => p.ContentType, ContentType.NONE);
            panel.SetupProperty(p => p.Font, "Debug");
            panel.Setup(p => p.WriteText(It.IsAny<string>(), It.IsAny<bool>()))
                .Callback<string, bool>((text, append) => frames.Add(text)).Returns(true);
            Add(panel.Object);
            return frames;
        }

        // A light, white as built; every colour written to it is appended to the returned list.
        public List<Color> AddLight(string name, IMyCubeGrid grid = null)
        {
            var colours = new List<Color>();
            var light = new Mock<IMyLightingBlock>();
            Place(light, name, grid);
            light.SetupGet(l => l.Color).Returns(() => colours.Count == 0 ? Color.White : colours[colours.Count - 1]);
            light.SetupSet(l => l.Color = It.IsAny<Color>()).Callback<Color>(colours.Add);
            Add(light.Object);
            return colours;
        }

        // A cargo container holding nothing, its fill reported as used / max volume.
        public void AddCargo(string name, double used, double max)
        {
            var inventory = new Mock<IMyInventory>();
            inventory.SetupGet(i => i.CurrentVolume).Returns((MyFixedPoint)used);
            inventory.SetupGet(i => i.MaxVolume).Returns((MyFixedPoint)max);
            var cargo = new Mock<IMyCargoContainer>();
            Place(cargo, name, null);
            cargo.SetupGet(c => c.HasInventory).Returns(true);
            cargo.SetupGet(c => c.InventoryCount).Returns(1);
            cargo.Setup(c => c.GetInventory(It.IsAny<int>())).Returns(inventory.Object);
            cargo.Setup(c => c.GetInventory()).Returns(inventory.Object);
            Add(cargo.Object);
        }

        // An untagged cargo container holding `items`, which nothing ever moves out.
        public void AddStock(string name, params MyInventoryItem[] items)
        {
            var inventory = new Mock<IMyInventory>();
            inventory.SetupGet(i => i.MaxVolume).Returns((MyFixedPoint)10);
            inventory.Setup(i => i.GetItems(It.IsAny<List<MyInventoryItem>>(), It.IsAny<Func<MyInventoryItem, bool>>()))
                .Callback<List<MyInventoryItem>, Func<MyInventoryItem, bool>>((list, keep) => list.AddRange(items));
            inventory.Setup(i => i.TransferItemTo(It.IsAny<IMyInventory>(), It.IsAny<MyInventoryItem>(), It.IsAny<MyFixedPoint?>()))
                .Callback(() => EngineWrites++).Returns(false);
            inventory.Setup(i => i.TransferItemTo(It.IsAny<IMyInventory>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<MyFixedPoint?>()))
                .Callback(() => EngineWrites++).Returns(false);
            var cargo = new Mock<IMyCargoContainer>();
            Place(cargo, name, null);
            cargo.SetupGet(c => c.HasInventory).Returns(true);
            cargo.SetupGet(c => c.InventoryCount).Returns(1);
            cargo.Setup(c => c.GetInventory(It.IsAny<int>())).Returns(inventory.Object);
            cargo.Setup(c => c.GetInventory()).Returns(inventory.Object);
            Add(cargo.Object);
        }

        // A gas tank: a bottle refill station, empty unless it holds gas and takes bottles.
        public void AddTank(string name, bool gas = false)
        {
            var inventory = new Mock<IMyInventory>();
            inventory.Setup(i => i.CanItemsBeAdded(It.IsAny<MyFixedPoint>(), It.IsAny<MyItemType>())).Returns(gas);
            var tank = new Mock<IMyGasTank>();
            Place(tank, name, null);
            tank.SetupGet(t => t.FilledRatio).Returns(gas ? 1.0 : 0.0);
            tank.SetupGet(t => t.HasInventory).Returns(true);
            tank.SetupGet(t => t.InventoryCount).Returns(1);
            tank.Setup(t => t.RefillBottles()).Callback(() => EngineWrites++);
            tank.Setup(t => t.GetInventory(It.IsAny<int>())).Returns(inventory.Object);
            tank.Setup(t => t.GetInventory()).Returns(inventory.Object);
            Add(tank.Object);
        }

        // A connected connector pair between two grids, both ends listed as the terminal system lists them.
        public void Dock(IMyCubeGrid a, IMyCubeGrid b)
        {
            var ca = new Mock<IMyShipConnector>();
            var cb = new Mock<IMyShipConnector>();
            Place(ca, "Connector", a);
            Place(cb, "Connector", b);
            ca.SetupGet(c => c.Status).Returns(MyShipConnectorStatus.Connected);
            cb.SetupGet(c => c.Status).Returns(MyShipConnectorStatus.Connected);
            ca.SetupGet(c => c.OtherConnector).Returns(cb.Object);
            cb.SetupGet(c => c.OtherConnector).Returns(ca.Object);
            foreach (var c in new[] { ca.Object, cb.Object }) { connectors.Add(c); Add(c); }
        }

        // Another SHEPHERD PB on `grid`, heard every heartbeat from now on, as its own broadcasts would arrive.
        public void AddPeer(string pbName, IMyCubeGrid grid, long rank, long address)
        {
            var pb = new Mock<IMyProgrammableBlock>();
            Place(pb, pbName, grid);
            Add(pb.Object);
            var label = pbName + " on " + grid.CustomName;
            var nextBeat = 0.0;
            beforeTick.Add(now =>
            {
                if (now < nextBeat) return;
                nextBeat = now + PeerElection.LeadHeartbeatSeconds;
                inbox.Enqueue(new MyIGCMessage(new MyTuple<long, long, string>(rank, pb.Object.EntityId, label), "SHEPHERD", address));
            });
        }

        void Place<T>(Mock<T> block, string name, IMyCubeGrid grid) where T : class, IMyTerminalBlock
        {
            var id = nextId++;
            block.SetupGet(b => b.CustomName).Returns(name);
            block.SetupGet(b => b.CubeGrid).Returns(grid ?? Home);
            block.SetupGet(b => b.OwnerId).Returns(Owner);
            block.SetupGet(b => b.EntityId).Returns(id);
            block.SetupGet(b => b.IsFunctional).Returns(true);
        }

        void Add(IMyTerminalBlock block)
        {
            blocks.Add(block);
            byId[block.EntityId] = block;
        }

        public void Tick(int count = 1)
        {
            for (var i = 0; i < count; i++)
            {
                gameSeconds += WakeSeconds() + pausedSeconds;
                foreach (var a in beforeTick) a(gameSeconds);
                echoesThisRun = 0;
                Program.Main("", UpdateType.Update1);
                pausedSeconds = 0;
            }
        }

        // Game time the world runs before the script's next run, as when the server leaves the PB unrun.
        public void Pause(double seconds) => pausedSeconds += seconds;

        // Ticks until `done` holds, failing after `limit` ticks so a regression can't hang the run.
        public void TickUntil(Func<bool> done, int limit = 5000)
        {
            for (var i = 0; i < limit; i++)
            {
                if (done()) return;
                Tick();
            }
            throw new InvalidOperationException("condition not reached in " + limit + " ticks; echo: " + Echo);
        }

        public void Command(string argument) => Program.Main(argument, UpdateType.Terminal);
    }
}
