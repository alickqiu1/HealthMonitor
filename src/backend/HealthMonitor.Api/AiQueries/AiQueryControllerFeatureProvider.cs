using HealthMonitor.Api.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Removes the AI controller until production transport and admission controls are complete.</summary>
/// <remarks>There is intentionally no runtime bypass. Contract tests remove only this provider in their isolated host.</remarks>
internal sealed class AiQueryControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    /// <inheritdoc />
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(type => type.AsType() == typeof(AiDeviceQueriesController)).ToArray())
            feature.Controllers.Remove(controller);
    }
}
