$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$sourceRoot = Split-Path -Parent $testRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$modelExe = Join-Path $testRoot 'MonitorModelTests.exe'
$sensorExe = Join-Path $testRoot 'SensorReaderTests.exe'
& $compiler /nologo /target:exe /platform:x64 ('/out:' + $modelExe) (Join-Path $sourceRoot 'MonitorModel.cs') (Join-Path $testRoot 'MonitorModelTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Model tests failed to compile.' }
& $modelExe
if ($LASTEXITCODE -ne 0) { throw 'Model tests failed.' }
& $compiler /nologo /target:exe /platform:x64 /r:System.Management.dll ('/out:' + $sensorExe) (Join-Path $sourceRoot 'SensorReader.cs') (Join-Path $testRoot 'SensorReaderTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Sensor tests failed to compile.' }
& $sensorExe
if ($LASTEXITCODE -ne 0) { throw 'Sensor tests failed.' }
