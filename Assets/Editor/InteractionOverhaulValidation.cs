using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Protobot;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Tools;
using Protobot.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using InputEvent = Protobot.InputEvents.InputEvent;

namespace ProtobotEditorTools {
    /// <summary>Visible Main UI contracts: queue device events, dispatch through the production module, and render.</summary>
    public static class InteractionOverhaulValidation {
        const string Key = "Protobot.InteractionOverhaulSmoke";
        static int ticks;
        static bool Diagnose => Environment.GetCommandLineArgs().Contains("-overhaulDiagnose");
        static bool Render => Environment.GetCommandLineArgs().Contains("-overhaulRenderProbe");
        static readonly List<string> failures = new();
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod] static void Resume() {
            if (SessionState.GetBool(Key, false)) { ticks = 0; EditorApplication.update += Tick; }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] static void Devices() {
            if (!SessionState.GetBool(Key, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++ticks < 20) return;
            EditorApplication.update -= Tick; SessionState.SetBool(Key, false);
            try {
                GameObject.Find("Launch Canvas")?.SetActive(false);
                GameObject.Find("Splash Screen")?.SetActive(false);
                if (Render) {
                    var game = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
                    game.Show(); game.Focus();
                    await Frames(3);
                }
                using var window = Render ? new ProbeWindow() : null;
                if (window != null) { window.Size(1600,1000); await Frames(4); }
                await Scenario("color", CheckColor);
                await Scenario("settings", CheckSettings);
                if (window != null) {
                    window.Size(1280,720); await Frames(4);
                    await Scenario("settings small", CheckSettings);
                    window.Size(1600,1000); await Frames(4);
                }
                if (Render) await Scenario("camera dispatch", CheckCameraDispatch);
                await Scenario("box", CheckBox);
                Require(failures.Count == 0, string.Join("\n", failures));
                Debug.Log("Interaction overhaul dispatch/render smoke passed: all RGB controls, ancestor click isolation, off-panel release ownership, real settings wheel targets, focus, compact layout, scaled/reversed selection rectangles.");
                EditorApplication.Exit(0);
            } catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
        sealed class ProbeWindow : IDisposable {
            readonly EditorWindow game;
            readonly object group;
            readonly PropertyInfo selected;
            readonly int previous;
            readonly List<int> added = new();
            public ProbeWindow() {
                var assembly = typeof(EditorWindow).Assembly;
                game = EditorWindow.GetWindow(assembly.GetType("UnityEditor.GameView"));
                selected = game.GetType().GetProperty("selectedSizeIndex", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                previous = (int)selected.GetValue(game);
                var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
                var sizes = sizesType.BaseType.GetProperty("instance", BindingFlags.Public|BindingFlags.Static).GetValue(null);
                var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType");
                group = sizesType.GetMethod("GetGroup").Invoke(sizes,new[] {Enum.Parse(groupType,"Standalone")});
            }
            public void Size(int width,int height) {
                var assembly = typeof(EditorWindow).Assembly;
                var type = assembly.GetType("UnityEditor.GameViewSize");
                var kind = assembly.GetType("UnityEditor.GameViewSizeType");
                var size = Activator.CreateInstance(type, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
                    null,new[] {Enum.Parse(kind,"FixedResolution"),(object)width,height,"Overhaul validation"},null);
                int count = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group,null);
                int builtins = (int)group.GetType().GetMethod("GetBuiltinCount").Invoke(group,null);
                group.GetType().GetMethod("AddCustomSize").Invoke(group,new[] {size}); added.Add(count-builtins);
                selected.SetValue(game,count); game.Focus(); game.Repaint();
            }
            public void Dispose() {
                selected.SetValue(game,previous);
                foreach (int index in added.AsEnumerable().Reverse()) group.GetType().GetMethod("RemoveCustomSize").Invoke(group,new object[] {index});
            }
        }
        static async Task Scenario(string name, Func<Task> check) {
            try { await check(); }
            catch (Exception e) {
                if (!Diagnose) throw;
                failures.Add(name + ": " + e.Message); Debug.Log("REPRO " + name + ": " + e);
            }
        }
        internal static async Task Frames(int count = 2) {
            for (int i = 0; i < count; i++) {
                int frame = Time.frameCount;
                double end = EditorApplication.timeSinceStartup + 20;
                while (Time.frameCount == frame) {
                    Require(EditorApplication.timeSinceStartup < end, "Play Mode stopped advancing.");
                    EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(10);
                }
            }
        }
        internal static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        internal static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        internal static Vector2 Point(RectTransform rect, Vector2 normalized) {
            var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
            return RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                rect.TransformPoint(rect.rect.min + Vector2.Scale(rect.rect.size, normalized)));
        }
        internal static GameObject Hit(Vector2 point) {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.FirstOrDefault().gameObject;
        }
        internal static void Pointer(Vector2 point, bool down = false, Vector2 scroll = default) {
            InputSystem.QueueStateEvent(Mouse.current, new MouseState { position = point, scroll = scroll }.WithButton(MouseButton.Left, down));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null)
                .Invoke(null, new object[] { InputUpdateType.Dynamic });
            // Exercise the early polling order too: tools may poll before EventSystem.Update.
            foreach (var input in Object.FindObjectsByType<InputEvent>(FindObjectsSortMode.None)) input.Update();
            Require(EventSystem.current.currentInputModule is ToolUIInputModule, "Main is not using ToolUIInputModule.");
            EventSystem.current.currentInputModule.Process();
        }
        internal static void Keys(params UnityEngine.InputSystem.Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(keys));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic|BindingFlags.Static,null,new[] {typeof(InputUpdateType)},null)
                .Invoke(null,new object[] {InputUpdateType.Dynamic});
            EventSystem.current.currentInputModule.Process();
        }
        internal static async Task Click(RectTransform rect, Vector2? normalized = null) {
            Vector2 point = Point(rect, normalized ?? Vector2.one * .5f);
            Pointer(point); await Frames();
            var hit = Hit(point);
            Require(hit != null && (hit.transform == rect || hit.transform.IsChildOf(rect)), rect.name + " is covered by " + hit?.name + " at " + point);
            Pointer(point, true); await Frames(); Pointer(point); await Frames();
        }
        static async Task Capture(string name) {
            if (!Render) return;
            string path = "/tmp/protobot-overhaul-" + (Diagnose ? "before-" : "after-") + name + ".png";
            File.Delete(path); Canvas.ForceUpdateCanvases(); ScreenCapture.CaptureScreenshot(path);
            for (int i = 0; i < 60 && !File.Exists(path); i++) await Frames(1);
            Require(File.Exists(path), "Screenshot did not complete: " + path);
            Debug.Log("Rendered probe: " + path + " (" + Screen.width + "x" + Screen.height + ")");
        }
        static async Task CheckColor() {
            var tool = Object.FindFirstObjectByType<ColorTool>(FindObjectsInactive.Include);
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var part = PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(20,20,20), Quaternion.identity);
            manager.SetCurrent(new ObjectSelection { gameObject = part, individual = true }, true, true);
            var dropdown = tool.GetComponentInChildren<Dropdown>(true);
            var toggle = tool.GetComponent<Toggle>();
            var panel = dropdown.transform.parent.gameObject;
            if (!toggle.isOn) await Click((RectTransform)toggle.transform);
            dropdown.value = dropdown.options.Count - 1;
            await Frames();
            await Capture("color");
            await CheckColorControls(tool, dropdown, panel);
        }
        internal static async Task CheckColorControls(ColorTool tool, Dropdown dropdown, GameObject panel) {
            var custom = Get<GameObject>(tool, "custom");
            var manager = GameObject.Find("Primary Selection Manager").GetComponent<SelectionManager>();
            var before = manager.current;
            var box = Object.FindFirstObjectByType<BoxSelectUI>();
            var toggle = tool.GetComponent<Toggle>();
            foreach (var slider in custom.GetComponentsInChildren<Slider>(true)) {
                float previous = slider.value;
                await Click((RectTransform)slider.transform, new Vector2(.25f, .5f));
                Require(panel.activeInHierarchy && custom.activeInHierarchy && toggle.isOn,
                    slider.name + " click reached the ancestor toolbar toggle and closed the color UI.");
                Require(slider.value != previous && ColorTool.CustomColor, slider.name + " did not update the brush.");
                var point = Point(slider.handleRect != null ? slider.handleRect : (RectTransform)slider.transform, Vector2.one*.5f);
                Pointer(point, true); await Frames();
                // Same-panel drag used to click the ancestor Toggle on release too.
                Pointer(Point((RectTransform)slider.transform, new Vector2(.75f, .5f)), true); await Frames();
                Pointer(Point((RectTransform)slider.transform, new Vector2(.75f, .5f))); await Frames();
                Require(panel.activeInHierarchy && toggle.isOn, slider.name + " drag closed the UI.");
                point = Point(slider.handleRect != null ? slider.handleRect : (RectTransform)slider.transform,Vector2.one*.5f);
                Pointer(point, true); await Frames();
                var outside = new Vector2(Screen.width * .8f, Screen.height * .35f);
                Pointer(outside, true); await Frames();
                Require(MouseInput.overUI && manager.current == before && !Get<bool>(box, "selecting"), slider.name + " drag escaped to scene selection.");
                Pointer(outside);
                Require(MouseInput.overUI, slider.name + " release was not owned in the dispatch frame.");
                await Frames();
                Require(panel.activeInHierarchy && toggle.isOn && manager.current == before, slider.name + " outside release changed tool/selection.");
            }
            // Blank panel areas must consume clicks too, without toggling the owning toolbar.
            await Click((RectTransform)custom.transform, new Vector2(.96f, .96f));
            Require(panel.activeInHierarchy && toggle.isOn, "Color panel background toggled its ancestor toolbar.");
            await ChoosePalette(dropdown,1);
            Require(!ColorTool.UseDefaultColor && !ColorTool.CustomColor && panel.activeInHierarchy && toggle.isOn,"Palette choice changed the owning color tool.");
            await ChoosePalette(dropdown,dropdown.options.Count-1);
            Require(custom.activeInHierarchy && toggle.isOn,"Custom palette did not reopen its controls.");
            await ChoosePalette(dropdown,0);
            Require(ColorTool.UseDefaultColor && !custom.activeSelf, "Default did not close the custom controls deliberately.");
            if (toggle.isOn) await Click((RectTransform)toggle.transform);
            Require(!panel.activeSelf, "Explicit toolbar close did not close the color panel.");
        }
        static async Task ChoosePalette(Dropdown dropdown,int index) {
            await Click((RectTransform)dropdown.transform);
            var list = dropdown.transform.Find("Dropdown List");
            Require(list != null,"Palette dropdown did not open through pointer dispatch.");
            var item = list.GetComponentsInChildren<Toggle>().Single(t => t.name.StartsWith("Item " + index + ":"));
            var scroll = list.GetComponentInChildren<ScrollRect>();
            if (scroll != null) {
                for (int i=0;i<50;i++) {
                    var point = Point((RectTransform)item.transform,Vector2.one*.5f);
                    if (ScreenBounds(scroll.viewport).Contains(point)) break;
                    Pointer(Point(scroll.viewport,Vector2.one*.5f),scroll:new Vector2(0,-1200)); await Frames();
                }
            }
            await Click((RectTransform)item.transform);
            for (int i=0;i<100 && dropdown.transform.Find("Dropdown List")!=null;i++) await Frames(1);
            Require(dropdown.value==index && dropdown.transform.Find("Dropdown List")==null,"Palette selection did not apply/close its popup.");
        }
        static async Task CheckSettings() {
            var fields = Object.FindObjectsByType<NumericPreferenceField>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var scroller = fields[0].GetComponentInParent<PreferencesCategoryScroller>(true);
            var category = (RectTransform)fields[0].transform.parent.parent.parent;
            foreach (var t in scroller.GetComponentsInParent<Transform>(true)) t.gameObject.SetActive(true);
            foreach (Transform t in scroller.transform) t.gameObject.SetActive(t == category);
            EventSystem.current.SetSelectedGameObject(null);
            await Frames(3); Canvas.ForceUpdateCanvases();
            foreach (Transform t in category) Debug.Log("Camera layout " + t.name + ": " + RectTransformUtility.CalculateRelativeRectTransformBounds(category, t));
            var cameraToggle = scroller.transform.parent.Find("Category Selection/Camera Toggle") as RectTransform;
            if (cameraToggle != null) await Click(cameraToggle);
            await Capture("camera-" + Screen.width + "x" + Screen.height);
            var left = category.Find("Left Column");
            var right = category.Find("Right Column");
            var rows = category.GetComponentsInChildren<RebindUI>().Select(r => (RectTransform)r.transform).ToArray();
            float bottom = rows.Min(r => RectTransformUtility.CalculateRelativeRectTransformBounds(category, r).min.y);
            float top = RectTransformUtility.CalculateRelativeRectTransformBounds(category, left).max.y;
            float gap = bottom - top;
            Debug.Log("Camera section gap: " + gap);
            if (!Diagnose) Require(gap >= 0 && gap <= 30, "Camera sections have an excessive gap/overlap: " + gap);
            await CheckSettingsScroll(scroller, category, fields[0]);
            if (Diagnose) Require(gap >= 0 && gap <= 30, "Camera sections have an excessive gap/overlap: " + gap);
            scroller.transform.root.gameObject.SetActive(false);
        }
        internal static async Task CheckSettingsScroll(PreferencesCategoryScroller scroller, RectTransform category, NumericPreferenceField field) {
            var viewport = (RectTransform)scroller.transform;
            var prefs = (RectTransform)viewport.parent;
            await Frames(3);
            float height = prefs.rect.height;
            var input = field.GetComponent<InputField>();
            string saved = LocalPreferences.GetString(field.PreferenceKey);
            bool had = LocalPreferences.HasKey(field.PreferenceKey);
            try {
                prefs.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 250f);
                EventSystem.current.SetSelectedGameObject(null); await Frames(3);
                var origin = category.anchoredPosition;
                float zoom = PivotCamera.Main.focusDistance;
                var points = new List<Vector2> { new(.98f,.5f), new(.02f,.02f), new(.5f,.98f) };
                foreach (var position in points) {
                    Vector2 point = Point(viewport, position);
                    Debug.Log("Scroll hit " + position + ": " + Hit(point)?.name);
                    Pointer(point, scroll: new Vector2(0,-120)); await Frames();
                    Require(category.anchoredPosition.y > origin.y + .1f, "Wheel did not reach Preferences across blank viewport at " + position);
                    Require(Mathf.Approximately(zoom, PivotCamera.Main.focusDistance), "Settings scroll zoomed the scene camera.");
                    Pointer(point, scroll: new Vector2(0,12000)); await Frames();
                }
                // Labels, rebind boxes and reset buttons must bubble wheel events
                // to the same parent, with their own pointer actions preserved.
                var rebind = category.GetComponentsInChildren<RebindUI>().First();
                foreach (var graphic in rebind.GetComponentsInChildren<Graphic>().Where(g => g.raycastTarget)) {
                    var point = Point(graphic.rectTransform, Vector2.one * .5f);
                    if (!ScreenBounds(viewport).Contains(point)) continue;
                    Pointer(point, scroll: new Vector2(0,-120)); await Frames();
                    Require(category.anchoredPosition.y > origin.y + .1f, "Scroll did not bubble from " + graphic.name);
                    Pointer(Point(viewport,new Vector2(.98f,.5f)), scroll: new Vector2(0,12000)); await Frames();
                }
                // Keyboard focus reveals the actual numeric row. Scrolling must
                // then keep the caret/focus instead of snapping back each frame.
                EventSystem.current.SetSelectedGameObject(field.gameObject); await Frames();
                Require(ScreenBounds(viewport).Contains(Point((RectTransform)field.transform, Vector2.one*.5f)), "Focused camera field was not revealed.");
                await Click((RectTransform)field.transform);
                Require(input.isFocused, "Numeric field did not acquire focus from a real pointer click.");
                var tools = Object.FindObjectsByType<ToolToggle>(FindObjectsSortMode.None).Select(t => (t,t.toggled)).ToArray();
                foreach (var key in new[] {UnityEngine.InputSystem.Key.Digit1,UnityEngine.InputSystem.Key.Digit2,UnityEngine.InputSystem.Key.G,UnityEngine.InputSystem.Key.Delete,UnityEngine.InputSystem.Key.LeftShift}) {
                    Keys(key); await Frames(); Keys(); await Frames();
                    Require(tools.All(t => t.t.toggled == t.toggled), "Typing " + key + " in a settings field activated a scene tool.");
                    Require(!InputEvent.IsNoclipMovementCaptured && input.isFocused,"Settings text editing yielded camera/keyboard focus.");
                }
                int caret = input.caretPosition;
                Pointer(Point(viewport,new Vector2(.98f,.5f)), scroll: new Vector2(0,120)); await Frames(3);
                Require(input.isFocused && EventSystem.current.currentSelectedGameObject == input.gameObject && input.caretPosition == caret,
                    "Wheel stole numeric field focus/caret.");
                float offset = category.anchoredPosition.y;
                await Frames(3);
                Require(Mathf.Approximately(category.anchoredPosition.y, offset), "Focused numeric row undid wheel scrolling.");
                // Change focus away/back to reveal it again, then click the real
                // reset button. Text entry itself uses legacy uGUI's OS event queue.
                EventSystem.current.SetSelectedGameObject(null); await Frames();
                EventSystem.current.SetSelectedGameObject(field.gameObject); await Frames();
                input.text = "0.375";
                var reset = (RectTransform)field.transform.parent.Find("Set Default Button");
                // Clicking the background commits the field before resetting it.
                await Click(viewport,new Vector2(.98f,.5f));
                Require(field.Current == .375f, "Real field deselection did not commit numeric text.");
                EventSystem.current.SetSelectedGameObject(field.gameObject); await Frames();
                await Click(reset);
                Require(field.IsDefault && LocalPreferences.GetFloat(field.PreferenceKey) == field.DefaultValue, "Real reset button did not persist the default.");
                Require(Mathf.Approximately(zoom,PivotCamera.Main.focusDistance), "Settings child interactions zoomed the camera.");
            }
            finally {
                EventSystem.current.SetSelectedGameObject(null);
                prefs.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                if (had) LocalPreferences.SetString(field.PreferenceKey,saved); else LocalPreferences.DeleteKey(field.PreferenceKey);
                field.Initialize();
            }
        }
        static async Task CheckCameraDispatch() {
            foreach (var scroller in Object.FindObjectsByType<PreferencesCategoryScroller>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                scroller.transform.root.gameObject.SetActive(false);
            EventSystem.current.SetSelectedGameObject(null);
            Keys(); await Frames();
            var camera = PivotCamera.Main;
            camera.SetTransform(Vector3.zero,new Vector3(15,25,0),-10);
            Vector2 point = new Vector2(Screen.width*.55f,Screen.height*.55f);
            Pointer(point); await Frames();
            Require(!MouseInput.overUI && Application.isFocused,"Camera dispatch probe needs a focused unobstructed Game View.");
            Quaternion rotation = camera.transform.rotation;
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,delta=new Vector2(10,5)}.WithButton(MouseButton.Right));
            await Frames();
            Require(Quaternion.Angle(rotation,camera.transform.rotation) > .1f,"Queued orbit did not move the camera.");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point}); await Frames();
            Vector3 position = camera.transform.position;
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,delta=new Vector2(10,5)}.WithButton(MouseButton.Middle));
            await Frames();
            Require(Vector3.Distance(position,camera.transform.position) > .01f,"Queued pan did not move the camera.");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point}); await Frames();
            float distance = -camera.focusDistance;
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,scroll=new Vector2(0,120)}); await Frames();
            float tick = -camera.focusDistance;
            Require(tick < distance,"Queued wheel did not zoom in.");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,scroll=new Vector2(0,-120)}); await Frames();
            Require(Mathf.Abs(-camera.focusDistance-distance) < .0001f,"Opposite queued zoom did not round-trip.");
            for (int i=0;i<2;i++) {
                InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,scroll=new Vector2(0,60)}); await Frames();
            }
            Require(Mathf.Abs(-camera.focusDistance-tick) < .0001f,"Trackpad frame grouping changed queued zoom.");
            Keys(UnityEngine.InputSystem.Key.G); await Frames(); Keys(); await Frames();
            Require(KeyboardTransformTool.IsCapturingInput,"Queued G did not start the transform preview.");
            position = camera.transform.position;
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point,delta=new Vector2(10,5)}.WithButton(MouseButton.Middle)); await Frames();
            Require(KeyboardTransformTool.IsCapturingInput && Vector3.Distance(position,camera.transform.position)>.01f,"Grab preview blocked pausable camera pan.");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point}); await Frames();
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point}.WithButton(MouseButton.Right)); await Frames();
            Require(!KeyboardTransformTool.IsCapturingInput,"Right-click camera binding stole grab cancellation.");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState {position=point}); await Frames();
            position = camera.transform.position;
            Keys(UnityEngine.InputSystem.Key.LeftShift,UnityEngine.InputSystem.Key.W); await Frames();
            Require(InputEvent.IsNoclipMovementCaptured && Vector3.Distance(position,camera.transform.position)>.01f,"Queued free-fly activation/movement did not respond.");
            Keys(); await Frames();
            Require(!InputEvent.IsNoclipMovementCaptured,"Free-fly release retained input capture.");
        }
        public static void Validate() {
            CameraNavigationValidation.Validate();
            WindowsPlayerConfigurationValidation.Validate();
            ShortcutPriorityValidation.ValidateRelated();
            CollaborationSetup.ExportCatalog();
        }
        static async Task CheckBox() {
            foreach (var scroller in Object.FindObjectsByType<PreferencesCategoryScroller>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                scroller.transform.root.gameObject.SetActive(false);
            var box = Object.FindFirstObjectByType<BoxSelectUI>(); var image = box.GetComponent<Image>();
            var canvas = image.canvas.rootCanvas;
            var scaler = canvas.GetComponent<CanvasScaler>();
            bool enabled = scaler != null && scaler.enabled;
            if (scaler != null) scaler.enabled = false;
            var boxInput = Get<InputEvent>(box,"input");
            string binding = LocalPreferences.GetString(boxInput.name);
            bool hadBinding = LocalPreferences.HasKey(boxInput.name);
            boxInput.rebindAction.action.ApplyBindingOverride(3,"<Mouse>/leftButton");
            boxInput.rebindAction.SaveRebinds();
            string rebound = LocalPreferences.GetString(boxInput.name);
            int i = 0;
            foreach (float scale in new[] {1f, 1.5f, .75f}) {
                canvas.scaleFactor = scale;
                foreach (var size in new[] {new Vector2(3,3), new Vector2(500,20), new Vector2(20,300), new Vector2(Screen.width-100,Screen.height-150)}) {
                    Vector2 start = new Vector2(60,60), end = start + size;
                    int direction = i++ % 4;
                    Vector2 dragStart = direction switch { 0=>end, 1=>start, 2=>new Vector2(end.x,start.y), _=>new Vector2(start.x,end.y) };
                    Vector2 dragEnd = direction switch { 0=>start, 1=>end, 2=>new Vector2(start.x,end.y), _=>new Vector2(end.x,start.y) };
                    Pointer(dragStart); await Frames();
                    Pointer(dragStart,true); await Frames();
                    // A rectangle may start beneath other scene UI in the large
                    // render case. Choose the clear endpoint as the press origin.
                    if (!Get<bool>(box,"selecting")) {
                        Pointer(dragStart); await Frames();
                        (dragStart,dragEnd) = (dragEnd,dragStart);
                        Pointer(dragStart); await Frames(); Pointer(dragStart,true); await Frames();
                    }
                    Require(Get<bool>(box,"selecting"),"Queued scene pointer-down did not begin a box.");
                    Pointer(dragEnd,true);
                    await Frames(); Canvas.ForceUpdateCanvases();
                    var bounds = ScreenBounds((RectTransform)image.transform);
                    Debug.Log("Box scale " + scale + " requested " + size + " rendered " + bounds.size + "; border " + image.sprite.border / (image.pixelsPerUnit * image.pixelsPerUnitMultiplier));
                    await Capture("box-" + scale + "-" + i);
                    if (!Diagnose) {
                        Require(Vector2.Distance(bounds.size, size) < 2f, "Box screen bounds differ from drag at UI scale " + scale);
                        Require(image.sprite.border.x / (image.pixelsPerUnit * image.pixelsPerUnitMultiplier) <= 2f, "Box border is excessively thick.");
                    }
                    Require(LocalPreferences.GetString(boxInput.name) == rebound, "Box dragging erased the saved selection binding.");
                    int releases = 0;
                    Action<Vector2,Vector2> released = (_,_) => releases++;
                    box.OnReleaseBox += released;
                    bool expectRelease = Vector2.Distance(dragStart,dragEnd)>5 && Hit(dragEnd)==null;
                    Pointer(dragEnd); await Frames();
                    box.OnReleaseBox -= released;
                    Require(!Get<bool>(box,"selecting") && releases == (expectRelease ? 1 : 0),
                        "Box release was consumed by its own visual or crossed the UI boundary.");
                }
            }
            Vector2 cancelStart = new Vector2(Screen.width*.4f,Screen.height*.4f);
            Pointer(cancelStart); await Frames(); Pointer(cancelStart,true); await Frames();
            Require(Get<bool>(box,"selecting"),"Box did not start for queued Escape cancellation.");
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(UnityEngine.InputSystem.Key.Escape)); await Frames();
            Require(!Get<bool>(box,"selecting"),"Queued Escape did not cancel box selection.");
            int canceledReleases=0;
            Action<Vector2,Vector2> cancelRelease=(_,_)=>canceledReleases++;
            box.OnReleaseBox+=cancelRelease;
            Pointer(cancelStart+Vector2.one*100); await Frames(); Keys(); await Frames();
            box.OnReleaseBox-=cancelRelease;
            Require(canceledReleases==0,"Canceled box resolved on physical pointer release.");
            if (hadBinding) LocalPreferences.SetString(boxInput.name,binding); else LocalPreferences.DeleteKey(boxInput.name);
            boxInput.rebindAction.action.RemoveAllBindingOverrides();
            if (hadBinding) boxInput.rebindAction.LoadRebinds(); else boxInput.defaultAction.Enable();
            if (scaler != null) scaler.enabled = enabled;
        }
        static Rect ScreenBounds(RectTransform rect) {
            Vector2 a = Point(rect, Vector2.zero), b = Point(rect, Vector2.one);
            return Rect.MinMaxRect(a.x,a.y,b.x,b.y);
        }
    }
}
