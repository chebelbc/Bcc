using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.PSTools;

namespace MilestoneAdmin.Application.Services
{
    public sealed class InventoryLoadResult
    {
        public InventorySnapshot Snapshot { get; set; }
        public AdminError Error { get; set; }

        public bool Success => Error == null;
    }

    /// <summary>
    /// Loads the site inventory in dependency order (recording servers -> hardware -> cameras, plus storage) so pages
    /// never ask the administrator for IDs.
    /// </summary>
    public sealed class InventoryService
    {
        private readonly SessionContext _session;
        private readonly ActivityLog _activity;

        public InventoryService(SessionContext session, ActivityLog activity)
        {
            _session = session;
            _activity = activity;
        }

        public async Task<InventoryLoadResult> RefreshAsync(CancellationToken ct)
        {
            var gw = _session.Gateway;
            if (gw == null) return new InventoryLoadResult { Error = NotConnected() };
            var snapshot = new InventorySnapshot();
            try
            {
                _activity.Info("Retrieving recording servers...");
                var rs = await gw.GetRecordingServersAsync(ct).ConfigureAwait(false);
                if (!rs.Success) return Fail(rs.Raw, "retrieve recording servers");
                snapshot.RecordingServers.AddRange(rs.Items.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
                _activity.Success($"{rs.Items.Count} recording server(s) found.");

                _activity.Info("Retrieving hardware (enabled and disabled)...");
                var hw = await gw.GetHardwareAsync(null, ct).ConfigureAwait(false);
                if (!hw.Success) return Fail(hw.Raw, "retrieve hardware");
                snapshot.Hardware.AddRange(hw.Items);
                Warn(snapshot, hw.Raw);
                _activity.Success($"{hw.Items.Count} hardware device(s) found.");

                _activity.Info("Retrieving cameras (enabled and disabled)...");
                var cams = await gw.GetCamerasAsync(null, ct).ConfigureAwait(false);
                if (!cams.Success) return Fail(cams.Raw, "retrieve cameras");
                snapshot.Cameras.AddRange(cams.Items);
                Warn(snapshot, cams.Raw);
                _activity.Success($"{cams.Items.Count} camera(s) found.");

                if (_session.Catalog?.Contains("Get-VmsStorage") != false)
                {
                    var st = await gw.GetStorageAsync(null, ct).ConfigureAwait(false);
                    if (st.Success) snapshot.Storage.AddRange(st.Items);
                    else snapshot.Warnings.Add("Storage could not be read: " + st.Raw.PrimaryError?.Message);
                }

                snapshot.Join();
                snapshot.LoadedAt = DateTimeOffset.Now;
                _session.SetInventory(snapshot);
                return new InventoryLoadResult { Snapshot = snapshot };
            }
            catch (Exception ex)
            {
                var err = ErrorTranslator.FromException(ex, new OperationContext("load the site inventory", _session.SystemName));
                _activity.Error(err.Summary);
                return new InventoryLoadResult { Error = err };
            }
        }

        private static void Warn(InventorySnapshot s, PSTools.Execution.OperationResult r)
        {
            foreach (var e in r.Errors) s.Warnings.Add(e.Message);
            if (r.Truncated) s.Warnings.Add("Results were truncated.");
        }

        private InventoryLoadResult Fail(PSTools.Execution.OperationResult raw, string what)
        {
            var err = ErrorTranslator.Translate(raw, new OperationContext(what, null, _session.SystemName));
            _activity.Error(err.Summary);
            return new InventoryLoadResult { Error = err };
        }

        private static AdminError NotConnected() => new AdminError
        {
            Kind = ErrorKind.NotConnected,
            Summary = "Not connected to an XProtect management server.",
            SuggestedChecks = { "Open Connect and sign in." }
        };
    }
}
