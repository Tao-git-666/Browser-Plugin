param([string]$BaseUrl = 'http://localhost:5165')
$ErrorActionPreference = 'Stop'
# Uses only a synthetic local fixture. Leaves its small test library in server storage.
# Requires a running API with the decompiler worker configured. No CRM or model calls.
$fixtureProject = Join-Path $PSScriptRoot 'fixtures/CrmLogicLens.SamplePlugin'
dotnet build $fixtureProject --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Sample DLL build failed.' }
$capabilities = Invoke-RestMethod "$BaseUrl/api/v1/capabilities"
if (!$capabilities.decompiler.workerAvailable) { throw 'Decompiler worker unavailable.' }
$assemblyId = [guid]::NewGuid().ToString()
$catalog = @{
    assemblies = @(@{ id=$assemblyId; name='CrmLogicLens.SamplePlugin'; sourceType=0 })
    types = @(@{ id='type-smoke'; typeName='Contoso.Crm.Plugins.AccountApprovalPlugin'; assemblyId=$assemblyId })
    steps = @(@{ id='step-smoke'; name='Account Update'; eventHandlerId='type-smoke'; messageId='msg-smoke'; filterId='filter-smoke'; stage=40; mode=0; stateCode=0; filteringAttributes='new_approved' })
    messages = @(@{ id='msg-smoke'; name='Update' })
    filters = @(@{ id='filter-smoke'; primaryObjectTypeCode='account' })
}
$dllBytes = [IO.File]::ReadAllBytes((Join-Path $fixtureProject 'bin/Debug/net10.0/CrmLogicLens.SamplePlugin.dll'))
# A new synthetic environment guarantees a cold first decompilation without removing any cache.
$organizationUrl = 'https://code-library-smoke.invalid/' + [guid]::NewGuid().ToString('N')
$batch = @{
    libraryId=$null; expectedAssemblies=1; discoveryComplete=$true
    upload=@{
        context=@{ organizationUrl=$organizationUrl; pageType='entityrecord'; entityName='account'; version='9.1'; apiVersion='v9.0' }
        capturedAt=[datetime]::UtcNow.ToString('o')
        artifacts=@(
            @{ kind='pluginCatalog'; name='plugin-catalog.json'; mediaType='application/json'; contentBase64='' },
            @{ kind='pluginAssembly'; name='CrmLogicLens.SamplePlugin.dll'; componentId=$assemblyId; version='1'; mediaType='application/octet-stream'; contentBase64=[Convert]::ToBase64String($dllBytes) }
        )
    }
}
$priorLibrary = $null
foreach ($attempt in 1..2) {
    if ($attempt -eq 2) { $catalog.steps[0].filteringAttributes = 'statuscode' }
    $batch.upload.artifacts[0].contentBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($catalog | ConvertTo-Json -Depth 10 -Compress)))
    $response = Invoke-WebRequest "$BaseUrl/api/v1/code-library/batches/stream" -Method Post -ContentType 'application/json' -Body ($batch | ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 120
    $body = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    $events = @($body -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $errorEvent = $events | Where-Object type -eq 'error'
    if ($errorEvent) { throw $errorEvent.error }
    $result = ($events | Where-Object type -eq 'result').response
    if (!$result -or $result.indexed -ne 1) { throw 'Missing complete terminal result.' }
    if ($result.cacheHit -ne ($attempt -eq 2)) { throw 'Unexpected cold/warm cache status.' }
    if ($attempt -eq 2 -and $result.libraryId -eq $priorLibrary) { throw 'New sync must create a new registration snapshot.' }
    $priorLibrary = $result.libraryId
    [pscustomobject]@{ attempt=$attempt; cacheHit=$result.cacheHit; progressEvents=@($events | Where-Object type -eq 'progress').Count; libraryId=$result.libraryId; warnings=$result.warnings } | ConvertTo-Json -Depth 4
}
'Code library streaming + real decompiler + hash cache smoke passed.'
