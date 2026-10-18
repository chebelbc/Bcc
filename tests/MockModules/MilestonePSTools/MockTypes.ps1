# TEST DOUBLE - NOT MILESTONE SOFTWARE.
# Defines attribute types with the same names as the ones MilestonePSTools declares in
# src/MilestonePSTools/Attributes.cs, so the worker engine's attribute-based policy checks
# (connection guard, interactive-session detection) run exactly as they would against the real module.
if (-not ('RequiresVmsConnectionAttribute' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequiresVmsConnectionAttribute : Attribute
{
    public RequiresVmsConnectionAttribute() { ConnectionRequired = true; AutoConnect = true; }
    public RequiresVmsConnectionAttribute(bool connectionRequired) { ConnectionRequired = connectionRequired; AutoConnect = true; }
    public bool ConnectionRequired { get; set; }
    public bool AutoConnect { get; set; }
    public string Description { get { return ConnectionRequired ? "Requires VMS connection and will attempt to connect automatically" : "Does not require a VMS connection"; } }
}
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequiresInteractiveSessionAttribute : Attribute
{
    public string Description { get { return "Requires an interactive session"; } }
}
// Same shape as MilestonePSTools' VersionInterval / RequiresVmsVersionAttribute (src/MilestonePSTools/Attributes.cs):
// RequiredVersion is a VersionInterval with MinVersion/MaxVersion; only the "minimum version" form is simulated.
public class VersionInterval
{
    public VersionInterval(string version) { MinVersion = new Version(version.Trim('[', '(', ' ').Split(',')[0]); MaxVersion = new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue); }
    public Version MinVersion { get; private set; }
    public Version MaxVersion { get; private set; }
    public override string ToString() { return MinVersion.ToString(); }
}
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequiresVmsVersionAttribute : Attribute
{
    public RequiresVmsVersionAttribute(string version) { RequiredVersion = new VersionInterval(version); }
    public VersionInterval RequiredVersion { get; private set; }
    public string Description { get { return "Requires VMS version " + RequiredVersion; } }
}
'@
}
