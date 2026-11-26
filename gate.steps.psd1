@{
    Schema   = 1
    Repo     = 'Functions'
    Workflow = '.github/workflows/main_crgolden-functions.yml'
    Sonar    = @{ ProjectKey = 'crgolden_Functions' }
    Probe    = @{
        Kind     = 'dotnet'
        Projects = @('Functions.Tests.Unit', 'Functions.Tests.Integration')
        Wrap     = @{ 'Functions.Tests.Integration' = @('pwsh', '-NoProfile', '-File', 'scripts/gate/Use-TestDatabase.ps1') }
    }
    Steps    = @(
        @{ Name = 'Regenerate lock files and push'; Delivery = $true }
        @{ Name = 'Install SonarQube Cloud scanner'; Tool = 'dotnet-sonarscanner'; Inputs = @('*') }
        @{ Name = 'Restore local tools'; Run = @('dotnet', 'tool', 'restore'); Inputs = @('dotnet-tools.json'); Always = $true }
        @{
            Name   = 'Begin Sonar analysis'
            Sonar  = 'begin'
            Needs  = @('Install SonarQube Cloud scanner', 'Restore local tools')
            Inputs = @('*')
            Run    = @(
                'dotnet-sonarscanner', 'begin', '/k:{sonarProject}', '/o:crgolden', '/d:sonar.host.url=https://sonarcloud.io',
                '/d:sonar.cs.vscoveragexml.reportsPaths=coverage.xml,coverage-integration.xml',
                '/d:sonar.exclusions=**/bin/**,**/obj/**', '/d:sonar.coverage.exclusions=**/Program.cs,**/gate.ps1,**/gate.steps.psd1,scripts/gate/**',
                '/d:sonar.qualitygate.wait=true', '/d:sonar.scanner.skipJreProvisioning=true',
                '/d:sonar.scm.use.blame.algorithm=GIT_NATIVE_BLAME', '/d:sonar.branch.name={sonarBranch}'
            )
        }
        @{
            Name   = 'Clean the build outputs'
            Sonar  = 'build'
            Needs  = @('Restore local tools')
            Inputs = @('*')
            Run    = @('dotnet', 'clean', '--configuration', 'Release')
        }
        @{
            Name   = 'Restore the Functions project and its host extensions'
            Sonar  = 'build'
            Needs  = @('Clean the build outputs')
            Inputs = @('*')
            Run    = @('dotnet', 'restore', 'Functions/Functions.csproj', '/p:RestoreLockedMode=true')
        }
        @{
            Name   = 'Build with dotnet'
            Sonar  = 'build'
            Needs  = @('Restore the Functions project and its host extensions')
            Inputs = @('*')
            Run    = @('dotnet', 'build', '--configuration', 'Release', '/p:RestoreLockedMode=true', '-warnaserror')
        }
        @{
            Name   = 'ReSharper inspection (jb inspectcode)'
            Needs  = @('Build with dotnet')
            Inputs = @('*')
            Remove = @('{output}\functions-inspect.sarif')
            Run    = @('dotnet', 'jb', 'inspectcode', 'Functions.slnx', '--no-build', '-e=WARNING', '--caches-home={inspectCache}', '--output={output}\functions-inspect.sarif')
            Check  = @{ Sarif = '{output}\functions-inspect.sarif' }
        }
        @{
            Name   = 'Run unit tests with coverage'
            Needs  = @('Build with dotnet')
            Inputs = @('*', '!Functions.Tests.Integration/*')
            Remove = @('Functions.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx')
            Run    = @(
                'dotnet', 'dotnet-coverage', 'collect',
                'dotnet test --project Functions.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit --report-xunit-trx --report-xunit-trx-filename unit-tests.trx --results-directory=Functions.Tests.Unit/bin/Release/net11.0/TestResults',
                '-f', 'xml', '-o', 'coverage.xml', '-s', 'coverage.settings.xml'
            )
            Check  = @{ Trx = 'Functions.Tests.Unit\bin\Release\net11.0\TestResults\unit-tests.trx'; Floor = 1 }
        }
        @{
            Name   = 'Run integration tests with coverage'
            Needs  = @('Build with dotnet')
            Inputs = @('*', '!Functions.Tests.Unit/*')
            Remove = @('Functions.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx')
            Run    = @(
                'pwsh', '-NoProfile', '-File', 'scripts/gate/Use-TestDatabase.ps1', 'dotnet', 'dotnet-coverage', 'collect',
                'dotnet test --project Functions.Tests.Integration --no-build --configuration Release -- --filter-trait Category=Integration --report-xunit-trx --report-xunit-trx-filename integration-tests.trx --results-directory=Functions.Tests.Integration/bin/Release/net11.0/TestResults',
                '-f', 'xml', '-o', 'coverage-integration.xml', '-s', 'coverage.settings.xml'
            )
            Check  = @{ Trx = 'Functions.Tests.Integration\bin\Release\net11.0\TestResults\integration-tests.trx'; Floor = 1 }
        }
        @{ Name = 'dotnet publish'; Delivery = $true }
        @{
            Name   = 'End Sonar analysis'
            Sonar  = 'end'
            Needs  = @('Begin Sonar analysis', 'Build with dotnet')
            Inputs = @('*')
            Run    = @('dotnet-sonarscanner', 'end')
        }
        @{ Name = 'Fail on open Sonar issues'; Sonar = 'issues'; Needs = @('End Sonar analysis'); Inputs = @('*') }
        @{ Name = 'Run the app on .NET 11'; Delivery = $true }
    )
}
