using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MilestoneAdmin.Application.Actions;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Application.Workflows;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Csv;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.PSTools;
using MilestoneAdmin.PSTools.Commands;
using Xunit;

namespace MilestoneAdmin.Tests.EndToEnd
{
    /// <summary>End-to-end coverage of Excel onboarding, device groups, workbook import, site selection and quick actions.</summary>
    [Collection("PowerShell")]
    public class FeatureEndToEndTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static readonly global::MilestoneAdmin.Domain.Catalog.CommandCatalog RealCatalog = global::MilestoneAdmin.Domain.Catalog.CommandCatalog.Load(TestPaths.StaticCatalog, ClassificationRules.Embedded);

        private static async Task<List<GenericItem>> Query(AppHarness app, string type, Func<Protocol.PipelineStep> step)
        {
            var r = await app.Session.Gateway.QueryAsync(new GenericQuery { Title = "Retrieve " + type, ObjectType = type, Step = step }, None);
            Assert.True(r.Success, r.Raw.PrimaryError?.Message);
            app.Session.Register(r.Items);
            return r.Items;
        }

        private static async Task<BulkResult<QuickActionTarget>> Run(AppHarness app, string actionId, IReadOnlyList<VmsObject> selection, Action<QuickActionForm> fill, VmsObject parent = null, bool whatIfFirst = false)
        {
            var form = app.Actions.CreateForm(QuickActionCatalog.Find(actionId), selection, parent);
            fill?.Invoke(form);
            var errors = app.Actions.Validate(form);
            Assert.True(errors.Count == 0, string.Join("; ", errors));
            var plan = app.Actions.BuildPlan(form);
            if (whatIfFirst)
            {
                var lines = await app.Actions.RunWhatIfAsync(plan, None);
                Assert.NotEmpty(lines);
                Assert.All(lines, l => Assert.StartsWith("What if:", l));
            }

            return await app.Actions.ApplyAsync(plan, null, None);
        }

        private static FormFieldSetter Field(QuickActionForm f, string name) => new FormFieldSetter(f.Form.Fields.Single(x => x.Name == name));

        private sealed class FormFieldSetter
        {
            private readonly Application.CommandExplorer.FormField _f;
            public FormFieldSetter(Application.CommandExplorer.FormField f) => _f = f;
            public string Text { set => _f.Text = value; }
            public bool? Flag { set => _f.Flag = value; }
            public string Secret { set => _f.Secret = AppHarness.Secure(value); }
            public VmsObject Object { set { _f.Objects.Clear(); _f.Objects.Add(value); } }
        }

        [Fact]
        public async Task Excel_onboarding_validates_adds_hardware_and_assigns_device_groups()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var file = Path.Combine(app.Root, "onboarding.xlsx");
            XlsxWriter.WriteFile(file, "Hardware import", HardwareImportTemplate.Headers, new List<IReadOnlyList<string>>
            {
                new[] { "Camera", "Door - Cam", "10.1.60.1", "0", "root", "Secr3t!", "806", "", "RS01", "True", "Door", "30 days", "", "/Imported cameras;/Doors" },
                new[] { "Camera", "Bad group", "10.1.60.2", "0", "", "", "", "", "RS01", "", "", "", "", "no-leading-slash" }
            });
            var table = HardwareImportFile.Read(file);
            var wf = new CsvOnboardingWorkflow(app.Session, app.Runner, app.Activity);
            var (site, err) = await wf.BuildSiteContextAsync(table, None);
            Assert.Null(err);
            var report = wf.Load(table, site);
            Assert.Equal("2 rows detected, 1 valid, 1 invalid", report.SummaryText);
            Assert.Contains(report.Rows[1].Errors, e => e.Contains("Invalid device group path"));

