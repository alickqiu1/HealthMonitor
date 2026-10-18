<#
.SYNOPSIS
Validates one CI integration group's actual TRX results against its reviewed inventory.
.DESCRIPTION
Uses TestMethod metadata for method identity; only the argument suffix is parsed for the
single-parameter InlineData theories used here. This matches the pinned xUnit/VSTest
adapter's observed TRX format; recheck this contract when upgrading the adapter.
It is not a general parser for arbitrary theory display names or test frameworks.
No database/configuration access or external modules.
A green test command alone is insufficient: zero discovery, skips, omitted methods,
duplicated cases, and missing theory variants must all fail verification.
.PARAMETER Path
TRX file from exactly one integration group.
.PARAMETER Group
Reviewed inventory to require. Update inventories deliberately when tests change.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidateSet('Receipts', 'Catalog', 'Provisioning', 'Ingestion', 'Locks', 'Concurrency', 'Retention', 'HealthQueries', 'History', 'Contract', 'ReadApi')][string]$Group
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inventories = @{
    Receipts = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.SimulationBatchRepositoryTests'
        Methods = @{
            Constructor_NullContext_Throws = @('')
            Add_NullReceipt_Throws = @('')
            FindByIdAsync_MissingReceipt_ReturnsNull = @('')
            Add_StagesReceiptWithoutSaving = @('')
            FindByIdAsync_ExistingReceipt_ReturnsStoredValues = @('')
            Transaction_Commit_MakesReceiptVisible = @('')
            Transaction_Rollback_DoesNotPersistReceipt = @('')
            DuplicateBatchId_IsRejectedByPostgreSql = @('')
            ReadCommitted_SeesReceiptAfterAnotherTransactionCommits = @('')
        }
    }
    Catalog = @{
        Class = 'HealthMonitor.IntegrationTests.Device.DeviceCatalogQueriesTests'
        Methods = @{
            GetByIdsAsync_MixedDuplicatesUnknownAndRetired_ProjectsDistinctOrderedUntrackedRecords = @('')
            GetActiveAsync_ExcludesRetiredAndPreservesCompleteOrderedProjection = @('')
            GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated = @('targetCount=1000', 'targetCount=1001')
            Queries_PreCanceledToken_PropagatesCancellation = @('')
            GetDetailAsync_ActiveAndRetired_ProjectsExactLifecycleWithoutTracking = @('')
            GetDetailAsync_UnknownAndEmpty_ReturnNullWithoutTracking = @('')
        }
    }
    Provisioning = @{
        Class = 'HealthMonitor.IntegrationTests.Provisioning.DeviceCatalogProvisionerTests'
        Methods = @{
            EmptyCatalog_InsertsAll24ExpectedDevices = @('')
            RepeatedProvisioning_ReturnsZeroAndPreservesRows = @('')
            PartialMatchingCatalog_InsertsOnlyMissingDevices = @('')
            FirstRun_PreservesUnrelatedRows = @('')
            ConflictingId_RejectsWithoutChangingState = @('')
            ConflictingAssetTag_RejectsWithoutChangingState = @('')
            ConflictingMetadata_RejectsWithoutChangingState = @('field=displayName', 'field=siteCode', 'field=deviceType')
            RetiredSeed_RejectsWithoutReactivation = @('')
            Capacity_ExactLimitSucceeds_OverflowRejects = @('projectedCount=1000', 'projectedCount=1001')
            ConcurrentProvisioning_InsertsOnce = @('')
        }
    }
    Ingestion = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.TelemetryIngestionTransactionTests'
        Methods = @{
            ConfirmedCommit_CleanupFailureStillReturnsSavedResult = @('')
            PrimaryFailureType_SurvivesCleanupFailure = @('kind=conflict', 'kind=busy', 'kind=cancellation')
            NewBatch_PersistsCompleteAtomicResult = @('scenario=Healthy', 'scenario=HighLatency', 'scenario=HighCpu', 'scenario=Timeout')
            MatchingReplay_AfterRetirementAndExpiry_PreservesSavedResult = @('')
            ConflictingReplay_DoesNotWrite = @('')
            IneligibleNewBatch_RejectsWithoutPersistence = @('registrationState=missing', 'registrationState=retired')
            FailureAfterExecutedWrites_RollsBackAndInvalidatesContext = @('')
            ExceptionAfterRealCommit_SameIdRetryReplays = @('')
            EqualObservationAccepted_RegressingObservationRejects = @('chronology=equal', 'chronology=regressing')
            TimeoutPreservesMeasurementIncidents_HealthyResolvesAll = @('')
            CallerTransaction_IsRejectedAndRemainsOwned = @('transactionKind=context', 'transactionKind=enlisted')
            OneWriteAttempt_RejectsSecondBatchWithoutChangingFirst = @('')
            RepeatedViolation_PreservesOriginalIncidentIdentityAndOpening = @('')
            MissingReceiptOutsideFreshnessWindow_RejectsWithoutPersistence = @('direction=expired', 'direction=future')
        }
    }
    Locks = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.TelemetryIngestionLockTests'
        Methods = @{
            AcquiredLock_ExcludesOthers_EndsWithTransaction = @('completion=commit', 'completion=rollback')
            Contention_UsesTwoSecondTimeout_LeavesNoWrites = @('')
            CancellationWhileWaiting_PropagatesAndLeavesNoWrites = @('')
            DuplicateUnderLock_RecheckReturnsSingleCommittedBatch = @('')
        }
    }
    ReadApi = @{
        Class = 'HealthMonitor.IntegrationTests.Api.ReadApiDatabaseTests'
        Methods = @{
            GridAndMetrics_ReadRealCatalogAndTelemetry = @('')
            Grid_GlobalFiltersOrderingAndPagingUseRealRows = @('')
            Detail_RetiredAndUnknownUseRealCatalog = @('')
            Telemetry_HalfOpenRangeAndPagingThroughHttp = @('')
            Incidents_OpenResolvedAllAndPagingThroughHttp = @('')
            Validation_RejectedBeforeDatabaseCommands = @('')
            Readiness_VerifiesFixtureDatabaseAndLivenessStaysIndependent = @('')
            ProviderFailure_ReturnsSanitizedProblemThroughRealContext = @('')
        }
    }
    Contract = @{
        Class = 'HealthMonitor.IntegrationTests.Api.ReadApiContractTests'
        Methods = @{
            Grid_DefaultsSerializeExactV1ShapeAndNullMeasurements = @('')
            Grid_FilterSortPageUsesGlobalSnapshotAndStableTies = @('')
            QueryAllowlist_RejectsUnknownDuplicateCaseVariantsAndBlankTokens = @('')
            BindingErrors_MalformedGuidPagingAndDatesAreSanitized = @('')
            Detail_KnownRetiredAndUnknownFollowReadContracts = @('')
            Telemetry_DefaultPartialRangesAndBoundsUseOneClock = @('')
            Incidents_AllowlistedStateAndPagingWithoutDateFilters = @('')
            Metrics_RemainsGlobalAndPreservesFractionalFreshMean = @('')
            Failures_ReturnSanitizedProblemsAndSafeLogs = @('')
            Cors_AllowedOriginsReceiveExactReadHeaders = @('')
            Cors_UntrustedAndMissingOriginsDoNotGainBrowserAccess = @('')
            Cors_PreflightGrantsOnlyConfiguredReadMethodAndHeader = @('')
            Probes_LivenessDoesNotResolveDatabaseCheckOrPool = @('')
            Probes_ReadinessMapsResultsFactoryFailureTimeoutAndCancellationSafely = @('')
            RequestCancellation_ReachesPortsWithoutInternalError = @('')
        }
    }
    History = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.DeviceHistoryQueriesTests'
        Methods = @{
            TelemetryRange_ProjectsHalfOpenBoundsAndSelectedDeviceUntracked = @('')
            TelemetryPages_OrderTimestampUuidAndPreserveTrueCount = @('')
            HugeOffset_UsesInt64ParameterAndReturnsTrueCount = @('')
            TelemetryCountAndPage_PreserveSnapshotDuringConcurrentInsert = @('')
            IncidentPages_FilterStateAndOrderTimestampUuid = @('')
            IncidentCountAndPage_PreserveOpenMembershipDuringConcurrentResolution = @('')
            CancellationAfterCount_PropagatesAndDisposesTransaction = @('')
            OriginalReadFailure_SurvivesTransactionCleanupFailure = @('')
            ExistingTransaction_IsRejectedWithoutChangingCallerWork = @('')
        }
    }
    HealthQueries = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.DeviceHealthQueriesTests'
        Methods = @{
            NoStates_ReturnsEmptyAndUntracked = @('')
            SelectedStates_ProjectsOrderedDistinctSnapshotsAndPreservesLastSuccess = @('')
            ExactCapacityAndDuplicateInputs_AcceptsThousandDistinctAndCollapsesDuplicates = @('')
            QueryFailure_PropagatesWithoutEmptyFallback = @('')
        }
    }
    Retention = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.TelemetryRetentionStoreTests'
        Methods = @{
            EmptyBatch_NoChanges = @('')
            CutoffEqualityRetained_OneMicrosecondOlderDeleted = @('')
            LatestStateAndOpenIncidentPreserved_PingReferencesSetNull = @('')
            RetainedPingProtectsExpiredReceipt = @('')
            CombinedBudgetAcrossTables_StopsAt1000 = @('')
            TenBatchPass_ReportsRemainingBacklog = @('')
            ExecutedDeletionFailure_RollsBackAllStages = @('')
            CancelledDeletion_RollsBack = @('')
            IngestionLockContention_PreventsRetentionAndReleases = @('')
            WaitingIngestion_ProceedsAfterRetentionCommit = @('')
            RepeatedBatch_ReusesCleanContext = @('')
            FailedBatch_InvalidatesContext = @('')
        }
    }
    Concurrency = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.TelemetryIngestionConcurrencyTests'
        Methods = @{
            IdenticalConcurrentRequests_CommitOnceAcrossSources = @('')
            ConflictingConcurrentRequests_CommitOneCanonicalPayload = @('')
            DistinctConcurrentBatches_PreserveStateAndIncidentUniqueness = @('')
            FailedWriter_ReleasesLockForWaitingSuccessor = @('')
        }
    }
}
$inventory = $inventories[$Group]
$expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $inventory.Methods.GetEnumerator()) {
    foreach ($variant in $entry.Value) { [void]$expected.Add($entry.Key + '|' + $variant) }
}

