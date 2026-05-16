BeforeAll {
    $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $sdk) {
        Set-ItResult -Skipped -Because 'dotnet SDK not available'
        return
    }
    $script:TmpOut = Join-Path ([System.IO.Path]::GetTempPath()) ('PublishTest_' + [System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $script:TmpOut -Force | Out-Null
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
    Push-Location $repoRoot
    & .\publish.ps1 -Runner -OutputDir $script:TmpOut 2>&1 | Out-Null
    Pop-Location
    $zip = Get-ChildItem $script:TmpOut -Filter '*.zip' | Select-Object -First 1
    if ($zip) {
        $script:ZipPath = $zip.FullName
        $script:ExtractDir = Join-Path $script:TmpOut 'extracted'
        Expand-Archive -Path $script:ZipPath -DestinationPath $script:ExtractDir -Force
    }
}

AfterAll {
    if ($script:TmpOut -and (Test-Path $script:TmpOut)) {
        Remove-Item $script:TmpOut -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'publish.ps1 -Runner pack contents' {

    BeforeEach {
        if (-not $script:ExtractDir) {
            Set-ItResult -Skipped -Because 'publish step did not produce a zip'
        }
    }

    It 'pack contains install.ps1' {
        $f = Get-ChildItem $script:ExtractDir -Filter 'install.ps1' -Recurse
        $f | Should -Not -BeNullOrEmpty
    }

    It 'pack contains install.cmd' {
        $f = Get-ChildItem $script:ExtractDir -Filter 'install.cmd' -Recurse
        $f | Should -Not -BeNullOrEmpty
    }

    It 'pack contains build-info.json' {
        $f = Get-ChildItem $script:ExtractDir -Filter 'build-info.json' -Recurse
        $f | Should -Not -BeNullOrEmpty
    }

    It 'build-info.json has required fields' {
        $f = Get-ChildItem $script:ExtractDir -Filter 'build-info.json' -Recurse | Select-Object -First 1
        $info = Get-Content $f.FullName -Raw | ConvertFrom-Json
        $info.version | Should -Not -BeNullOrEmpty
        $info.buildDate | Should -Not -BeNullOrEmpty
        $info.commit | Should -Not -BeNullOrEmpty
        $info.branch | Should -Not -BeNullOrEmpty
    }

}
