using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Session;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Commands;

namespace MilestoneAdmin.Application.Services
{
    /// <summary>
    /// Supplies the choices for object pickers, so users select objects instead of typing names or GUIDs. Recording
    /// servers, hardware, cameras and storage come from the loaded inventory; other types are listed with their read-only
    /// producer command (classification-rules.json objectTypes) and cached per site.
    /// </summary>
    public sealed class ObjectLookupService
    {
        /// <summary>Types whose producer would need a time range or parent object, or can return very large lists.</summary>
        private static readonly HashSet<string> NotListed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bookmark", "MarkedData", "VmsHardwareScanResult" };

        /// <summary>Producers for types that are not in the rules' objectTypes.</summary>
        private static readonly Dictionary<string, string> ExtraProducers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Rule", "Get-VmsRule" } };

        private readonly SessionContext _session;
        private readonly InventoryService _inventory;
        private readonly Dictionary<string, List<VmsObject>> _cache = new Dictionary<string, List<VmsObject>>(StringComparer.OrdinalIgnoreCase);
        private string _cacheScope;

        public ObjectLookupService(SessionContext session, InventoryService inventory)
        {
            _session = session;
            _inventory = inventory;
        }

        /// <summary>The producer command used for <paramref name="objectType"/>, or null when the type cannot be listed.</summary>
        public string ProducerFor(string objectType)
        {
            if (NotListed.Contains(objectType)) return null;
            string producer = null;
            if (ClassificationRules.Embedded.ObjectTypes.TryGetValue(objectType, out var def)) producer = def.Producer;
            else ExtraProducers.TryGetValue(objectType, out producer);
            var cmd = producer == null ? null : _session.Catalog?.Find(producer);
            if (cmd == null || !cmd.Risk.IsReadOnly || !producer.StartsWith("Get-", StringComparison.OrdinalIgnoreCase)) return null;
            var defaultSet = cmd.DefaultParameterSet;
            if (defaultSet != null && defaultSet.Parameters.Any(p => p.Mandatory)) return null;
            return producer;
        }

        public async Task<(List<VmsObject> Items, string Problem)> GetAsync(string objectType, CancellationToken ct)
        {
            if (!_session.IsConnected) return (new List<VmsObject>(), "Connect first.");
            var inv = _session.Inventory;
            switch (objectType)
            {
                case "RecordingServer":
                case "Hardware":
                case "Camera":
                case "Storage":
                    if (inv.LoadedAt == null)
                    {
                        var r = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
                        if (!r.Success) return (new List<VmsObject>(), r.Error?.Summary);
                        inv = _session.Inventory;
                    }

                    var list = objectType == "RecordingServer" ? inv.RecordingServers.Cast<VmsObject>()
                        : objectType == "Hardware" ? inv.Hardware.Cast<VmsObject>()
                        : objectType == "Camera" ? inv.Cameras.Cast<VmsObject>()
                        : inv.Storage.Cast<VmsObject>();
                    return (list.ToList(), null);
            }

            var scope = (_session.SelectedSite ?? string.Empty) + "|" + (_session.ManagementServer?.Id.ToString() ?? string.Empty);
            if (scope != _cacheScope)
            {
                _cache.Clear();
                _cacheScope = scope;
            }

            if (_cache.TryGetValue(objectType, out var cached)) return (cached, null);
            var producer = ProducerFor(objectType);
            if (producer == null) return (new List<VmsObject>(), $"{objectType} objects cannot be listed for selection here. Use the inventory pages or Expert Mode.");
            var allStates = _session.Catalog.Find(producer).FindParameter("EnableFilter")?.AcceptedValues.Contains("All") == true;
            var q = await _session.Gateway.QueryAsync(new GenericQuery { Title = "Retrieve " + objectType + " list", ObjectType = objectType, Step = () => PsCommands.Producer(producer, allStates) }, ct).ConfigureAwait(false);
            if (!q.Success) return (new List<VmsObject>(), q.Raw.PrimaryError?.Message);
            var items = q.Items.Where(i => !string.IsNullOrEmpty(i.Name)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Cast<VmsObject>().ToList();
            _session.Register(items);
            _cache[objectType] = items;
            return (items, null);
        }

        public void Invalidate(string objectType = null)
        {
            if (objectType == null) _cache.Clear();
            else _cache.Remove(objectType);
        }
    }
}
