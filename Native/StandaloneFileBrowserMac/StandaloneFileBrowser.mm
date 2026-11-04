#import <Cocoa/Cocoa.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <atomic>

// Preserve the UnityStandaloneFileBrowser C ABI. All AppKit work stays on the
// main thread; asynchronous entry points only copy arguments and queue work.
using Callback = void (*)(const char *);
static NSOpenPanel *openPanel;
static NSSavePanel *savePanel;
static NSSavePanel *activePanel;
static NSString *syncResult;
static std::atomic<uint64_t> callbackGeneration;

static NSString *String(const char *value) {
    return value ? [NSString stringWithUTF8String:value] ?: @"" : @"";
}

static void SetTypes(NSSavePanel *panel, NSArray<UTType *> *types) {
    // Unlike the deprecated allowedFileTypes API, allowedContentTypes uses an
    // empty array for unrestricted browsing and rejects nil.
    if (types.count == 0) types = @[];
    if (panel.allowedContentTypes != types && ![panel.allowedContentTypes isEqual:types])
        panel.allowedContentTypes = types;
}

@interface PBBFileTypes : NSObject
@property(nonatomic, weak) NSSavePanel *panel;
@property(nonatomic, copy) NSString *filter;
@property(nonatomic, copy) NSArray<NSArray<UTType *> *> *types;
@property(nonatomic, strong) NSPopUpButton *popup;
- (void)configure:(NSString *)filter panel:(NSSavePanel *)panel;
- (void)changed:(NSPopUpButton *)sender;
@end

@implementation PBBFileTypes
- (void)configure:(NSString *)filter panel:(NSSavePanel *)panel {
    self.panel = panel;
    if (![self.filter isEqualToString:filter]) {
        self.filter = filter;
        NSMutableArray *groups = [NSMutableArray array];
        NSMutableArray *names = [NSMutableArray array];
        for (NSString *group in [filter componentsSeparatedByString:@"|"]) {
            NSArray *fields = [group componentsSeparatedByString:@";"];
            if (fields.count != 2) continue;
            NSMutableArray *types = [NSMutableArray array];
            for (NSString *extension in [fields[1] componentsSeparatedByString:@","]) {
                if ([extension isEqualToString:@"*"]) { [types removeAllObjects]; break; }
                if (extension.length == 0) continue;
                UTType *type = [UTType typeWithFilenameExtension:extension conformingToType:UTTypeData];
                if (type) [types addObject:type];
            }
            [groups addObject:types];
            [names addObject:[NSString stringWithFormat:@"%@ (%@)", [fields[0] length] ? fields[0] : @"Files", fields[1]]];
        }
        self.types = groups;
        panel.accessoryView = nil;
        self.popup = nil;
        // A single extension group already has a native filter. Avoid creating
        // and laying out the old accessory dropdown for every ordinary request.
        if (names.count > 1) {
            self.popup = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(0, 0, 280, 26) pullsDown:NO];
            [self.popup addItemsWithTitles:names];
            self.popup.target = self;
            self.popup.action = @selector(changed:);
            panel.accessoryView = self.popup;
        }
    }
    [self.popup selectItemAtIndex:0];
    SetTypes(panel, self.types.firstObject);
}
- (void)changed:(NSPopUpButton *)sender {
    NSInteger index = sender.indexOfSelectedItem;
    if (index < 0 || index >= self.types.count) return;
    NSArray<UTType *> *types = self.types[index];
    SetTypes(self.panel, types);
    if (![self.panel isKindOfClass:NSOpenPanel.class] && types.firstObject.preferredFilenameExtension.length) {
        NSString *name = self.panel.nameFieldStringValue.stringByDeletingPathExtension;
        self.panel.nameFieldStringValue = [name stringByAppendingPathExtension:types.firstObject.preferredFilenameExtension];
    }
}
@end

static PBBFileTypes *openTypes;
static PBBFileTypes *saveTypes;

static void PreparePanels() {
    if (!openPanel) {
        openPanel = [NSOpenPanel openPanel];
        openTypes = [PBBFileTypes new];
    }
    if (!savePanel) {
        savePanel = [NSSavePanel savePanel];
        saveTypes = [PBBFileTypes new];
    }
}

static NSSavePanel *Configure(bool save, bool folders, NSString *title, NSString *directory,
                              NSString *name, NSString *filter, bool multiple) {
    PreparePanels();
    NSSavePanel *panel = save ? savePanel : openPanel;
    panel.message = title;
    panel.canCreateDirectories = YES;
    // Do not turn an empty directory into the process working directory, or
    // reload an unchanged directory (which may live on a cloud/network volume).
    if (directory.length && ![panel.directoryURL.path isEqualToString:directory])
        panel.directoryURL = [NSURL fileURLWithPath:directory isDirectory:YES];
    [(save ? saveTypes : openTypes) configure:filter panel:panel];
    if (save) panel.nameFieldStringValue = name;
    else {
        openPanel.canChooseFiles = !folders;
        openPanel.canChooseDirectories = folders;
        openPanel.allowsMultipleSelection = multiple;
    }
    return panel;
}

