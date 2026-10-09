# Envy Monitor

A portable Windows app for graphing HP Envy CPU temperature and activity from two firmware fan sensors. It also displays CPU load to help correlate workload with temperature changes.

## Run

Download [EnvyMonitor.exe](EnvyMonitor.exe), open it, and click **Enable live sensors**. Approve the Windows administrator prompt to access the HP firmware sensors. You can also launch the app with **Run as administrator**.

Requires 64-bit Windows and .NET Framework 4.8. No installation is needed. Live hardware access was validated on an HP Envy 17-da0xxx with an Intel Core Ultra 7 155H and BIOS F.09. Other models are unverified and require a compatible HP WMI firmware provider.

## Features

- CPU temperature graph in degrees Celsius.
- Independent graphs for two fan readings.
- CPU load across all logical processors.
- Two-second sampling, with 5-minute, 15-minute and 1-hour views.
- Hover inspection, pause/resume and an optional keep-on-top window.
- CSV export of all retained readings, with UTC timestamps.
- Up to 12 hours of session history in memory. Export before closing.

Charts display local time. Missing sensor readings appear as gaps. The app reads sensors without installing a driver or changing fan speeds, power settings or firmware. It makes no network requests.

## Fan units

Fan readings are labeled **raw firmware values**, with RPM units unverified. The tested firmware returns values such as `2400` and `2100`, but its response contains no unit metadata. HP Diagnostics applies a conversion that reproduces the confusing `103` display from the first value.

The app preserves the reported values without assuming a conversion, direction of speed change or percentage scale. Correlate changes with CPU temperature and audible fan activity. Missing or invalid readings do not establish that a fan has stopped.

CPU temperature comes from the HP firmware CPU sensor; it is not a per-core temperature measurement.

## Build

From the repository root, run in PowerShell:

```powershell
& .\source\Build.ps1
```

The executable is generated at `source\bin\EnvyMonitor.exe`. The build uses the .NET Framework compiler included with Windows and does not require a .NET SDK or external packages. The included executable is unsigned.

## Tests

```powershell
& .\source\tests\Run-Tests.ps1
```

Thirty checks cover sensor parsing, two independent fans, missing and invalid data, history retention, graph gaps and CSV serialization. The parser checks also exercise 10,000 deterministic corrupt payloads. Hardware and UI behavior were checked on the validated laptop.

## Preview

```powershell
.\EnvyMonitor.exe --preview
```

Preview displays clearly labeled simulated readings and does not query hardware. Preview CSV exports identify their data as simulated.

See [README.txt](README.txt) for additional usage details.
