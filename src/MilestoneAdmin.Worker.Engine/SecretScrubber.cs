using System;
using System.Collections.Generic;
using System.Linq;

namespace MilestoneAdmin.Worker.Engine
{
    /// <summary>
    /// Replaces any occurrence of the current request's secret values in text leaving the worker
    /// (error messages, warnings, host output, serialized results). Defense in depth: commands should never
    /// echo credentials, but an exception message might.
    /// </summary>
    public sealed class SecretScrubber
    {
        public const string Mask = "********";
        private readonly string[] _secrets;

        public SecretScrubber(IEnumerable<string> secrets)
        {
            // Very short values would mask unrelated text; such secrets are still never logged by the worker itself.
            _secrets = (secrets ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s) && s.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length)
                .ToArray();
        }

        public static readonly SecretScrubber None = new SecretScrubber(null);

        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text) || _secrets.Length == 0) return text;
            foreach (var s in _secrets)
            {
                if (text.IndexOf(s, StringComparison.Ordinal) >= 0) text = text.Replace(s, Mask);
            }

            return text;
        }
    }
}
