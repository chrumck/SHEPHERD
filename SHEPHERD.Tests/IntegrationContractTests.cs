using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IngameScript;
using Xunit;

namespace SHEPHERD.Tests
{
    // Pins what other programs read from SHEPHERD (INTEGRATION.md) to the sample files in Fixtures/: each panel is
    // rendered by the real script, so a change to what it shows fails here and shows up as a diff to a sample. Set
    // SHEPHERD_WRITE_FIXTURES=1 to rewrite the samples from the current output instead.
    public class IntegrationContractTests
    {
        const string BaseData = "[Reserve:Spare]\nComponent/SteelPlate=100MA\n";

        static ProgramHarness Base(out List<string> status, string settings = "")
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", BaseData + settings);
            status = h.AddPanel("Status [SHEPHERD]");
            h.AddCargo("Ore Cargo [SHEPHERD:Ores,Ingots]", 4, 10);
            return h;
        }

        static string Health(List<string> frames) =>
            frames.Count == 0 ? null : frames[frames.Count - 1].Split('\n')[0].Trim();

        static bool Shows(List<string> frames, string health) =>
            Health(frames)?.EndsWith("· " + health, StringComparison.Ordinal) ?? false;

        static bool ShowsLine(List<string> frames, string line) =>
            frames.Count > 0 && frames[frames.Count - 1].Contains(line);

        [Fact]
        public void WorkingPanel()
        {
            List<string> status;
            var h = Base(out status);
            h.TickUntil(() => Shows(status, "working") && ShowsLine(status, "craft Component/SteelPlate"));
            Matches("status-working.txt", status[status.Count - 1]);
        }

        [Fact]
        public void IdlePanel()
        {
            List<string> status;
            var h = Base(out status);
            h.TickUntil(() => Shows(status, "idle"));
            Matches("status-idle.txt", status[status.Count - 1]);
        }

        [Fact]
        public void LearningPanel()
        {
            List<string> status;
            var h = Base(out status);
            h.TickUntil(() => Shows(status, "idle"));
            h.Command("learn");
            h.TickUntil(() => Shows(status, "learning") && ShowsLine(status, "learning - "));
            Matches("status-learning.txt", status[status.Count - 1]);
        }

        [Fact]
        public void HaltedPanel()
        {
            List<string> status;
            var h = Base(out status);
            h.TickUntil(() => Shows(status, "idle"));
            h.FailFetch = true;
            h.TickUntil(() => Shows(status, "HALTED"));
            Matches("status-halted.txt", status[status.Count - 1]);
        }

        // HALTED is not final: the breaker keeps retrying at the slowest wake rate, and the first clean step closes it.
        [Fact]
        public void HaltedRecoversOnceTheFaultClears()
        {
            List<string> status;
            var h = Base(out status);
            h.TickUntil(() => Shows(status, "idle"));
            h.FailFetch = true;
            h.TickUntil(() => Shows(status, "HALTED"));
            h.FailFetch = false;
            h.TickUntil(() => Shows(status, "working") || Shows(status, "idle"));
        }

        // A stand-by PB draws its own panel only when the leader is on another construct: here a shuttle docked at a
        // base whose PB outranks it (a static grid over a small one).
        [Fact]
        public void StandingByPanel()
        {
            var shuttle = ProgramHarness.Grid("Shuttle", false, false);
            var station = ProgramHarness.Grid("Base", true, true);
            var h = new ProgramHarness(shuttle, "Shuttle PB", "");
            var status = h.AddPanel("Shuttle Status [SHEPHERD]");
            h.Dock(shuttle, station);
            h.AddPeer("Base PB", station, PeerElection.Rank(0, true, true), 2002);
            h.TickUntil(() => Shows(status, "standing by"));
            Matches("status-standing-by.txt", status[status.Count - 1]);
        }

        // What SHEPHERD writes into an empty Custom Data: every [Settings] key with its default, in the guide's order.
        [Fact]
        public void DefaultSettings()
        {
            var h = new ProgramHarness(ProgramHarness.Grid("Base", true, true), "Base PB", "");
            h.TickUntil(() => h.CustomData.Contains("[Settings]"));
            Matches("settings-defaults.ini", h.CustomData);
        }

        [Fact]
        public void EverySampleShowsAReleaseVersionAndAClock()
        {
            foreach (var path in Directory.GetFiles(FixtureDir(), "status-*.txt"))
            {
                var lines = Lf(File.ReadAllText(path)).Split('\n');
                Assert.Matches(@"^SHEPHERD \d+\.\d+\.\d+ · ", lines[0]);
                Assert.Matches(@"^\d\d:\d\d:\d\d$", lines[2]);
            }
        }

        // The version on line 1 and the clock on line 3 differ between releases and runs; everything else must match.
        static string Masked(string panel)
        {
            var lines = Lf(panel).Split('\n');
            if (lines.Length > 0) lines[0] = Regex.Replace(lines[0], @"^SHEPHERD \d+\.\d+\.\d+ ", "SHEPHERD <version> ");
            if (lines.Length > 2) lines[2] = Regex.Replace(lines[2], @"^\d\d:\d\d:\d\d$", "<clock>");
            return string.Join("\n", lines);
        }

        static string Lf(string text) => text.Replace("\r\n", "\n");

        static void Matches(string fixture, string actual)
        {
            var path = Path.Combine(FixtureDir(), fixture);
            if (Environment.GetEnvironmentVariable("SHEPHERD_WRITE_FIXTURES") == "1")
            {
                Directory.CreateDirectory(FixtureDir());
                File.WriteAllText(path, Lf(actual));
                return;
            }
            Assert.True(File.Exists(path), "missing sample " + path);
            var expected = File.ReadAllText(path);
            if (fixture.EndsWith(".txt", StringComparison.Ordinal)) Assert.Equal(Masked(expected), Masked(actual));
            else Assert.Equal(Lf(expected), Lf(actual));
        }

        static string FixtureDir([CallerFilePath] string source = "") =>
            Path.Combine(Path.GetDirectoryName(source), "Fixtures");
    }
}