# Reports are diagnostic inputs, not instructions; prohibit DTDs/external entity resolution.
$settings = [System.Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [System.Xml.XmlReader]::Create((Resolve-Path -LiteralPath $Path).Path, $settings)
try {
    $report = [System.Xml.XmlDocument]::new()
    $report.XmlResolver = $null
    $report.Load($reader)
} finally { $reader.Dispose() }

$definitions = @{}
foreach ($definition in $report.SelectNodes("/*[local-name()='TestRun']/*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
    $id = $definition.GetAttribute('id')
    if (-not $id -or $definitions.ContainsKey($id)) { throw 'Missing or duplicate TRX definition ID.' }
    $definitions[$id] = $definition
}
$results = $report.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']")
if ($results.Count -eq 0) { throw 'No integration tests executed.' }
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($result in $results) {
    if ($result.GetAttribute('outcome') -cne 'Passed') { throw 'Integration result failed, skipped, or did not execute.' }
    $id = $result.GetAttribute('testId')
    if (-not $definitions.ContainsKey($id) -or -not $seenIds.Add($id)) { throw 'Unknown or duplicate result ID.' }
    $definition = $definitions[$id]
    $methodNode = $definition.SelectSingleNode("*[local-name()='TestMethod']")
    if ($null -eq $methodNode -or $methodNode.GetAttribute('className') -cne $inventory.Class) { throw 'Unexpected test class.' }
    $method = $methodNode.GetAttribute('name')
    $prefix = $inventory.Class + '.' + $method
    $display = $definition.GetAttribute('name')
    if (-not $display.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected TRX display-name prefix.' }
    $suffix = $display.Substring($prefix.Length)
    $variant = ''
    if ($suffix) {
        # Whitespace is adapter formatting; parameter name and value identify the required case.
        if ($suffix -notmatch '^\((?<parameter>\w+):\s*(?:"(?<text>[^"]+)"|(?<number>\d+))\)$') { throw 'Unsupported theory argument format.' }
        $value = if ($Matches['text']) { $Matches['text'] } else { $Matches['number'] }
        $variant = $Matches['parameter'] + '=' + $value
    }
    $key = $method + '|' + $variant
    if (-not $expected.Contains($key) -or -not $seen.Add($key)) { throw 'Unexpected or duplicate test/theory variant.' }
}
if (-not $seen.SetEquals($expected) -or $definitions.Count -ne $results.Count) { throw 'Required test methods or theory variants are missing.' }
$summary = $report.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']")
if ($null -eq $summary -or $summary.GetAttribute('outcome') -cne 'Completed') { throw 'Test run did not complete successfully.' }
$counters = $summary.SelectSingleNode("*[local-name()='Counters']")
if ($null -eq $counters) { throw 'TRX counters are missing.' }
foreach ($name in @('total', 'executed', 'passed')) {
    if ($counters.GetAttribute($name) -cne [string]$expected.Count) { throw 'TRX counters disagree with required execution.' }
}
foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning')) {
    $value = $counters.GetAttribute($name)
    if ($value -and $value -cne '0') { throw 'TRX reports incomplete or unsuccessful execution.' }
}
Write-Output "$Group verified: $($seen.Count) passed, 0 failed, 0 skipped; all required variants present."
