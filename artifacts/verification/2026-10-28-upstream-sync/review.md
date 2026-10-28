# Upstream integration review

## Result

No remaining actionable integration findings in the reviewed working tree. I found no confirmed regression in the fork behavior covered below.

This is a source review, not a runtime approval. I did not run builds, tests, restores, or app checks. I did not edit source files, stop apps, or access user data. The parent owns CI and end-to-end verification.

## Scope

- Checkout: `/home/jshah/.t3/worktrees/typewhisper-linux/t3-a974a2aa`.
- Branch: `t3/check-upstream-and-local-status-3`.
- Reviewed merge: `f7418cc0a6731600eef01776d29a85689dea2de7`.
- First parent: `c878eced59fc4ccc7755ccc5f30b1fbcc62c1939`.
- Second parent: `d9fbf9f9`, the reviewed `upstream/linux` tip with 24 incoming commits.
- Comparisons: first parent to HEAD, upstream to HEAD, and both parents from merge base `d38b4f21`. I also inspected Git's remerge diff for the conflict resolutions and the manual XAML cleanup.
- During review, the parent added working-tree changes to `HistorySection.axaml` and `recovery_smoke.py`. I inspected those changes and left them untouched.

## Integration evidence

### Settings and audio

`src/TypeWhisper.Core/Models/AppSettings.cs:82` retains the fork's cleanup timeout. The MIGraphX and WebGPU settings and normalization remain at lines 32 to 33 and 318 to 323. The incoming change removes only the obsolete registry first-run flag. It does not remove saved plugin enablement or change microphone settings.

The only incoming production change in `AudioRecordingService.cs` is the display-name `ToString()` override at line 2051. The fork's follow-default device selection, stable device identity, and capture migration code remain intact. Device selection still uses `PersistentId` and the device index, not the display string.

### Text insertion

`src/TypeWhisper.Linux/Services/TextInsertionService.cs:1277` accepts GTK's `COMPOUND_TEXT` target as plain text on Wayland. This fits the existing non-text-format check. The fork's clipboard read-back check for copy-only delivery at line 417 and its shared paste-confirmation/restoration wait at line 934 remain unchanged by the merge.

I checked the incoming Transform Selection caller changes. They pass the captured text into the new validation method, recheck the selected text before replacement, and retain clipboard-only delivery when validation fails. I found no missed caller or incompatible change to the fork's insertion contract.

### History and recovery

The recovery service and History view-model implementations are unchanged from the first parent. The History recovery controls remain bound to the original transcript and separate recovery preview. The recent-transcriptions palette retains its recovery commands and cancellation behavior. The incoming accessible-name property at `RecentTranscriptionsPaletteViewModel.cs:209` uses the existing entry and subtitle without changing selection or recovery state.

The committed merge retained a redundant `AutomationProperties.Name` attribute beside upstream's property-element binding on the History row. The parent's pending change removes that attribute and keeps the upstream time-plus-preview name at `HistorySection.axaml:232`. The parent also updated `recovery_smoke.py` to locate that new row name. This is resolved in the reviewed working tree and is not an outstanding finding. Ensure those pending changes are included in the final handoff.

### Plugin settings and catalog refresh

The incoming changes in `PluginsSectionViewModel.cs` add an accessible expander label and its expansion notification. They do not change the settings or catalog-refresh implementation.

Opening a settings card still loads settings without calling the explicit catalog-refresh method. Explicit refresh still checks for dirty settings before saving, retains edits made during an awaited save, and protects the active plugin instance. The reviewed paths are `ToggleExpandedAsync` at line 468 and `RefreshModelCatalogAsync` at line 612.

The manual cleanup in `PluginsSection.axaml` removes the duplicate attributes from the automatic merge and keeps upstream's plugin-specific enable-toggle name.

### Notification-only overlays

The fork's `TYPEWHISPER_DISABLE_OVERLAYS=1` override remains in `DesktopDetector.UsesNotificationRecordingIndicator()`. Both `DictationOverlayWindow.axaml.cs:151` and `LiveTextWindow.axaml.cs:162` return for notification-only mode before mapping their windows or reaching the new X11 input-shape calls. I found no path added by this merge that bypasses that suppression.

The new X11 helper clears the native input region for hidden mapped overlays and restores it when shown. It does not change the fork's choice between floating windows and notifications.

### Localization

The six application locale files retain the fork's cleanup, recovery, and acceleration entries. The incoming accessibility and profile task-label additions appear in each locale. I found no locale conflict resolution that discarded a fork key or left conflict markers.

### Versions, deployment, and removed registry

`Directory.Build.props` sets the host and shared SDK build version to `0.19.0`. The app uses Avalonia `12.1.4` with `Tmds.DBus.Protocol` `0.94.1`; the test framework pin matches. The reviewed lock files use the same resolved Avalonia and Tmds versions.

The fork's shared SDK acceleration values and `ITranscriptionEngineRole` additions remain present. Plugin loading still shares the host SDK and preserves the explicit error when a plugin requires a newer SDK. The deployment scripts still rebuild the SDK and bundle plugins with the selected host build.

The online-registry publishing workflow deletion matches upstream's removal of its publisher script, registry service, bootstrap stages, and service registration. I found no surviving source reference to the deleted registry service or first-run flag. The fork's native Sherpa runtime build steps remain in `scripts/build-linux-packages.sh`; deleting the plugin-release workflow does not remove them from app packaging.

The README conflict retains the fork's GPU paragraph as requested.

## Limits

`git diff --check c878eced..HEAD` reported no whitespace errors. This is not evidence of correct runtime behavior.

The parent must use its CI and real app results to establish runtime compatibility, clipboard delivery, recovery behavior, and upgrade preservation. In particular, source review cannot prove that the installed process and its deployed plugins use the matching `0.19.0` SDK, or that text appeared at its intended destination.
