using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace HealthMonitor.UnitTests.Health;

/// <summary>In-memory BCL data-source double for lease, execution, cancellation, and cleanup boundaries; never opens a socket.</summary>
/// <remarks>Unsupported database operations fail explicitly so tests cannot silently exercise an unmodeled path.</remarks>
internal sealed class ProbeDataSource : DbDataSource
{
    internal const string Canary = "probe-secret-canary";
    internal HashSet<string> Failures { get; } = new(StringComparer.Ordinal);
    internal object? Scalar { get; set; } = 1;
    internal Exception Error { get; set; } = new InvalidOperationException(Canary);
    internal bool WaitForCancellation { get; set; }
    internal Action? BeforeExecute { get; set; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool SourceDisposed { get; private set; }
    internal Lease Connection { get; }
    internal Command Query { get; }
    public override string ConnectionString => Canary;

    /// <summary>Creates independent fake lease/command records for each single-invocation check test.</summary>
    internal ProbeDataSource() { Connection = new Lease(this); Query = new Command(this); }
    /// <inheritdoc />
    protected override DbConnection CreateDbConnection() => Connection;
    /// <inheritdoc />
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entered.TrySetResult();
        if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Fail("open");
        return Connection;
    }
    /// <summary>Injects at one named boundary, retaining a canary exception to detect unsafe propagation.</summary>
    internal void Fail(string point) { if (Failures.Contains(point)) throw Error; }
    /// <inheritdoc />
    protected override void Dispose(bool disposing) => SourceDisposed = true;

    /// <summary>Records whether the borrowed connection lease is returned, including when disposal itself fails.</summary>
    internal sealed class Lease(ProbeDataSource owner) : DbConnection
    {
        internal bool WasDisposed { get; private set; }
        [AllowNull] public override string ConnectionString { get => Canary; set { } }
        public override string Database => "fake";
        public override string DataSource => "in-memory";
        public override string ServerVersion => "fake";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() => throw new NotSupportedException();
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => owner.Query;
        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            WasDisposed = true; owner.Fail("connectionDispose"); return ValueTask.CompletedTask;
        }
    }

    /// <summary>Captures the actual scalar query and disposal without implementing unrelated parameter/reader behavior.</summary>
    internal sealed class Command(ProbeDataSource owner) : DbCommand
    {
        internal bool WasDisposed { get; private set; }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            owner.BeforeExecute?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            owner.Fail("execute"); return Task.FromResult(owner.Scalar);
        }
        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            WasDisposed = true; owner.Fail("commandDispose"); return ValueTask.CompletedTask;
        }
    }
}
