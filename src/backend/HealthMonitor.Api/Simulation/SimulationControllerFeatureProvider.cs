using HealthMonitor.Api.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HealthMonitor.Api.Simulation;

/// <summary>Runs after standard controller discovery so disabled simulation has no registered route.</summary>
/// <param name="enabled">Validated startup mode; false removes only this module's controller.</param>
internal sealed class SimulationControllerFeatureProvider(bool enabled) : IApplicationFeatureProvider<ControllerFeature>
{
    /// <inheritdoc />
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (enabled) return;
        foreach (var controller in feature.Controllers.Where(type => type.AsType() == typeof(SimulationController)).ToArray())
            feature.Controllers.Remove(controller);
    }
}
