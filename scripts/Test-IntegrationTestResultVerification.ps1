<#
.SYNOPSIS
Exercises the TRX verifier with clearly synthetic reports; never runs database tests.
.DESCRIPTION
Checks valid inventories and rejection of incomplete/failed/skipped/malformed cases.
Temporary XML fixtures are isolated under the OS temporary directory and removed in finally.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verifier = Join-Path $PSScriptRoot 'Verify-IntegrationTestResults.ps1'
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('healthmonitor-trx-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureDirectory)
$fixturePath = Join-Path $fixtureDirectory 'synthetic.trx'
$ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'

# Independent expected-case fixtures exercise the public verification contract.
$fixtures = @{
    Receipts = @{
        Class = 'HealthMonitor.IntegrationTests.Telemetry.SimulationBatchRepositoryTests'
        Cases = @('Constructor_NullContext_Throws', 'Add_NullReceipt_Throws',
            'FindByIdAsync_MissingReceipt_ReturnsNull', 'Add_StagesReceiptWithoutSaving',
            'FindByIdAsync_ExistingReceipt_ReturnsStoredValues', 'Transaction_Commit_MakesReceiptVisible',
            'Transaction_Rollback_DoesNotPersistReceipt', 'DuplicateBatchId_IsRejectedByPostgreSql',
            'ReadCommitted_SeesReceiptAfterAnotherTransactionCommits')
    }
    Catalog = @{
        Class = 'HealthMonitor.IntegrationTests.Device.DeviceCatalogQueriesTests'
        Cases = @('GetByIdsAsync_MixedDuplicatesUnknownAndRetired_ProjectsDistinctOrderedUntrackedRecords',
            'GetActiveAsync_ExcludesRetiredAndPreservesCompleteOrderedProjection',
            'GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated(targetCount: 1000)',
            'GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated(targetCount: 1001)',
            'Queries_PreCanceledToken_PropagatesCancellation')
    }
    Provisioning = @{
        Class = 'HealthMonitor.IntegrationTests.Provisioning.DeviceCatalogProvisionerTests'
        Cases = @('EmptyCatalog_InsertsAll24ExpectedDevices', 'RepeatedProvisioning_ReturnsZeroAndPreservesRows',
            'PartialMatchingCatalog_InsertsOnlyMissingDevices', 'FirstRun_PreservesUnrelatedRows',
            'ConflictingId_RejectsWithoutChangingState', 'ConflictingAssetTag_RejectsWithoutChangingState',
            'ConflictingMetadata_RejectsWithoutChangingState(field: "displayName")',
            'ConflictingMetadata_RejectsWithoutChangingState(field: "siteCode")',
            'ConflictingMetadata_RejectsWithoutChangingState(field: "deviceType")',
            'RetiredSeed_RejectsWithoutReactivation',
            'Capacity_ExactLimitSucceeds_OverflowRejects(projectedCount: 1000)',
            'Capacity_ExactLimitSucceeds_OverflowRejects(projectedCount: 1001)',
            'ConcurrentProvisioning_InsertsOnce')
    }
}

function New-SyntheticReport([string]$Group) {
    $doc = [xml]"<TestRun xmlns='$ns'><Results/><TestDefinitions/><ResultSummary outcome='Completed'><Counters/></ResultSummary></TestRun>"
    $results = $doc.DocumentElement.ChildNodes[0]
    $definitions = $doc.DocumentElement.ChildNodes[1]
    foreach ($case in $fixtures[$Group].Cases) {
        $id = [guid]::NewGuid().ToString()
        $unit = $doc.CreateElement('UnitTest', $ns)
        $unit.SetAttribute('id', $id)
        $unit.SetAttribute('name', $fixtures[$Group].Class + '.' + $case)
        $method = $doc.CreateElement('TestMethod', $ns)
        $method.SetAttribute('className', $fixtures[$Group].Class)
        $method.SetAttribute('name', ($case -split '\(', 2)[0])
        [void]$unit.AppendChild($method)
        [void]$definitions.AppendChild($unit)
        $result = $doc.CreateElement('UnitTestResult', $ns)
        $result.SetAttribute('testId', $id)
        $result.SetAttribute('outcome', 'Passed')
        [void]$results.AppendChild($result)
    }
    $counters = $doc.DocumentElement.ChildNodes[2].FirstChild
    foreach ($key in @('total', 'executed', 'passed')) { $counters.SetAttribute($key, [string]$fixtures[$Group].Cases.Count) }
    $counters.SetAttribute('failed', '0')
    $counters.SetAttribute('notExecuted', '0')
    return ,$doc
}

$checks = 0
function Assert-Report([string]$Label, [xml]$Report, [string]$Group, [bool]$ShouldPass) {
    $Report.Save($fixturePath)
    $accepted = $false
    try { & $verifier -Path $fixturePath -Group $Group | Out-Null; $accepted = $true } catch { }
    if ($accepted -ne $ShouldPass) { throw "Verifier self-test failed: $Label" }
    $script:checks++
}

try {
    foreach ($group in @('Receipts', 'Catalog', 'Provisioning')) {
        Assert-Report "valid $group" (New-SyntheticReport $group) $group $true
    }
    foreach ($outcome in @('Failed', 'NotExecuted', 'Aborted')) {
        $doc = New-SyntheticReport 'Catalog'
        $doc.DocumentElement.ChildNodes[0].FirstChild.SetAttribute('outcome', $outcome)
        Assert-Report $outcome $doc 'Catalog' $false
    }
    $doc = New-SyntheticReport 'Catalog'
    [void]$doc.DocumentElement.ChildNodes[0].RemoveChild($doc.DocumentElement.ChildNodes[0].LastChild)
    Assert-Report 'missing result' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    [void]$doc.DocumentElement.ChildNodes[0].RemoveChild($doc.DocumentElement.ChildNodes[0].FirstChild)
    [void]$doc.DocumentElement.ChildNodes[1].RemoveChild($doc.DocumentElement.ChildNodes[1].FirstChild)
    foreach ($key in @('total', 'executed', 'passed')) { $doc.DocumentElement.ChildNodes[2].FirstChild.SetAttribute($key, '4') }
    Assert-Report 'missing method with consistent reduced counters' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[1].FirstChild.FirstChild.SetAttribute('name', 'UnreviewedMethod')
    $doc.DocumentElement.ChildNodes[1].FirstChild.SetAttribute('name', $fixtures.Catalog.Class + '.UnreviewedMethod')
    Assert-Report 'unexpected method' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[0].RemoveAll()
    Assert-Report 'zero execution' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[1].ChildNodes[3].SetAttribute('name', $fixtures.Catalog.Class + '.GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated(targetCount: 1002)')
    Assert-Report 'wrong boundary variant' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[1].ChildNodes[3].SetAttribute('name', $fixtures.Catalog.Class + '.GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated(targetCount: 1000)')
    Assert-Report 'duplicate boundary variant' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[1].FirstChild.FirstChild.SetAttribute('className', 'Unexpected.Class')
    Assert-Report 'wrong class' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[0].ChildNodes[1].SetAttribute('testId', $doc.DocumentElement.ChildNodes[0].FirstChild.GetAttribute('testId'))
    Assert-Report 'duplicate result ID' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[2].FirstChild.SetAttribute('notExecuted', '1')
    Assert-Report 'summary skip' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Catalog'
    $doc.DocumentElement.ChildNodes[2].SetAttribute('outcome', 'Aborted')
    Assert-Report 'incomplete run' $doc 'Catalog' $false
    $doc = New-SyntheticReport 'Provisioning'
    $doc.DocumentElement.ChildNodes[1].ChildNodes[6].SetAttribute('name', $fixtures.Provisioning.Class + '.ConflictingMetadata_RejectsWithoutChangingState(field: "unknown")')
    Assert-Report 'missing metadata variant' $doc 'Provisioning' $false
    $accepted = $false
    try { & $verifier -Path (Join-Path $fixtureDirectory 'missing.trx') -Group Catalog | Out-Null; $accepted = $true } catch { }
    if ($accepted) { throw 'Missing report was accepted.' }
    $checks++
    Write-Output "TRX verifier self-tests: $checks passed. These synthetic reports are not integration-test evidence."
} finally {
    # Delete only this invocation's known files and empty directory; never recursively clean a computed tree.
    if (Test-Path -LiteralPath $fixturePath) { Remove-Item -LiteralPath $fixturePath }
    [IO.Directory]::Delete($fixtureDirectory)
}
