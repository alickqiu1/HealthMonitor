namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Serializes tests whose assertions depend on the complete Device catalog.</summary>
/// <remarks>
/// Query rollback and provisioning cleanup must finish before another Device test begins.
/// This coordinates one test process only; never run concurrent processes against the same
/// database. CI uses sequential groups and a disposable PostgreSQL service per job.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceDatabaseCollection
{
    /// <summary>The shared collection for Device reads and independently committing provisioning.</summary>
    public const string Name = "Device database";
}
