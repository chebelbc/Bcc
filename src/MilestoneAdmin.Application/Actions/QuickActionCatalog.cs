using System.Collections.Generic;
using System.Linq;

namespace MilestoneAdmin.Application.Actions
{
    /// <summary>
    /// The guided changes offered on the inventory pages. Every command and parameter named here is checked against the
    /// MilestonePSTools catalog by CatalogDriftTests; warnings quote or paraphrase the command documentation.
    /// </summary>
    public static class QuickActionCatalog
    {
        public static readonly IReadOnlyList<QuickAction> All = new List<QuickAction>
        {
            // ------------------------------------------------------------------ hardware
            new QuickAction
            {
                Id = "hardware.edit", Title = "Edit hardware", ObjectType = "Hardware", Command = "Set-VmsHardware", SelectionParameter = "Hardware", Selection = ActionSelection.Single,
                Description = "Rename the hardware, change its description, or change the address XProtect uses to reach it.",
                Fields = { new QuickActionField("Name", currentFrom: "Name"), new QuickActionField("Description", currentFrom: "Description"), new QuickActionField("Address", currentFrom: "Address") },
                Warnings = { "Changing the address makes the recording server connect to the device at the new address. Make sure the device is reachable there." }
            },
            new QuickAction
            {
                Id = "hardware.credentials", Title = "Update device credentials", ObjectType = "Hardware", Command = "Set-VmsHardware", SelectionParameter = "Hardware", Selection = ActionSelection.Multiple,
                Description = "Change the user name and password XProtect uses for the selected hardware.",
                Fields =
                {
                    new QuickActionField("UserName", "Device user name", currentFrom: "UserName"),
                    new QuickActionField("Password", "Device password", required: true),
                    new QuickActionField("UpdateRemoteHardware", "Also change the password on the device itself")
                },
                Warnings =
                {
                    "Without 'Also change the password on the device itself', only the credentials stored in XProtect change; they must match the device or it goes offline.",
                    "With that option, MilestonePSTools changes the password on the device too. The driver must support it, XProtect 2023 R2 or later is required, and Milestone requires up to 64 characters with at least one upper-case letter, one lower-case letter and one number (only a-z, A-Z and 0-9)."
                }
            },

            // ------------------------------------------------------------------ storage
            new QuickAction
            {
                Id = "storage.add", Title = "Add recording storage", ObjectType = "Storage", Command = "Add-VmsStorage", Selection = ActionSelection.None, TargetNameField = "Name",
                Description = "Add a live recording storage to a recording server.",
                Fields =
                {
                    new QuickActionField("RecordingServer"), new QuickActionField("Name"), new QuickActionField("Description"), new QuickActionField("Path", "Path on the recording server"),
                    new QuickActionField("Retention", "Retention (d.hh:mm:ss)"), new QuickActionField("MaximumSizeMB", "Maximum size (MB)"),
                    new QuickActionField("Default", "Make this the default storage"), new QuickActionField("EnableSigning", "Enable signing")
                },
                Warnings = { "The path is created on the recording server, not on this workstation. The recording server service account needs write access to it." }
            },
            new QuickAction
            {
                Id = "storage.remove", Title = "Remove recording storage", ObjectType = "Storage", Command = "Remove-VmsStorage", ParameterSet = "ByStorage", SelectionParameter = "Storage", Selection = ActionSelection.Multiple,
                Description = "Remove storage configurations that are not the default and that no device records to.", StrongConfirmation = true,
                Warnings = { "MilestonePSTools removes the storage only if it is not the default storage and no devices record to it. The storage configuration is removed together with all archive storages attached to it." }
            },

            // ------------------------------------------------------------------ roles and users
            new QuickAction
            {
                Id = "roles.new", Title = "New role", ObjectType = "Role", Command = "New-VmsRole", Selection = ActionSelection.None, TargetNameField = "Name",
                Description = "Create a role. Permissions are configured in Management Client.",
                Fields = { new QuickActionField("Name"), new QuickActionField("Description"), new QuickActionField("AllowSmartClientLogOn"), new QuickActionField("AllowMobileClientLogOn"), new QuickActionField("AllowWebClientLogOn") }
            },
            new QuickAction
            {
                Id = "roles.add-members", Title = "Add members to role", ObjectType = "Role", Command = "Add-VmsRoleMember", ParameterSet = "ByAccountName", SelectionParameter = "Role", Selection = ActionSelection.Multiple,
                Description = "Add Windows/Active Directory users or groups, or basic users, to the selected roles.",
                Fields = { new QuickActionField("AccountName", "Accounts (domain\\user, user@domain or user; one per line)") },
                Warnings = { "Account names without a domain are looked up on the management server's machine first, then in Active Directory (Add-VmsRoleMember documentation)." }
            },
            new QuickAction
            {
                Id = "roles.remove-members", Title = "Remove from role", ObjectType = "User", Command = "Remove-VmsRoleMember", ParameterSet = "BySid", SelectionParameter = "Sid", SelectionValueProperty = "Sid",
                ParentParameter = "Role", Selection = ActionSelection.Multiple, OneOperationPerObject = false, StrongConfirmation = true,
                Description = "Remove the selected members from the role."
            },
            new QuickAction
            {
                Id = "basicusers.new", Title = "New basic user", ObjectType = "BasicUser", Command = "New-VmsBasicUser", Selection = ActionSelection.None, TargetNameField = "Name",
                Description = "Create a Milestone basic user.",
                Fields = { new QuickActionField("Name"), new QuickActionField("Password"), new QuickActionField("Description"), new QuickActionField("ForcePasswordChange"), new QuickActionField("CanChangePassword"), new QuickActionField("Status") },
                Warnings = { "A new basic user has no permissions until it is added to a role." }
            },
            new QuickAction
            {
                Id = "basicusers.enable", Title = "Enable basic users", ObjectType = "BasicUser", Command = "Set-VmsBasicUser", SelectionParameter = "BasicUser", Selection = ActionSelection.Multiple,
                Fixed = { { "Status", "Enabled" } }
            },
            new QuickAction
            {
                Id = "basicusers.disable", Title = "Lock out basic users", ObjectType = "BasicUser", Command = "Set-VmsBasicUser", SelectionParameter = "BasicUser", Selection = ActionSelection.Multiple,
                Fixed = { { "Status", "LockedOutByAdmin" } },
                Warnings = { "Locked-out users cannot log in until they are enabled again." }
            },
            new QuickAction
            {
                Id = "basicusers.password", Title = "Reset password", ObjectType = "BasicUser", Command = "Set-VmsBasicUser", SelectionParameter = "BasicUser", Selection = ActionSelection.Single,
                Fields = { new QuickActionField("Password", "New password", required: true), new QuickActionField("ForcePasswordChange", "Require a password change at next login") }
            },
            new QuickAction
            {
                Id = "basicusers.remove", Title = "Remove basic users", ObjectType = "BasicUser", Command = "Remove-VmsBasicUser", SelectionParameter = "InputObject", Selection = ActionSelection.Multiple,
                StrongConfirmation = true
            },

            // ------------------------------------------------------------------ rules
            new QuickAction
            {
                Id = "rules.enable", Title = "Enable rules", ObjectType = "Rule", Command = "Set-VmsRule", SelectionParameter = "Rule", Selection = ActionSelection.Multiple,
                Fixed = { { "Enabled", true } }
            },
            new QuickAction
            {
                Id = "rules.disable", Title = "Disable rules", ObjectType = "Rule", Command = "Set-VmsRule", SelectionParameter = "Rule", Selection = ActionSelection.Multiple,
                Fixed = { { "Enabled", false } },
                Warnings = { "Disabled rules stop triggering their actions (for example recording, notifications or outputs)." }
            },
            new QuickAction
            {
                Id = "rules.rename", Title = "Rename rule", ObjectType = "Rule", Command = "Set-VmsRule", SelectionParameter = "Rule", Selection = ActionSelection.Single,
                Fields = { new QuickActionField("Name", currentFrom: "DisplayName") }
            },

            // ------------------------------------------------------------------ Smart Client
            new QuickAction
            {
                Id = "viewgroups.new", Title = "New view group", ObjectType = "ViewGroup", Command = "New-VmsViewGroup", Selection = ActionSelection.None, TargetNameField = "Name",
                Fields = { new QuickActionField("Name"), new QuickActionField("Description") }
            },
            new QuickAction
            {
                Id = "viewgroups.edit", Title = "Rename view group", ObjectType = "ViewGroup", Command = "Set-VmsViewGroup", SelectionParameter = "ViewGroup", Selection = ActionSelection.Single,
                Fields = { new QuickActionField("Name", currentFrom: "DisplayName"), new QuickActionField("Description", currentFrom: "Description") }
            },
            new QuickAction
            {
                Id = "viewgroups.remove", Title = "Remove view groups", ObjectType = "ViewGroup", Command = "Remove-VmsViewGroup", SelectionParameter = "ViewGroup", Selection = ActionSelection.Multiple,
                StrongConfirmation = true,
                Fields = { new QuickActionField("Recurse", "Also remove all views and view groups inside") },
                Warnings = { "Removed views are no longer available to Smart Client users." }
            }
        };

        public static IEnumerable<QuickAction> For(string objectType) => All.Where(a => a.ObjectType == objectType);

        public static QuickAction Find(string id) => All.FirstOrDefault(a => a.Id == id);
    }
}
