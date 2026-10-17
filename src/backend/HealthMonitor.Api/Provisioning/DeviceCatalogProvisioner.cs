
using Npgsql;

namespace HealthMonitor.Api.Provisioning;

/// <summary>
/// Provisions the fixed fictional device catalog in PostgreSQL.
/// </summary>
/// <remarks>
/// This class is intended exclusively for an explicitly invoked,
/// private provisioning command. It must not run during normal API
/// startup or be exposed through a public HTTP endpoint.
///
/// Provisioning is idempotent: matching records are preserved,
/// missing records are inserted, and conflicting records cause
/// the operation to fail without modifying existing data.
///
/// The caller must provide a data source configured with the
/// privileged provisioning connection, not the API runtime account.
/// </remarks>
internal sealed class DeviceCatalogProvisioner
{
    // Edition 1.5 bounds the active fleet; retired history does not consume this capacity.
    private const int MaximumActiveDeviceCount = 1_000;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a provisioner using an explicitly supplied data source.
    /// </summary>
    /// <param name="dataSource">
    /// Database connection source owned by the private command.
    /// </param>
    /// <exception cref="ArgumentNullException">The supplied data source is null.</exception>
    public DeviceCatalogProvisioner(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource
            ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Ensures that the fixed 24-device catalog exists in PostgreSQL.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token used to cancel database operations.
    /// </param>
    /// <returns>
    /// Number of newly inserted device records. Returns zero when
    /// the active fixed catalog already matches and the active fleet remains within capacity.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the catalog definitions are invalid, an existing
    /// device conflicts with the fixed inventory, or the active
    /// fleet capacity of 1,000 registrations would be exceeded.
    /// </exception>
    /// <remarks>
    /// Fixed definitions are validated before database access. Inventory reads,
    /// conflict checks, capacity checks, and inserts share one transaction.
    /// The table lock prevents concurrent writers from invalidating those checks.
    ///
    /// Retired rows still reserve identities and tags, but do not consume active
    /// capacity. A retired fixed seed is a conflict, never a request to reactivate it.
    ///
    /// Failure before commit rolls back the transaction on disposal. Cancellation
    /// or loss of the commit acknowledgement does not prove that commit failed;
    /// retrying provisioning rechecks committed state without replacing matching rows.
    /// </remarks>
    public async Task<int> ProvisionAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = FixedDeviceCatalog.Devices;

        ValidateCatalog(devices);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        // Provisioning is infrequent and the catalog is small.
        // This lock serializes concurrent writers for the duration
        // of the transaction, preventing check-then-insert races.
        await using (var lockCommand = new NpgsqlCommand(
            """
            LOCK TABLE device.monitored_devices
            IN SHARE ROW EXCLUSIVE MODE
            """,
            connection,
            transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var existingById =
            new Dictionary<Guid, ExistingDevice>();

        var existingByTag =
            new Dictionary<string, ExistingDevice>(
                StringComparer.Ordinal);

        // Load existing inventory under the same transaction and lock.
        // Reading the complete catalog allows us to detect conflicts
        // before making any changes.
        await using (var readCommand = new NpgsqlCommand(
            """
            SELECT id, asset_tag, display_name,
                   device_type, site_code, retired_at_utc
            FROM device.monitored_devices
            """,
            connection,
            transaction))
        {
            await using var reader =
                await readCommand.ExecuteReaderAsync(
                    cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var existing = new ExistingDevice(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    !reader.IsDBNull(5));

                existingById.Add(existing.Id, existing);
                existingByTag.Add(existing.AssetTag, existing);
            }
        }

        // Verify all matching records before inserting anything.
        // Never overwrite or reactivate an existing device.
        foreach (var device in devices)
        {
            existingById.TryGetValue(
                device.Id, out var byId);

            existingByTag.TryGetValue(
                device.AssetTag, out var byTag);

            if (byId is not null &&
                !Matches(byId, device))
            {
                throw new InvalidOperationException(
                    $"Conflicting device ID for {device.AssetTag}.");
            }

            if (byTag is not null &&
                !Matches(byTag, device))
            {
                throw new InvalidOperationException(
                    $"Conflicting asset tag: {device.AssetTag}.");
            }
        }

        int missingCount = devices.Count(
            device => !existingById.ContainsKey(device.Id));

        // Retired rows remain relevant to identity/tag conflicts, but not fleet capacity.
        // Matching checks above reject retired fixed seeds before missing active seeds are counted.
        int existingActiveCount = existingById.Values.Count(device => !device.IsRetired);
        if (existingActiveCount + missingCount >
            MaximumActiveDeviceCount)
        {
            throw new InvalidOperationException(
                "Provisioning would exceed the active device catalog limit.");
        }

        int insertedCount = 0;

        foreach (var device in devices)
        {
            // The existing record has already passed all checks.
            if (existingById.ContainsKey(device.Id))
            {
                continue;
            }

            await using var insertCommand = new NpgsqlCommand(
                """
                INSERT INTO device.monitored_devices
                    (id, asset_tag, display_name,
                     device_type, site_code,
                     created_at_utc, retired_at_utc)
                VALUES
                    (@id, @assetTag, @displayName,
                     @deviceType, @siteCode,
                     CURRENT_TIMESTAMP, NULL)
                """,
                connection,
                transaction);

            insertCommand.Parameters.AddWithValue(
                "id", device.Id);

            insertCommand.Parameters.AddWithValue(
                "assetTag", device.AssetTag);

            insertCommand.Parameters.AddWithValue(
                "displayName", device.DisplayName);

            insertCommand.Parameters.AddWithValue(
                "deviceType", device.DeviceType);

            insertCommand.Parameters.AddWithValue(
                "siteCode", device.SiteCode);

            insertedCount +=
                await insertCommand.ExecuteNonQueryAsync(
                    cancellationToken);
        }

        // Only commit after every required insert succeeds.
        await transaction.CommitAsync(cancellationToken);

        return insertedCount;
    }

    /// <summary>
    /// Validates the complete fixed catalog before database access.
    /// </summary>
    /// <param name="devices">Fixed seed definitions, including stable IDs and canonical metadata.</param>
    /// <exception cref="InvalidOperationException">The catalog size, uniqueness, site distribution, or metadata is invalid.</exception>
    private static void ValidateCatalog(
        IReadOnlyList<DeviceSeedDefinition> devices)
    {
        if (devices.Count != 24)
        {
            throw new InvalidOperationException(
                "The fixed catalog must contain exactly 24 devices.");
        }

        if (devices.Select(d => d.Id)
                   .Distinct()
                   .Count() != devices.Count)
        {
            throw new InvalidOperationException(
                "The fixed catalog contains duplicate device IDs.");
        }

        if (devices.Select(d => d.AssetTag)
                   .Distinct(StringComparer.Ordinal)
                   .Count() != devices.Count)
        {
            throw new InvalidOperationException(
                "The fixed catalog contains duplicate asset tags.");
        }

        // The chosen fictional inventory distributes eight devices
        // across each of three demonstration sites.
        foreach (string site in new[] { "EDM", "CGY", "DEN" })
        {
            if (devices.Count(d => d.SiteCode == site) != 8)
            {
                throw new InvalidOperationException(
                    $"Expected eight devices at site {site}.");
            }
        }

        foreach (var device in devices)
        {
            if (device.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(device.AssetTag) ||
                device.AssetTag.Length > 64 ||
                device.AssetTag != device.AssetTag.Trim() ||
                device.AssetTag !=
                    device.AssetTag.ToUpperInvariant() ||
                string.IsNullOrWhiteSpace(device.DisplayName) ||
                device.DisplayName.Length > 120 ||
                device.DisplayName != device.DisplayName.Trim() ||
                device.DeviceType is not
                    ("Server" or "Database" or "Gateway" or "SCADA") ||
                device.SiteCode is not ("EDM" or "CGY" or "DEN"))
            {
                throw new InvalidOperationException(
                    $"Invalid catalog definition: {device.AssetTag}");
            }
        }
    }

    /// <summary>
    /// Determines whether an existing record represents the exact
    /// active device expected by the fixed inventory.
    /// </summary>
    /// <param name="existing">Stored inventory metadata, including retirement state.</param>
    /// <param name="expected">The fixed definition for this identity or asset tag.</param>
    /// <returns>True only for an exact active match; retired seeds are conflicts.</returns>
    /// <remarks>Creation timestamps are excluded so repeated provisioning preserves their original values.</remarks>
    private static bool Matches(
        ExistingDevice existing,
        DeviceSeedDefinition expected)
    {
        return existing.Id == expected.Id &&
               existing.AssetTag == expected.AssetTag &&
               existing.DisplayName == expected.DisplayName &&
               existing.DeviceType == expected.DeviceType &&
               existing.SiteCode == expected.SiteCode &&
               !existing.IsRetired;
    }

    /// <summary>
    /// Read-only representation of an existing catalog row used
    /// exclusively for provisioning conflict detection.
    /// </summary>
    private sealed record ExistingDevice(
        Guid Id,
        string AssetTag,
        string DisplayName,
        string DeviceType,
        string SiteCode,
        bool IsRetired);
}
