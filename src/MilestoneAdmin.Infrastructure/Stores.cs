using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MilestoneAdmin.Application.Abstractions;
using MilestoneAdmin.Application.Automation;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Infrastructure
{
    /// <summary>
    /// Locations of application data. Roaming: settings, profiles, jobs (no secrets). Local: audit log, catalog cache,
    /// diagnostics. Secrets live only in Windows Credential Manager.
    /// </summary>
    public sealed class AppPaths
    {
        public const string ProductFolder = "XProtectAdminAssistant";

        public AppPaths(string roamingRoot = null, string localRoot = null)
        {
            Roaming = roamingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder);
            Local = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);
            Directory.CreateDirectory(Roaming);
            Directory.CreateDirectory(Local);
        }

        public string Roaming { get; }
        public string Local { get; }
        public string SettingsFile => Path.Combine(Roaming, "settings.json");
        public string ProfilesFile => Path.Combine(Roaming, "profiles.json");
        public string JobsFile => Path.Combine(Roaming, "jobs.json");
        public string AuditFile => Path.Combine(Local, "audit", "audit.jsonl");
        public string CatalogCacheDirectory => Path.Combine(Local, "catalog-cache");
        public string DiagnosticsDirectory => Path.Combine(Local, "logs");
    }

    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static JsonNode TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class JsonSettingsStore : ISettingsStore
    {
        private readonly string _path;

        public JsonSettingsStore(AppPaths paths)
        {
            _path = paths.SettingsFile;
        }

        public AppSettings Load() => AppSettings.FromJson(AtomicFile.TryRead(_path)?.AsObject());

        public void Save(AppSettings settings) => AtomicFile.WriteAllText(_path, settings.ToJson().ToJson(true));
    }

    public sealed class JsonProfileStore : IProfileStore
    {
        private readonly string _path;

        public JsonProfileStore(AppPaths paths)
        {
            _path = paths.ProfilesFile;
        }

        public IReadOnlyList<ConnectionProfile> LoadAll() =>
            (AtomicFile.TryRead(_path)?.AsArray() ?? new JsonArray()).Select(n => n.AsObject()).Where(o => o != null).Select(ConnectionProfile.FromJson).ToList();

        public void SaveAll(IEnumerable<ConnectionProfile> profiles) => AtomicFile.WriteAllText(_path, new JsonArray(profiles.Select(p => (JsonNode)p.ToJson())).ToJson(true));
    }

    public sealed class JsonJobStore
    {
        private readonly string _path;

        public JsonJobStore(AppPaths paths)
        {
            _path = paths.JobsFile;
        }

        public List<JobDefinition> LoadAll() =>
            (AtomicFile.TryRead(_path)?.AsArray() ?? new JsonArray()).Select(n => n.AsObject()).Where(o => o != null).Select(JobDefinition.FromJson).ToList();

        public void SaveAll(IEnumerable<JobDefinition> jobs) => AtomicFile.WriteAllText(_path, new JsonArray(jobs.Select(j => (JsonNode)j.ToJson())).ToJson(true));
    }

    /// <summary>
    /// Append-only JSON Lines audit log with a SHA-256 hash chain: each record stores the previous record's hash, so
    /// edits or deletions inside the file are detectable with <see cref="Verify"/>. (Tamper-evident, not tamper-proof:
    /// a user with write access could rewrite the whole chain.)
    /// </summary>
    public sealed class JsonLinesAuditStore : IAuditStore
    {
        private static readonly object FileLock = new object();
        private readonly string _path;

        public JsonLinesAuditStore(AppPaths paths)
            : this(paths.AuditFile)
        {
        }

        public JsonLinesAuditStore(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
        }

        public string Location => _path;

        public void Append(AuditRecord record)
        {
            lock (FileLock)
            {
                var last = ReadAllUnlocked().LastOrDefault();
                record.Sequence = (last?.Sequence ?? 0) + 1;
                record.PreviousHash = last?.Hash ?? new string('0', 64);
                record.Hash = record.ComputeHash();
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(record.ToJson().ToJson());
                    w.Write('\n');
                }
            }
        }

        public IReadOnlyList<AuditRecord> ReadAll()
        {
            lock (FileLock) return ReadAllUnlocked();
        }

        private List<AuditRecord> ReadAllUnlocked()
        {
            var list = new List<AuditRecord>();
            if (!File.Exists(_path)) return list;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        list.Add(AuditRecord.FromJson(JsonNode.Parse(line).AsObject()));
                    }
                    catch (Exception)
                    {
                        list.Add(new AuditRecord { Sequence = -1, Operation = "(unreadable record)", Hash = "invalid" });
                    }
                }
            }

            return list;
        }

        public long? Verify()
        {
            var prev = new string('0', 64);
            foreach (var r in ReadAll())
            {
                if (r.Sequence < 0 || r.PreviousHash != prev || r.ComputeHash() != r.Hash) return r.Sequence < 0 ? 0 : r.Sequence;
                prev = r.Hash;
            }

            return null;
        }
    }

    public sealed class FileCatalogCache : ICatalogCache
    {
        private readonly string _dir;

        public FileCatalogCache(AppPaths paths)
        {
            _dir = paths.CatalogCacheDirectory;
            Directory.CreateDirectory(_dir);
        }

        private string FileFor(string version, string modulePath)
        {
            using (var sha = SHA256.Create())
            {
                var key = (version ?? "unknown") + "|" + (modulePath ?? string.Empty).ToLowerInvariant();
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty).Substring(0, 16);
                var safeVersion = new string((version ?? "unknown").Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());
                return Path.Combine(_dir, $"catalog-{safeVersion}-{hash}.json");
            }
        }

        public JsonObject TryLoad(string moduleVersion, string modulePath)
        {
            var o = AtomicFile.TryRead(FileFor(moduleVersion, modulePath))?.AsObject();
            // Only trust a cache entry for exactly this version (a re-installed module with the same version keeps its cache).
            return o != null && o.GetString("moduleVersion") == moduleVersion && o.GetString("schemaVersion") == "1.0" ? o : null;
        }

        public void Save(string moduleVersion, string modulePath, JsonObject catalog) => AtomicFile.WriteAllText(FileFor(moduleVersion, modulePath), catalog.ToJson());
    }

    /// <summary>Sanitized diagnostic log (worker stderr/log events, unexpected exceptions). Never receives secrets.</summary>
    public sealed class DiagnosticLog
    {
        private readonly object _lock = new object();
        private readonly string _file;

        public DiagnosticLog(AppPaths paths)
        {
            Directory.CreateDirectory(paths.DiagnosticsDirectory);
            _file = Path.Combine(paths.DiagnosticsDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd}.log");
        }

        public string File => _file;

        public void Write(string message)
        {
            try
            {
                lock (_lock) System.IO.File.AppendAllText(_file, $"{DateTimeOffset.Now:o} {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // diagnostics must never break the application
            }
        }
    }
}
