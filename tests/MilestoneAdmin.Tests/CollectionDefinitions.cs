using Xunit;

namespace MilestoneAdmin.Tests
{
    /// <summary>
    /// PowerShell engine tests share process-wide state (environment variables, the PowerShell module cache);
    /// run them sequentially.
    /// </summary>
    [CollectionDefinition("PowerShell", DisableParallelization = true)]
    public class PowerShellCollection
    {
    }
}
