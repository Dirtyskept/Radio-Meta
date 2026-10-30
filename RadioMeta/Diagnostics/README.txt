RADIO META DIAGNOSTICS SOURCE AREA
=================================

Purpose
-------
This folder contains the C# implementation files whose primary purpose is
collecting, retaining, or serving diagnostic information:

- RadioMetaDiagnosticEventStore.cs
- RadioMetaDiagnosticsService.cs
- RadioMetaMemoryDiagnosticsStore.cs
- RadioMetaPerformanceMetricsStore.cs
- RadioMetaStationOperationDiagnosticsService.cs
- RadioMetaStationOperationStore.cs

The dashboard-side diagnostic source sections are grouped separately under:
WebUI/DashboardSections/Diagnostics/

CURRENT BUILD BEHAVIOR
----------------------
All files in this folder are included by the SDK-style project and remain
compiled and active. The dashboard builder also includes all four diagnostic
sections. No diagnostic endpoint, panel, sampler, counter, event store, or
background service is disabled or removed by this organization change.

BOUNDARIES NOT YET DECOUPLED
----------------------------
This is the first structural separation step, not the final optional-diagnostics
architecture. Several core components still reference diagnostic services or
stores directly, including Plugin.cs, RadioMetadataReader.cs,
RadioMetadataReader.IcyNetwork.cs, RadioMetaAdaptiveRoutingStateStore.cs, and
station-management services. Those references are intentionally unchanged.

A later separation step should introduce narrow diagnostic interfaces and
no-op implementations at those boundaries, then make the diagnostic C# area
and dashboard sections conditional through explicit build configurations.
That later step must preserve the current Diagnostic build behavior and must
not change playback, metadata routing, station mutation, proxy, or display
behavior. Do not exclude this folder or the dashboard diagnostic sections from
a build until those interfaces and build checks exist.
