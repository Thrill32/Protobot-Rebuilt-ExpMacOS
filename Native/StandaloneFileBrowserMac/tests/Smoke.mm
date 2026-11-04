#import "../StandaloneFileBrowser.mm"
#include <cstdio>
#include <functional>
#include <stdexcept>
#include <string>

static int callbacks, beats;
static void Require(bool condition, const char *message) { if (!condition) throw std::runtime_error(message); }
static double Now() { return NSProcessInfo.processInfo.systemUptime; }
static void Completed(const char *path) {
    Require(NSThread.isMainThread, "Native completion left the AppKit thread.");
    Require(path && path[0] == '\0', "Cancellation did not return an empty path.");
    callbacks++;
}
static void Pump(const std::function<bool()> &done) {
    double deadline = Now() + 15;
    while (!done()) {
        Require(Now() < deadline, "Native panel check timed out.");
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, .01, false);
    }
}
static void Visible() {
    Pump([] { return activePanel && activePanel.visible; });
    int before = beats;
    double end = Now() + .15;
    Pump([end] { return Now() >= end; });
    Require(beats > before + 3, "The picker blocked the application's ordinary run loop.");
    Require(NSApp.modalWindow == nil, "An asynchronous request entered a blocking modal loop.");
}
static void Cancel() {
    int before = callbacks;
    [activePanel cancel:nil];
    Pump([before] { return callbacks == before + 1 && !activePanel; });
}

int main() {
    @autoreleasepool {
        try {
            [NSApplication sharedApplication];
            [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
            NSWindow *owner = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 480, 260)
                styleMask:NSWindowStyleMaskTitled backing:NSBackingStoreBuffered defer:NO];
            owner.title = @"Protobot native file-picker validation";
            [NSApp finishLaunching]; [owner makeKeyAndOrderFront:nil];
            NSTimer *heartbeat = [NSTimer scheduledTimerWithTimeInterval:.01 repeats:YES block:^(NSTimer *) { beats++; }];
            DialogWarmup();
            Require(!openPanel && !savePanel, "Preparation performed Cocoa work inline.");
            Pump([] { return openPanel && savePanel; });
            NSOpenPanel *preparedOpen = openPanel;
            NSSavePanel *preparedSave = savePanel;
            Require(!openPanel.visible && !savePanel.visible, "Preparation displayed an unrequested panel.");
            NSString *directory = NSTemporaryDirectory();
            std::string title = "Sélèct 文件";
            std::string transientDirectory = directory.UTF8String;
            double requested = Now();
            DialogOpenFilePanelAsync(title.c_str(), transientDirectory.c_str(), "Build;pbb", false, Completed);
            double returnedMs = (Now() - requested) * 1000;
            title.assign(4096, 'x'); transientDirectory.assign(4096, 'x');
            Require(!activePanel && callbacks == 0, "Open entered AppKit before returning to Unity.");
            Visible();
            Require(activePanel == preparedOpen && openPanel.canChooseFiles && !openPanel.canChooseDirectories,
                "Open did not reuse its prepared file panel.");
            Require([openPanel.message isEqualToString:@"Sélèct 文件"], "Deferred arguments were not copied before P/Invoke returned.");
            Require([openPanel.allowedContentTypes.firstObject.preferredFilenameExtension isEqualToString:@"pbb"], "Unknown .pbb extension was not allowed.");
            Require(openPanel.accessoryView == nil, "A single filter recreated the redundant accessory view.");
            Cancel();

            DialogOpenFilePanelAsync("Models", directory.UTF8String, "Models;obj,stl|All files;*", true, Completed);
            Visible();
            Require(activePanel == preparedOpen && openPanel.allowsMultipleSelection && openPanel.allowedContentTypes.count == 2,
                "Open filters/multiselection retained stale panel configuration.");
            Require(openTypes.popup != nil, "Multiple filter groups lost their chooser.");
            [openTypes.popup selectItemAtIndex:1]; [openTypes changed:openTypes.popup];
            Require(openPanel.allowedContentTypes.count == 0, "The all-files group did not clear the native filter.");
            Cancel();

            DialogOpenFolderPanelAsync("Folder", directory.UTF8String, true, Completed);
            Visible();
            Require(activePanel == preparedOpen && !openPanel.canChooseFiles && openPanel.canChooseDirectories &&
                openPanel.allowedContentTypes.count == 0 && openPanel.accessoryView == nil,
                "Folder selection inherited stale file filters/accessories.");
            Cancel();

            DialogSaveFilePanelAsync("Save", directory.UTF8String, "Röbot 测试", "Build;pbb", Completed);
            Visible();
            Require(activePanel == preparedSave && [savePanel.nameFieldStringValue hasPrefix:@"Röbot 测试"],
                "Save failed to reuse its panel or preserve the Unicode default name.");
            Cancel();

            DialogOpenFilePanelAsync("First request", directory.UTF8String, "Build;pbb", false, Completed);
            Visible();
            int before = callbacks;
            DialogSaveFilePanelAsync("Overlapping request", directory.UTF8String, "Ignored", "Build;pbb", Completed);
            Pump([before] { return callbacks == before + 1; });
            Require(activePanel == preparedOpen && openPanel.visible, "Overlapping native request replaced the active picker.");
            Cancel();
            Require(callbacks == 6, "A native panel completed more than once.");

            DialogOpenFilePanelAsync("Queued before reload", directory.UTF8String, "Build;pbb", false, Completed);
            DialogReset();
            double drain = Now() + .1;
            Pump([drain] { return Now() >= drain; });
            Require(!activePanel && callbacks == 6, "A queued request survived managed-domain reset.");
            DialogOpenFilePanelAsync("Visible before reload", directory.UTF8String, "Build;pbb", false, Completed);
            Visible(); DialogReset();
            Pump([] { return !activePanel; });
            Require(callbacks == 6, "Closing a panel called an unloaded managed callback.");
            DialogOpenFilePanelAsync("After reload", directory.UTF8String, "Build;pbb", false, Completed);
            Visible(); Cancel();
            Require(callbacks == 7, "The native bridge could not reopen after managed-domain reset.");

            NSArray *urls = @[[NSURL fileURLWithPath:@"/tmp/Röbot 测试.PBB"], [NSURL fileURLWithPath:@"/tmp/Second.pbb"]];
            // NSURL exposes the filesystem's decomposed Unicode spelling on
            // macOS. Compare canonical text while preserving the exact separator.
            Require([Paths(urls).precomposedStringWithCanonicalMapping isEqualToString:@"/tmp/Röbot 测试.PBB\034/tmp/Second.pbb"],
                "Unicode/multiple paths changed their SFB wire format.");
            [heartbeat invalidate]; [owner orderOut:nil];
            std::printf("Native file-dialog validation passed: async return %.3fms, %d ordinary run-loop ticks, cached open/save panels, main-thread callbacks, cancellation, filters, folders, Unicode, overlap isolation and domain-reload cleanup.\n", returnedMs, beats);
            return 0;
        }
        catch (const std::exception &error) {
            if (activePanel) [activePanel cancel:nil];
            std::fprintf(stderr, "Native file-dialog validation failed: %s\n", error.what());
            return 1;
        }
    }
}
