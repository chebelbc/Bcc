using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Worker.Engine
{
    /// <summary>Read-only facts about the worker environment, used by the setup wizard and pre-checks.</summary>
    public static class EnvironmentProbe
    {
        public static JsonObject Collect(Runspace runspace, EngineOptions options, PSModuleInfo loadedModule)
        {
            var o = new JsonObject();
            o.Set("osVersion", Environment.OSVersion.VersionString);
            o.Set("is64BitProcess", Environment.Is64BitProcess);
            o.Set("is64BitOperatingSystem", Environment.Is64BitOperatingSystem);
            o.Set("clrVersion", Environment.Version.ToString());
            o.Set("userName", string.IsNullOrEmpty(Environment.UserDomainName) ? Environment.UserName : Environment.UserDomainName + "\\" + Environment.UserName);
            o.Set("machineName", Environment.MachineName);
            o.Set("netFrameworkRelease", NetFrameworkRelease());
            o.Set("isElevated", IsElevated());
            o.Set("workerVersion", typeof(EnvironmentProbe).Assembly.GetName().Version.ToString());

            using (var ps = PowerShell.Create())
            {
                ps.Runspace = runspace;
                ps.AddCommand("Microsoft.PowerShell.Utility\\Get-Variable").AddParameter("Name", "PSVersionTable").AddParameter("ValueOnly");
                var table = ps.Invoke().FirstOrDefault()?.BaseObject as System.Collections.IDictionary;
                if (table != null)
                {
                    o.Set("psVersion", Convert.ToString(table["PSVersion"]));
                    o.Set("psEdition", Convert.ToString(table["PSEdition"]));
                }
            }

            o.Set("languageMode", runspace.SessionStateProxy.LanguageMode.ToString());
            o.Set("executionPolicy", Safe(() =>
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = runspace;
                    ps.AddCommand("Microsoft.PowerShell.Security\\Get-ExecutionPolicy");
                    return Convert.ToString(ps.Invoke().FirstOrDefault());
                }
            }));

            var modules = new JsonArray();
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = runspace;
                ps.AddCommand("Microsoft.PowerShell.Core\\Get-Module").AddParameter("ListAvailable").AddParameter("Name", options.ModuleName);
                foreach (var m in ps.Invoke().Select(x => x.BaseObject).OfType<PSModuleInfo>().OrderByDescending(m => m.Version))
                    modules.Add(new JsonObject { { "version", m.Version.ToString() }, { "path", m.ModuleBase } });
            }

            o.Set("moduleName", options.ModuleName);
            o.Set("installedModuleVersions", modules);
            o.Set("loadedModuleVersion", loadedModule?.Version.ToString());
            o.Set("loadedModulePath", loadedModule?.ModuleBase);

            // Documented by Connect-Vms: the EULA acceptance marker lives in the roaming profile.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            o.Set("mipSdkEulaAccepted", !string.IsNullOrEmpty(appData) && File.Exists(Path.Combine(appData, "MilestonePSTools", "user-accepted-eula.txt")));
            o.Set("telemetryEnvironmentVariable", Environment.GetEnvironmentVariable("VMS_ApplicationInsights__Enabled"));
            return o;
        }

        private static string Safe(Func<string> f)
        {
            try
            {
                return f();
            }
            catch (Exception ex)
            {
                return "unknown (" + ex.GetType().Name + ")";
            }
        }

        private static JsonNode NetFrameworkRelease()
        {
#if NETFRAMEWORK
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var v = key?.GetValue("Release");
                    if (v is int i) return new JsonNumber(i);
                }
            }
            catch (Exception)
            {
                // not readable
            }
#endif
            return JsonNull.Instance;
        }

        private static bool IsElevated()
        {
#if NETFRAMEWORK
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
#else
            return false;
#endif
        }
    }
}
