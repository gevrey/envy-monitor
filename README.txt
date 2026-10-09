ENVY MONITOR 1.0
CPU temperature and two-fan activity for this HP Envy laptop

START
Double-click EnvyMonitor.exe. Click Enable live sensors and approve the Windows
administrator prompt. Alternatively, right-click the executable and choose
Run as administrator. No installation is required.

USING THE APP
Readings are collected every two seconds while the window is open.
The top graph shows the HP firmware CPU temperature in degrees Celsius.
The lower graph shows fan 1 in teal and fan 2 in purple.
CPU load is an interval average across all logical processors.
Choose 5 min, 15 min or 1 hour to change the visible history.
Hover over a graph to inspect a reading and its local timestamp.
Pause freezes collection and the graphs. Resume continues collection;
longer pauses appear as gaps.
Export CSV saves all retained readings, including both fans and CPU load.
CSV timestamps use UTC; charts use the Windows local timezone.
Keep on top keeps the window above other windows.

FAN READING LIMITATION
Fan values are deliberately labeled as raw firmware values, not verified RPM
or percent. The installed HP Diagnostics app applies a conversion that gives
contradictory readings on this laptop. Its displayed 103 can be reproduced
from the firmware bytes that this monitor decodes as 2400.
The firmware response contains no unit metadata, so the app does not assume
a conversion, a direction of speed change, or a percentage scale.
Use this graph to correlate changes in firmware readings with temperature
and audible fan changes. A flat line means a constant reported value.
Missing, zero or invalid fan responses produce gaps and an unavailable
reading; they do not establish that a fan has stopped. No audio is recorded.

HISTORY
History is held in memory for this session, up to 21,600 samples (about 12 hours
at two-second intervals). Older samples are discarded. Export before closing
to retain your measurements. There is no background service or auto-start.
Each launched window starts a separate session.

REQUIREMENTS AND ACCESS
Built for 64-bit Windows with .NET Framework 4.8, already present on this PC.
Validated on HP Envy 17-da0xxx, Core Ultra 7 155H, BIOS F.09.
Uses the existing HP firmware WMI provider in root\WMI. Other HP models may
expose different layouts and are not validated. CPU temperature is the HP
firmware CPU sensor, not a per-core temperature or a calibrated instrument.
Without administrator access, CPU load works but HP hardware readings are
unavailable. If a temporary provider error occurs, collection retries.

The app reads sensors only. It does not install drivers, adjust power modes,
set fan speeds, update firmware, send telemetry or make network requests.
The locally built executable has no commercial code-signing certificate.

SOURCE AND CHECKS
Source code and Build.ps1 are in the source folder. To rebuild in PowerShell:
  & .\source\Build.ps1
The executable is generated in source\bin.
Run the included verification harnesses with:
  & .\source\tests\Run-Tests.ps1
Thirty checks cover parsing, absent/invalid sensor data, two independent fans,
history, graph gaps, CSV encoding/escaping and 10,000 corrupt sensor payloads.
The app was also checked against live HP firmware and visually in preview mode.

OPTIONAL PREVIEW
  .\EnvyMonitor.exe --preview
This displays clearly labeled simulated readings for layout testing. Preview
CSV exports are also labeled as simulated. Preview does not query hardware.
