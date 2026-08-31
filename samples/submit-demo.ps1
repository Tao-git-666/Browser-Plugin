param(
    [string]$ServerUrl = "http://localhost:5165"
)

$sampleRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Convert-FileToBase64([string]$LiteralFilePath) {
    $bytes = [System.IO.File]::ReadAllBytes($LiteralFilePath)
    return [Convert]::ToBase64String($bytes)
}

$capturedAt = [DateTimeOffset]::UtcNow.ToString("o")
$snapshot = @{
    context = @{
        organizationUrl = "https://crm.example.local/Contoso"
        organizationId = "66666666-6666-6666-6666-666666666666"
        version = "9.1.0.0"
        apiVersion = "v9.1"
        pageType = "entityrecord"
        entityName = "account"
        entityId = "99999999-9999-9999-9999-999999999999"
        formId = "88888888-8888-8888-8888-888888888888"
        appId = "77777777-7777-7777-7777-777777777777"
        formLabel = "客户主窗体"
    }
    capturedAt = $capturedAt
    artifacts = @(
        @{
            kind = "formXml"
            name = "客户主窗体"
            componentId = "88888888-8888-8888-8888-888888888888"
            version = "1"
            mediaType = "application/xml"
            contentBase64 = Convert-FileToBase64 (Join-Path $sampleRoot "form.xml")
            sourceUrl = "https://crm.example.local/Contoso/api/data/v9.1/systemforms(88888888-8888-8888-8888-888888888888)"
        },
        @{
            kind = "javaScript"
            name = "new_/account.logic.js"
            componentId = "11111111-1111-1111-1111-111111111111"
            version = "1"
            mediaType = "application/javascript"
            contentBase64 = Convert-FileToBase64 (Join-Path $sampleRoot "account.logic.js")
            sourceUrl = "https://crm.example.local/Contoso/api/data/v9.1/webresourceset(11111111-1111-1111-1111-111111111111)"
        },
        @{
            kind = "ribbonXml"
            name = "account ribbon"
            componentId = $null
            version = "1"
            mediaType = "application/xml"
            contentBase64 = Convert-FileToBase64 (Join-Path $sampleRoot "ribbon.xml")
            sourceUrl = "https://crm.example.local/Contoso/api/data/v9.1/RetrieveEntityRibbon(EntityName='account',RibbonLocationFilter=Microsoft.Dynamics.CRM.RibbonLocationFilters'All')"
        },
        @{
            kind = "pluginCatalog"
            name = "account plugin catalog"
            componentId = $null
            version = "1"
            mediaType = "application/json"
            contentBase64 = Convert-FileToBase64 (Join-Path $sampleRoot "plugin-catalog.json")
            sourceUrl = "https://crm.example.local/Contoso/api/data/v9.1/pluginassemblies"
        }
    )
}

$json = $snapshot | ConvertTo-Json -Depth 12
$receipt = Invoke-RestMethod `
    -Uri "$($ServerUrl.TrimEnd('/'))/api/v1/snapshots" `
    -Method Post `
    -ContentType "application/json; charset=utf-8" `
    -Body $json

$receipt | ConvertTo-Json -Depth 5
Write-Host "Snapshot $($receipt.snapshotId) queued as job $($receipt.jobId)."
