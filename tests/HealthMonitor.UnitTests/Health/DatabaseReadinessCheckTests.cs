using HealthMonitor.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthMonitor.UnitTests.Health;

/// <summary>Exercises connectivity-only proof, resource ownership, sanitized failures, and cancellation without database/network access.</summary>
public sealed class DatabaseReadinessCheckTests
{
    [Fact]
    public async Task Success_ExecutesOneScalarAndReturnsOnlyBorrowedResources()
    {
        using var source = new ProbeDataSource();
        var result = await new DatabaseReadinessCheck(source).CheckHealthAsync(new());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("SELECT 1", source.Query.CommandText); Assert.Equal(5, source.Query.CommandTimeout);
        Assert.True(source.Query.WasDisposed); Assert.True(source.Connection.WasDisposed); Assert.False(source.SourceDisposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData("1")]
    public async Task UnexpectedScalar_IsNotHealthy(object? scalar)
    {
        using var source = new ProbeDataSource { Scalar = scalar };
        AssertSafeFailure(await new DatabaseReadinessCheck(source).CheckHealthAsync(new()));
        Assert.True(source.Query.WasDisposed); Assert.True(source.Connection.WasDisposed);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("execute")]
    [InlineData("commandDispose")]
    [InlineData("connectionDispose")]
    [InlineData("bothDispose")]
    public async Task FailureAtResourceBoundary_IsSanitizedAndNeverDisposesPool(string point)
    {
        using var source = new ProbeDataSource();
        if (point == "bothDispose") source.Failures.UnionWith(new[] { "commandDispose", "connectionDispose" });
        else source.Failures.Add(point);
        AssertSafeFailure(await new DatabaseReadinessCheck(source).CheckHealthAsync(new()));
        Assert.False(source.SourceDisposed);
        if (point != "open") { Assert.True(source.Query.WasDisposed); Assert.True(source.Connection.WasDisposed); }
    }

    [Fact]
    public async Task CancellationDuringOpen_IsPreservedWithoutRawException()
    {
        using var source = new ProbeDataSource { WaitForCancellation = true };
        using var cancel = new CancellationTokenSource();
        var work = new DatabaseReadinessCheck(source).CheckHealthAsync(new(), cancel.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Null(error.InnerException);
        Assert.DoesNotContain(ProbeDataSource.Canary, error.ToString()); Assert.False(source.SourceDisposed);
    }

    [Fact]
    public async Task CleanupFailure_DoesNotReplaceRequestedCancellation()
    {
        using var source = new ProbeDataSource();
        using var cancel = new CancellationTokenSource();
        source.BeforeExecute = cancel.Cancel;
        source.Failures.Add("connectionDispose");
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => new DatabaseReadinessCheck(source).CheckHealthAsync(new(), cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Null(error.InnerException);
        Assert.True(source.Query.WasDisposed); Assert.True(source.Connection.WasDisposed); Assert.False(source.SourceDisposed);
    }

    [Fact]
    public async Task UnrelatedCancellation_IsSanitizedFailure()
    {
        using var source = new ProbeDataSource { Error = new OperationCanceledException(ProbeDataSource.Canary) };
        source.Failures.Add("execute");
        AssertSafeFailure(await new DatabaseReadinessCheck(source).CheckHealthAsync(new()));
    }

    [Fact]
    public void NullSource_IsRejected() => Assert.Throws<ArgumentNullException>(() => new DatabaseReadinessCheck(null!));

    /// <summary>Requires a safe report before the framework can log or serialize it.</summary>
    /// <param name="result">Result from any provider/cleanup failure or unexpected scalar.</param>
    private static void AssertSafeFailure(HealthCheckResult result)
    {
        Assert.Equal(HealthStatus.Unhealthy, result.Status); Assert.Null(result.Exception); Assert.Empty(result.Data);
        Assert.Equal("Database connectivity unavailable.", result.Description);
    }
}