static NSString *Paths(NSArray<NSURL *> *urls) {
    NSMutableArray *paths = [NSMutableArray arrayWithCapacity:urls.count];
    for (NSURL *url in urls) if (url.isFileURL && url.path) [paths addObject:url.path];
    return [paths componentsJoinedByString:[NSString stringWithFormat:@"%c", 28]];
}

static NSString *Result(NSSavePanel *panel, NSModalResponse response) {
    if (response != NSModalResponseOK) return @"";
    if ([panel isKindOfClass:NSOpenPanel.class]) return Paths(((NSOpenPanel *)panel).URLs);
    return panel.URL.path ?: @"";
}

static void ShowAsync(bool save, bool folders, const char *title, const char *directory,
                      const char *name, const char *filter, bool multiple, Callback callback) {
    // Marshalled C# strings expire when P/Invoke returns. Copy them before the
    // dispatch, and capture the callback per request rather than in a global slot.
    NSString *titleValue = String(title), *directoryValue = String(directory);
    NSString *nameValue = String(name), *filterValue = String(filter);
    uint64_t generation = callbackGeneration.load();
    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool {
            if (generation != callbackGeneration.load()) return;
            if (activePanel) { if (callback) callback(""); return; }
            @try {
                NSSavePanel *panel = Configure(save, folders, titleValue, directoryValue, nameValue, filterValue, multiple);
                activePanel = panel;
                void (^complete)(NSModalResponse) = ^(NSModalResponse response) {
                    NSString *result = Result(panel, response);
                    [panel orderOut:nil];
                    activePanel = nil;
                    if (callback && generation == callbackGeneration.load()) callback(result.UTF8String);
                };
                NSWindow *owner = NSApp.keyWindow ?: NSApp.mainWindow;
                if (owner && owner != panel && !owner.attachedSheet)
                    [panel beginSheetModalForWindow:owner completionHandler:complete];
                else [panel beginWithCompletionHandler:complete];
            }
            @catch (NSException *exception) {
                NSLog(@"Protobot file dialog failed: %@", exception.reason);
                [activePanel orderOut:nil];
                activePanel = nil;
                if (callback && generation == callbackGeneration.load()) callback("");
            }
        }
    });
}

static const char *ShowSync(bool save, bool folders, const char *title, const char *directory,
                            const char *name, const char *filter, bool multiple) {
    NSString *titleValue = String(title), *directoryValue = String(directory);
    NSString *nameValue = String(name), *filterValue = String(filter);
    __block NSString *result = @"";
    void (^show)() = ^{
        if (activePanel) return;
        @try {
            NSSavePanel *panel = Configure(save, folders, titleValue, directoryValue, nameValue, filterValue, multiple);
            activePanel = panel;
            result = Result(panel, [panel runModal]);
            [panel orderOut:nil];
            activePanel = nil;
        }
        @catch (NSException *exception) {
            NSLog(@"Protobot file dialog failed: %@", exception.reason);
            [activePanel orderOut:nil]; activePanel = nil;
        }
    };
    if (NSThread.isMainThread) show();
    else dispatch_sync(dispatch_get_main_queue(), show);
    syncResult = result;
    return syncResult.UTF8String;
}

extern "C" {
void DialogReset() {
    // Unity can unload the managed callback's domain while a sheet is open.
    // Invalidate both queued and visible requests before closing the panel.
    callbackGeneration.fetch_add(1);
    void (^cancel)() = ^{ [activePanel cancel:nil]; [activePanel orderOut:nil]; };
    if (NSThread.isMainThread) cancel();
    else dispatch_sync(dispatch_get_main_queue(), cancel);
}
void DialogWarmup() {
    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool {
            if (activePanel) return;
            @try { PreparePanels(); }
            @catch (NSException *exception) { NSLog(@"Protobot file dialog preparation failed: %@", exception.reason); }
        }
    });
}
void DialogOpenFilePanelAsync(const char *title, const char *directory, const char *filters, bool multiple, Callback callback) {
    ShowAsync(false, false, title, directory, "", filters, multiple, callback);
}
void DialogOpenFolderPanelAsync(const char *title, const char *directory, bool multiple, Callback callback) {
    ShowAsync(false, true, title, directory, "", "", multiple, callback);
}
void DialogSaveFilePanelAsync(const char *title, const char *directory, const char *name, const char *filters, Callback callback) {
    ShowAsync(true, false, title, directory, name, filters, false, callback);
}
const char *DialogOpenFilePanel(const char *title, const char *directory, const char *filters, bool multiple) {
    return ShowSync(false, false, title, directory, "", filters, multiple);
}
const char *DialogOpenFolderPanel(const char *title, const char *directory, bool multiple) {
    return ShowSync(false, true, title, directory, "", "", multiple);
}
const char *DialogSaveFilePanel(const char *title, const char *directory, const char *name, const char *filters) {
    return ShowSync(true, false, title, directory, name, filters, false);
}
}
