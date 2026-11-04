# macOS file panels

This source implements the existing UnityStandaloneFileBrowser C ABI used by `StandaloneFileBrowserMac.cs`. Open-file, open-folder and save requests queue copied arguments onto AppKit's main thread and use `beginSheetModalForWindow:completionHandler:` (or `beginWithCompletionHandler:` when no owner window is available). The asynchronous paths never enter `runModal`.

`NativeFileDialogs` prepares the open/save panels after scene startup in interactive runs. Panels and filter accessories are reused; the common single-filter case needs no custom accessory. Empty directory strings retain the panel's location, and unchanged directories are not reassigned. No disk availability checks run before presenting a panel. Native callbacks remain rooted in the managed wrapper and are posted back to Unity by `NativeFileDialogs`. Exiting Play Mode, quitting, or reloading managed assemblies cancels requests and invalidates native callbacks before the managed domain is unloaded; queued requests cannot display a stale panel afterward.

Rebuild only the plugin (both Apple Silicon and Intel, macOS 12+):

```sh
bash Native/StandaloneFileBrowserMac/build.sh
```

The helper signs and verifies a clean bundle in a temporary directory, then updates the source plugin's executable and signature. Unity `.meta` files remain outside the signed resources. This does not rebuild `build.app`. Restart an Editor session that has already loaded the native plugin before testing the rebuilt library.

Run the native UI checks on a Mac with a desktop session:

```sh
bash Native/StandaloneFileBrowserMac/tests/run.sh
```

The check displays and cancels its own panels automatically. It verifies that the ordinary application run loop keeps advancing while a panel is visible, cached-panel reconfiguration, Unicode and callback behavior. It does not accept files or write a selected save path. Cocoa's file-picker XPC service must be accessible; restricted command sandboxes may block it.

Run `ProtobotEditorTools.BuildFileValidation.Smoke`, `BuildTransferValidation.Smoke` and `CustomModelValidation.Smoke` in Unity for the managed save/load/import paths. Synchronous SFB exports remain for ABI compatibility; application code should use `NativeFileDialogs`' asynchronous methods.

Apple references: [presenting a panel asynchronously](https://developer.apple.com/documentation/appkit/nssavepanel/begin(completionhandler:)), [sheet presentation](https://developer.apple.com/documentation/appkit/nssavepanel/beginsheetmodal(for:completionhandler:)).
