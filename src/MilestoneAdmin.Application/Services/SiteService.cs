using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Activity;
using MilestoneAdmin.Application.Errors;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.Domain.Inventory;

namespace MilestoneAdmin.Application.Services
{
    public sealed class SiteList
    {
        public List<GenericItem> Sites { get; } = new List<GenericItem>();
        public string Selected { get; set; }
        public AdminError Error { get; set; }
        public bool IsMultiSite => Sites.Count > 1;
    }

    /// <summary>
    /// Site selection in a Milestone Federated Architecture. MilestonePSTools commands operate on the selected site
    /// (Get-VmsSite / Select-VmsSite documentation); the session starts on the site that was logged in to.
    /// </summary>
    public sealed class SiteService
    {
        private readonly SessionContext _session;
        private readonly OperationRunner _runner;
        private readonly ActivityLog _activity;

        public SiteService(SessionContext session, OperationRunner runner, ActivityLog activity)
        {
            _session = session;
            _runner = runner;
            _activity = activity;
        }

        public bool IsAvailable => _session.Catalog?.Contains("Get-VmsSite") == true && _session.Catalog.Contains("Select-VmsSite");

        public async Task<SiteList> LoadAsync(CancellationToken ct)
        {
            var list = new SiteList();
            if (!IsAvailable) return list;
            var all = await _session.Gateway.GetSitesAsync(true, ct).ConfigureAwait(false);
            if (!all.Success)
            {
                list.Error = ErrorTranslator.Translate(all.Raw, new OperationContext("read the available sites"));
                return list;
            }

            list.Sites.AddRange(all.Items.Where(s => !string.IsNullOrEmpty(s.Name)));
            var current = await _session.Gateway.GetSitesAsync(false, ct).ConfigureAwait(false);
            list.Selected = current.Success ? current.Items.FirstOrDefault()?.Name : null;
            _session.Register(list.Sites);
            return list;
        }

        /// <summary>Switches the session to <paramref name="site"/> and re-reads the management server of that site.</summary>
        public async Task<AdminError> SelectAsync(GenericItem site, CancellationToken ct)
        {
            var ctx = new OperationContext("select the site", site.Name);
            var outcome = await _runner.RunAsync(_session.Gateway.PlanSelectSite(site), ctx, null, ct).ConfigureAwait(false);
            if (!outcome.Success) return outcome.Error;
            ManagementServerInfo server = null;
            try
            {
                server = await _session.Gateway.GetManagementServerAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _activity.Warning("The management server of the selected site could not be read: " + ex.Message);
            }

            _session.SetSite(site.Name, server);
            _activity.Success($"Site '{site.Name}' selected. Inventory and commands now target {server?.Name ?? site.Name}.");
            return null;
        }
    }
}
