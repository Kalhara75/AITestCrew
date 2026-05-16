BeforeAll {
    . (Join-Path $PSScriptRoot '..' '..' 'install.ps1')

    function Read-Fixture {
        param([string]$Scenario, [string]$File)
        $path = Join-Path $PSScriptRoot 'fixtures' $Scenario $File
        Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
    }
}

Describe 'Merge-AppSettings' {

    It 'A: preserves ApiKey from existing when incoming is blank' {
        $existing = Read-Fixture 'A-preserve-toplevel' 'existing.json'
        $incoming = Read-Fixture 'A-preserve-toplevel' 'incoming.json'
        $expected = Read-Fixture 'A-preserve-toplevel' 'expected.json'
        $result   = Merge-AppSettings -Existing $existing -Incoming $incoming
        $result.Merged.TestEnvironment.ApiKey | Should -Be $expected.TestEnvironment.ApiKey
        $result.Preserved | Should -Contain 'TestEnvironment.ApiKey'
    }

    It 'B: preserves WinFormsAppPath in per-env block' {
        $existing = Read-Fixture 'B-preserve-per-env' 'existing.json'
        $incoming = Read-Fixture 'B-preserve-per-env' 'incoming.json'
        $expected = Read-Fixture 'B-preserve-per-env' 'expected.json'
        $result   = Merge-AppSettings -Existing $existing -Incoming $incoming
        $envKey   = ($expected.TestEnvironment.Environments.Keys | Select-Object -First 1)
        $result.Merged.TestEnvironment.Environments[$envKey].WinFormsAppPath |
            Should -Be $expected.TestEnvironment.Environments[$envKey].WinFormsAppPath
        $result.Preserved | Should -Contain ("TestEnvironment.Environments.$envKey.WinFormsAppPath")
    }

    It 'C: admin-added env appears in merged output alongside existing env' {
        $existing = Read-Fixture 'C-admin-adds-env' 'existing.json'
        $incoming = Read-Fixture 'C-admin-adds-env' 'incoming.json'
        $result   = Merge-AppSettings -Existing $existing -Incoming $incoming
        $envNames = $result.Merged.TestEnvironment.Environments.Keys
        $envNames | Should -Contain 'TASN'
        $envNames | Should -Contain 'NEMA'
    }

    It 'D: admin-removed field is absent from merged; ApiKey still preserved' {
        $existing = Read-Fixture 'D-admin-removes-field' 'existing.json'
        $incoming = Read-Fixture 'D-admin-removes-field' 'incoming.json'
        $result   = Merge-AppSettings -Existing $existing -Incoming $incoming
        # LegacyTotpSecret not in incoming -> not in merged (incoming is the authoritative baseline)
        $result.Merged.TestEnvironment.ContainsKey('LegacyTotpSecret') | Should -BeFalse
        # existing ApiKey=atc_key, incoming ApiKey='' -> ApiKey is preserved
        $result.Merged.TestEnvironment.ApiKey | Should -Be 'atc_key'
        $result.Preserved | Should -Contain 'TestEnvironment.ApiKey'
    }

    It 'E: renamed env replaces old env; old WinFormsAppPath logged as Removed' {
        $existing = Read-Fixture 'E-admin-renames-env' 'existing.json'
        $incoming = Read-Fixture 'E-admin-renames-env' 'incoming.json'
        $result   = Merge-AppSettings -Existing $existing -Incoming $incoming
        $envNames = $result.Merged.TestEnvironment.Environments.Keys
        $envNames | Should -Not -Contain 'OldEnv'
        $envNames | Should -Contain 'NewEnv'
        # OldEnv.WinFormsAppPath is a preserved-class field that has nowhere to go -> Removed
        $result.Removed | Should -Contain 'TestEnvironment.Environments.OldEnv.WinFormsAppPath'
    }

}
