using System;
using System.Collections.Generic;
using System.Security;
using MilestoneAdmin.Domain.Audit;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.PSTools;

namespace MilestoneAdmin.Application.Abstractions
{
    public enum TelemetryChoice
    {
        /// <summary>Set VMS_ApplicationInsights__Enabled=false for the worker process (application default).</summary>
        DisableForApplication,

        /// <summary>Leave MilestonePSTools telemetry as configured by the user/module.</summary>
        FollowModuleSetting
    }

    /// <summary>Application settings. Contains no secrets by design.</summary>
    public sealed class AppSettings
    {
        public bool SetupCompleted { get; set; }
        public bool ExpertMode { get; set; }
        public TelemetryChoice Telemetry { get; set; } = TelemetryChoice.DisableForApplication;

        /// <summary>
        /// The user explicitly accepted the Milestone MIP SDK EULA in the setup wizard. Only then does the application
        /// pass -AcceptEula to Connect-Vms.
        /// </summary>
        public bool MipSdkEulaAccepted { get; set; }

        public List<string> Favorites { get; set; } = new List<string> { "recorders.inventory", "hardware.add", "cameras.enable", "reports.camera-inventory" };
        public string LastProfileId { get; set; }
        public bool CaptureVerbose { get; set; }
        public int OperationTimeoutSeconds { get; set; } = 600;

        /// <summary>Optional path to a specific MilestonePSTools module (.psd1); empty = newest installed version.</summary>
        public string ModulePath { get; set; }

        public JsonObject ToJson() => new JsonObject
        {
            { "setupCompleted", SetupCompleted },
            { "expertMode", ExpertMode },
            { "telemetry", Telemetry.ToString() },
            { "mipSdkEulaAccepted", MipSdkEulaAccepted },
            { "favorites", new JsonArray(Favorites.ConvertAll(f => (JsonNode)f)) },
            { "lastProfileId", LastProfileId },
            { "captureVerbose", CaptureVerbose },
            { "operationTimeoutSeconds", OperationTimeoutSeconds },
            { "modulePath", ModulePath }
        };

        public static AppSettings FromJson(JsonObject o)
        {
            var s = new AppSettings();
            if (o == null) return s;
            s.SetupCompleted = o.GetBool("setupCompleted");
            s.ExpertMode = o.GetBool("expertMode");
            s.Telemetry = Enum.TryParse<TelemetryChoice>(o.GetString("telemetry"), out var t) ? t : TelemetryChoice.DisableForApplication;
            s.MipSdkEulaAccepted = o.GetBool("mipSdkEulaAccepted");
            var fav = o.GetArray("favorites");
            if (fav != null) s.Favorites = new List<string>(fav.Strings());
            s.LastProfileId = o.GetString("lastProfileId");
            s.CaptureVerbose = o.GetBool("captureVerbose");
            s.OperationTimeoutSeconds = (int)o.GetInt64("operationTimeoutSeconds", 600);
            s.ModulePath = o.GetString("modulePath");
            return s;
        }
    }

    /// <summary>A saved connection. Passwords are never part of a profile; see <see cref="ISecretStore"/>.</summary>
    public sealed class ConnectionProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string ServerAddress { get; set; }
        public ConnectionAuthMode AuthMode { get; set; }
        public string UserName { get; set; }
        public bool SecureOnly { get; set; }
        public bool IncludeChildSites { get; set; }
        public bool RememberPassword { get; set; }

        public string SecretKey => "XProtectAdminAssistant/profile/" + Id;

        public override string ToString() => Name ?? ServerAddress;

        public JsonObject ToJson() => new JsonObject
        {
            { "id", Id }, { "name", Name }, { "serverAddress", ServerAddress }, { "authMode", AuthMode.ToString() },
            { "userName", UserName }, { "secureOnly", SecureOnly }, { "includeChildSites", IncludeChildSites }, { "rememberPassword", RememberPassword }
        };

        public static ConnectionProfile FromJson(JsonObject o) => new ConnectionProfile
        {
            Id = o.GetString("id") ?? Guid.NewGuid().ToString("N"),
            Name = o.GetString("name"),
            ServerAddress = o.GetString("serverAddress"),
            AuthMode = Enum.TryParse<ConnectionAuthMode>(o.GetString("authMode"), out var m) ? m : ConnectionAuthMode.CurrentWindowsUser,
            UserName = o.GetString("userName"),
            SecureOnly = o.GetBool("secureOnly"),
            IncludeChildSites = o.GetBool("includeChildSites"),
            RememberPassword = o.GetBool("rememberPassword")
        };
    }

    public interface ISettingsStore
    {
        AppSettings Load();

        void Save(AppSettings settings);
    }

    public interface IProfileStore
    {
        IReadOnlyList<ConnectionProfile> LoadAll();

        void SaveAll(IEnumerable<ConnectionProfile> profiles);
    }

    /// <summary>Per-user protected secret storage (Windows Credential Manager in production).</summary>
    public interface ISecretStore
    {
        void Save(string key, string userName, SecureString secret);

        SecureString Load(string key);

        void Delete(string key);
    }

    public interface IAuditStore
    {
        /// <summary>Appends the record, assigning sequence number and hash chain.</summary>
        void Append(AuditRecord record);

        IReadOnlyList<AuditRecord> ReadAll();

        /// <summary>Verifies the hash chain; returns the sequence of the first broken record or null when intact.</summary>
        long? Verify();

        string Location { get; }
    }

    public interface ICatalogCache
    {
        JsonObject TryLoad(string moduleVersion, string modulePath);

        void Save(string moduleVersion, string modulePath, JsonObject catalog);
    }

    public interface IUserContext
    {
        string WindowsUser { get; }

        string MachineName { get; }
    }

    public interface IClock
    {
        DateTimeOffset Now { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.Now;
    }

    public sealed class EnvironmentUserContext : IUserContext
    {
        public string WindowsUser => string.IsNullOrEmpty(Environment.UserDomainName) ? Environment.UserName : Environment.UserDomainName + "\\" + Environment.UserName;

        public string MachineName => Environment.MachineName;
    }
}
