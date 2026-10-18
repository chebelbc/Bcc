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
