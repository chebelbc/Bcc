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
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequiresVmsVersionAttribute : Attribute
{
    public RequiresVmsVersionAttribute(string version) { Version = version; }
    public string Version { get; set; }
    public string Description { get { return "Requires VMS version " + Version; } }
}
'@
}
