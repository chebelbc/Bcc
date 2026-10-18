using System;
using System.IO;

namespace MilestoneAdmin.Tests
{
    internal static class TestPaths
    {
        private static string _root;

        public static string RepoRoot
        {
            get
            {
                if (_root != null) return _root;
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "catalog", "classification-rules.json"))) dir = dir.Parent;
                _root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
                return _root;
            }
        }

        public static string StaticCatalog => Path.Combine(RepoRoot, "catalog", "milestonepstools-catalog.json");

        public static string MockModule => Path.Combine(RepoRoot, "tests", "MockModules", "MilestonePSTools", "MilestonePSTools.psd1");
    }
}

namespace MilestoneAdmin.Tests
{
    internal static class WorkerPaths
    {
        /// <summary>The net8.0 test build of the worker, started with "dotnet" (production uses the net48 x64 exe).</summary>
        public static string WorkerDll
        {
            get
            {
                foreach (var cfg in new[] { "Debug", "Release" })
                {
                    var p = System.IO.Path.Combine(TestPaths.RepoRoot, "src", "MilestoneAdmin.Worker", "bin", cfg, "net8.0", "MilestoneAdmin.Worker.dll");
                    if (System.IO.File.Exists(p)) return p;
                }

                throw new System.InvalidOperationException("Worker test build not found; build src/MilestoneAdmin.Worker for net8.0.");
            }
        }
    }
}