            var results = await wf.ApplyAsync(report.Hardware, null, None);
            var door = results.Single();
            Assert.True(door.Succeeded, door.Error?.ToReport() + string.Join(" | ", door.Messages));
            Assert.Contains("Device group '/Imported cameras': 1 camera(s) added.", door.Messages);
            Assert.Contains("Device group '/Doors': 1 camera(s) added.", door.Messages);
            Assert.DoesNotContain(door.Messages, m => m.StartsWith("Failed", StringComparison.Ordinal));
            Assert.Contains(app.Audit.ReadAll(), a => a.SanitizedCommand.Contains("New-VmsDeviceGroup -Path '/Doors'") && a.SanitizedCommand.Contains("Add-VmsDeviceGroupMember -DeviceId"));
        }

        [Fact]
        public async Task Milestonepstools_workbook_is_previewed_and_imported_by_the_module()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var file = Path.Combine(app.Root, "export.xlsx");
            File.Copy(Path.Combine(TestPaths.RepoRoot, "tests", "Fixtures", "xlsx", "pstools-export-shaped.xlsx"), file);
            Assert.True(WorkbookImportWorkflow.IsMilestonePSToolsWorkbook(file));

            var preview = app.Workbooks.Preview(file);
            Assert.Single(preview.Hardware);
            Assert.Equal(1, preview.CameraRows);
            Assert.True(preview.ContainsPasswords);
            var plan = app.Workbooks.BuildPlan(preview, null, "root", AppHarness.Secure("Dev1ce-Pass"), updateExisting: false);
            Assert.False(plan.WhatIfAvailable);
            Assert.Contains(plan.Warnings, w => w.Contains("does not support PowerShell -WhatIf"));
            Assert.Contains("Import-VmsHardware -LiteralPath", plan.ScriptPreview);
            Assert.DoesNotContain("Dev1ce-Pass", plan.ScriptPreview);

            var before = app.Session.Inventory.Hardware.Count;
            var (outcome, _) = await app.Workbooks.ApplyAsync(preview, null, "root", AppHarness.Secure("Dev1ce-Pass"), false, null, None);
            Assert.True(outcome.Success, outcome.Error?.ToReport());
            await app.Inventory.RefreshAsync(None);
            Assert.Equal(before + 1, app.Session.Inventory.Hardware.Count);
            Assert.Contains(app.Session.Inventory.Hardware, h => h.Name == "Imported export");

            // A second import without "update existing" leaves the existing hardware alone, as MilestonePSTools does.
            var (again, _) = await app.Workbooks.ApplyAsync(app.Workbooks.Preview(file), null, null, null, false, null, None);
            Assert.True(again.Success);
            await app.Inventory.RefreshAsync(None);
            Assert.Equal(before + 1, app.Session.Inventory.Hardware.Count);
            Assert.All(app.Audit.ReadAll(), a => Assert.DoesNotContain("Dev1ce-Pass", a.ToJson().ToJson()));
        }

        [Fact]
        public async Task Site_selection_switches_the_management_server_and_drops_cached_inventory()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            Assert.True(app.Sites.IsAvailable);
            var sites = await app.Sites.LoadAsync(None);
            Assert.Null(sites.Error);
            Assert.True(sites.IsMultiSite);
            Assert.Equal("Mock Site", sites.Selected);
            Assert.NotNull(app.Session.Inventory.LoadedAt);

            var err = await app.Sites.SelectAsync(sites.Sites.Single(s => s.Name == "Mock Child Site"), None);
            Assert.Null(err);
            Assert.Equal("Mock Child Site", app.Session.SelectedSite);
            Assert.Equal("Mock Child Site", app.Session.ManagementServer.Name);
            Assert.Null(app.Session.Inventory.LoadedAt);
            Assert.Equal("Mock Child Site", (await app.Sites.LoadAsync(None)).Selected);
        }

        [Fact]
        public async Task Hardware_can_be_renamed_and_credentials_updated_without_leaking_the_password()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var lobby = app.Session.Inventory.Hardware.Single(h => h.Name == "Lobby encoder");
            var form = app.Actions.CreateForm(QuickActionCatalog.Find("hardware.edit"), new[] { lobby });
            Assert.Equal("Lobby encoder", form.Form.Fields.Single(f => f.Name == "Name").Text);
            Field(form, "Name").Text = "Lobby main";
            var plan = app.Actions.BuildPlan(form);
            Assert.Single(plan.Plan.Items);
            Assert.Equal(new[] { "Name" }, plan.Plan.Items[0].EffectiveChanges.Select(c => c.Property));
            Assert.DoesNotContain("-Address", plan.Plan.ScriptPreview); // unchanged prefilled values are not sent
            Assert.True(plan.Plan.WhatIfAvailable);
            var whatIf = await app.Actions.RunWhatIfAsync(plan, None);
            Assert.Contains(whatIf, l => l.Contains("Lobby encoder"));
            var r = await app.Actions.ApplyAsync(plan, null, None);
            Assert.Equal(1, r.Succeeded);

            var creds = await Run(app, "hardware.credentials", app.Session.Inventory.Hardware.Where(h => h.RecordingServerName == "RS01").ToList(), f =>
            {
                Field(f, "Password").Secret = "N3w-Device-Pass";
            });
            Assert.Equal(2, creds.Succeeded);
            await app.Inventory.RefreshAsync(None);
            Assert.Contains(app.Session.Inventory.Hardware, h => h.Name == "Lobby main");
            Assert.All(app.Audit.ReadAll(), a => Assert.DoesNotContain("N3w-Device-Pass", a.ToJson().ToJson()));
            Assert.DoesNotContain("N3w-Device-Pass", string.Join("\n", app.Activity.Entries.Select(e => e.Message)));
        }

        [Fact]
        public async Task Storage_can_be_added_and_removed_with_strong_confirmation()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            // Forms are generated from parameter metadata. The mock declares [object] where the real module declares
            // [RecordingServer]; use the real module's catalog so the form gets the same object picker as in production.
            app.Session.Catalog = RealCatalog;
            var rs2 = app.Session.Inventory.RecordingServers.Single(r => r.Name == "RS02");
            var add = await Run(app, "storage.add", Array.Empty<VmsObject>(), f =>
            {
                Field(f, "RecordingServer").Object = rs2;
                Field(f, "Name").Text = "Archive";
                Field(f, "Path").Text = "E:\\Media";
                Field(f, "MaximumSizeMB").Text = "1024";
                Field(f, "Retention").Text = "7.00:00:00";
            });
            Assert.Equal(1, add.Succeeded);
            await app.Inventory.RefreshAsync(None);
            var archive = app.Session.Inventory.Storage.Single(s => s.Name == "Archive");
            Assert.Equal("RS02", archive.RecordingServerName);

            var form = app.Actions.CreateForm(QuickActionCatalog.Find("storage.remove"), new VmsObject[] { archive });
            var plan = app.Actions.BuildPlan(form);
            Assert.True(plan.Plan.RequiresStrongConfirmation);
            Assert.Equal("Archive", plan.Plan.ConfirmationPhrase);
            Assert.Equal(RiskLevel.Delete, plan.Plan.Risk);
            Assert.Equal(1, (await app.Actions.ApplyAsync(plan, null, None)).Succeeded);

            // The documented rule: the default storage is not removed; the error is reported per object.
            var def = app.Session.Inventory.Storage.First(s => s.IsDefault == true);
            var refused = await Run(app, "storage.remove", new VmsObject[] { def }, null);
            Assert.Equal(1, refused.Failed);
            Assert.Contains("default storage", refused.Items[0].Error.ToReport());
        }

        [Fact]
        public async Task Role_members_and_basic_users_are_managed_through_reviewed_actions()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var roles = await Query(app, "Role", PsCommands.GetVmsRole);
            var operators = roles.Single(r => r.Name == "Operators");

            // One account cannot be resolved: the role is reported as failed with the module's error (partial failures are
            // never hidden), while the resolvable account was added.
            var add = await Run(app, "roles.add-members", new VmsObject[] { operators }, f => Field(f, "AccountName").Text = "CONTOSO\\new1\nnobody");
            Assert.Equal(BulkItemStatus.Failed, add.Items.Single().Status);
            Assert.Contains("nobody", add.Items.Single().Error.ToReport());

            var members = await app.Session.Gateway.GetRoleMembersAsync(operators, None);
            Assert.True(members.Success);
            Assert.Equal(3, members.Items.Count);
            var jdoe = members.Items.Single(m => (string)m["DisplayName"] == "CONTOSO\\jdoe");
            var form = app.Actions.CreateForm(QuickActionCatalog.Find("roles.remove-members"), new VmsObject[] { jdoe }, operators);
            var plan = app.Actions.BuildPlan(form);
            Assert.True(plan.Plan.RequiresStrongConfirmation);
            Assert.Contains("Remove-VmsRoleMember -Role $role", plan.Plan.ScriptPreview);
            Assert.Contains("-Sid 'S-1-5-21-1000-1000-1000-1101'", plan.Plan.ScriptPreview);
            Assert.DoesNotContain("TODO", plan.Plan.ScriptPreview);
            Assert.Equal(1, (await app.Actions.ApplyAsync(plan, null, None)).Succeeded);
            Assert.Equal(2, (await app.Session.Gateway.GetRoleMembersAsync(operators, None)).Items.Count);

            var created = await Run(app, "basicusers.new", Array.Empty<VmsObject>(), f =>
            {
                Field(f, "Name").Text = "guard2";
                Field(f, "Password").Secret = "Gu4rd-Pass";
            });
            Assert.Equal(1, created.Succeeded);
            var users = await Query(app, "BasicUser", PsCommands.GetVmsBasicUser);
            var guard = users.Single(u => u.Name == "guard2");
            Assert.Equal(1, (await Run(app, "basicusers.disable", new VmsObject[] { guard }, null, whatIfFirst: true)).Succeeded);
            Assert.Equal("LockedOutByAdmin", (await Query(app, "BasicUser", PsCommands.GetVmsBasicUser)).Single(u => u.Name == "guard2")["Status"]);
            Assert.Equal(1, (await Run(app, "basicusers.password", new VmsObject[] { guard }, f => Field(f, "Password").Secret = "An0ther-Pass")).Succeeded);
            Assert.Equal(1, (await Run(app, "basicusers.remove", new VmsObject[] { guard }, null)).Succeeded);
            Assert.DoesNotContain(await Query(app, "BasicUser", PsCommands.GetVmsBasicUser), u => u.Name == "guard2");
            foreach (var secret in new[] { "Gu4rd-Pass", "An0ther-Pass" })
                Assert.All(app.Audit.ReadAll(), a => Assert.DoesNotContain(secret, a.ToJson().ToJson()));
        }

        [Fact]
        public async Task Rules_and_view_groups_are_changed_only_where_needed()
        {
            await using var app = await new AppHarness().ConnectedWithInventoryAsync();
            var rules = await Query(app, "Rule", PsCommands.GetVmsRule);
            var form = app.Actions.CreateForm(QuickActionCatalog.Find("rules.disable"), rules);
            var plan = app.Actions.BuildPlan(form);
            Assert.Equal(1, plan.Plan.ChangedItemCount); // "Night alarm" is already disabled
            var r = await app.Actions.ApplyAsync(plan, null, None);
            Assert.Equal(1, r.Succeeded);
            Assert.Equal(1, r.Skipped);
            Assert.All(await Query(app, "Rule", PsCommands.GetVmsRule), x => Assert.Equal(false, x["Enabled"]));

            Assert.Equal(1, (await Run(app, "viewgroups.new", Array.Empty<VmsObject>(), f => Field(f, "Name").Text = "Parking views")).Succeeded);
            var groups = await Query(app, "ViewGroup", () => PsCommands.GetVmsViewGroup());
            var parking = groups.Single(g => g.Name == "Parking views");
            Assert.Equal(1, (await Run(app, "viewgroups.edit", new VmsObject[] { parking }, f => Field(f, "Name").Text = "Parking")).Succeeded);

            // A non-empty view group is only removed with -Recurse; without it the module's error is shown per object.
            var lobby = groups.Single(g => g.Name == "Lobby views");
            var refused = await Run(app, "viewgroups.remove", new VmsObject[] { lobby }, null);
            Assert.Equal(1, refused.Failed);
            var removed = await Run(app, "viewgroups.remove", new VmsObject[] { lobby }, f => Field(f, "Recurse").Flag = true);
            Assert.Equal(1, removed.Succeeded);
            Assert.DoesNotContain(await Query(app, "ViewGroup", () => PsCommands.GetVmsViewGroup()), g => g.Name == "Lobby views");
        }
    }
}
