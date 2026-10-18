using System.Runtime.CompilerServices;

// Provider protocol stays internal; only the unit assembly needs direct access for network-free verification.
[assembly: InternalsVisibleTo("HealthMonitor.UnitTests")]
