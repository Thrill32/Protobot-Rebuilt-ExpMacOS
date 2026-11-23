using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DG.Tweening;
using Protobot;
using Protobot.Builds;
using Protobot.InputEvents;
using Protobot.SelectionSystem;
using Protobot.Simulation;
using Protobot.StateSystems;
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

namespace ProtobotEditorTools {
    public static class EscapeDeselectValidation {
        private const string SmokeKey = "Protobot.EscapeDeselectSmoke";
        private static int frames;
        public static void Smoke() {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            SessionState.SetBool(SmokeKey, true); EditorApplication.EnterPlaymode();
        }
        [InitializeOnLoadMethod]
        private static void Resume() { if (SessionState.GetBool(SmokeKey, false)) { frames = 0; EditorApplication.update += Tick; } }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Devices() {
            if (!SessionState.GetBool(SmokeKey, false)) return;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
        }
        private static async void Tick() {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling || ++frames < 15) return;
            EditorApplication.update -= Tick; SessionState.SetBool(SmokeKey, false);
            try {
                await Check();
                Debug.Log("Escape deselect validation passed: one tier per press, held-key and input-order capture, G/R and numeric cancellation, catalog/P/transfer/Move previews, gizmo drag/Alt-copy rollback without undo debris, box selection, Connect source/motion, simulation, UI/text/rebinding priority, selection scope and toolbar reset.");
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        private static async Task Check() {
            GameObject.Find("Splash Screen")?.SetActive(false);
            var placement = Object.FindFirstObjectByType<Placement>();
            Require(placement.GetComponent<EscapeDeselect>() != null, "Main did not install Escape handling.");
            var keyboard = placement.GetComponent<KeyboardTransformTool>();
            var selection = Get<SelectionManager>(keyboard, "selectionManager");
            var transformButton = GameObject.Find("Transform Tool").GetComponent<Toggle>();
            var movement = Object.FindFirstObjectByType<MovementManager>();
            var body = PartsManager.GeneratePart("CCHL-1x2-5", new Vector3(250, 250, 250), Quaternion.Euler(13, 29, 7));
            var other = PartsManager.GeneratePart("PLTE-3-3", body.transform.position + Vector3.right * 10, Quaternion.identity);
            var start = new Pose(body.transform.position, body.transform.rotation);
            EventSystem.current.SetSelectedGameObject(null);
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true);
            transformButton.isOn = true;
            await NextFrame();

            // A grab, including exact numeric entry, unwinds before selection/tool.
            int state = StateSystem.curIndex;
            Invoke(keyboard, "BeginSelection", false);
            body.transform.position += Vector3.right * 5;
            await Escape(() => {
                Near(body.transform.position, start.position, "Grab did not restore its starting position.");
                Require(selection.current?.gameObject == body && transformButton.isOn && StateSystem.curIndex == state,
                    $"Grab cancellation advanced another tier or undo history: selection={selection.current?.gameObject}, tool={transformButton.isOn}, state={StateSystem.curIndex}/{state}.");
                Invoke(keyboard, "HandleInput", Keyboard.current, Mouse.current);
                foreach (var input in Object.FindObjectsByType<Protobot.InputEvents.InputEvent>(FindObjectsSortMode.None)) input.Update();
                Require(selection.current?.gameObject == body && transformButton.isOn, "Another Escape handler advanced a second tier.");
            });
            await Escape(() => Require(selection.current == null && transformButton.isOn, "Second press must only deselect."));
            await Escape(() => Require(!transformButton.isOn, "Third press must turn off the tool."));
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true); transformButton.isOn = true;
            await NextFrame();
            Invoke(keyboard, "BeginSelection", true);
            Get<TransformNumericInput>(keyboard, "numericInput").Cycle(false);
            body.transform.rotation = Quaternion.Euler(90, 40, 10);
            await Escape(() => { Near(body.transform.rotation, start.rotation, "Numeric rotation did not restore pose."); Require(!Get<TransformNumericInput>(keyboard, "numericInput").IsOpen && selection.current != null && transformButton.isOn, "Numeric cancellation leaked."); });

            var generator = PartsManager.GetPartType("PLTE").GetComponent<PartGenerator>();
            placement.StartPlacing(new PartPlacementData(generator, generator.GetComponent<PartType>(), placement.transform));
            int parts = PartsManager.FindLoadedObjects().Count;
            await Escape(() => Require(selection.current?.gameObject == body && transformButton.isOn, "Catalog cancel cleared selection/tool."));
            Require(!placement.placing && PartsManager.FindLoadedObjects().Count == parts, "Catalog cancellation created a part or left placement active.");
            var duplicator = Object.FindFirstObjectByType<ObjectDuplicator>();
            placement.StartPlacing(SelectionPlacementData.Capture(selection.current, placement.transform, duplicator));
            await Escape(() => Require(!placement.placing && selection.current?.gameObject == body, "P cancel did not retain source selection."));
            placement.StartPlacing(SelectionPlacementData.FromBuild(BuildTransfer.Capture(selection.current), placement.transform, duplicator, selection));
            await Escape(() => Require(!placement.placing && PartsManager.FindLoadedObjects().Count == parts, "Transfer cancellation leaked real parts."));
            placement.StartPlacing(new GameObjectPlacementData(body, placement.transform, true));
            await Escape(() => { Near(body.transform.position, start.position, "Move cancel moved the original."); Require(body.GetComponent<Renderer>().enabled && body.layer == 0 && selection.current?.gameObject == body, "Move did not restore visibility/selection."); });

            // Real gizmo pointer handlers: cancel while the mouse is still held,
            // then verify later drag/release cannot restart or commit it.
            var drag = Object.FindObjectsByType<PositionAxis>(FindObjectsInactive.Include, FindObjectsSortMode.None).First();
            var pointer = new PointerEventData(EventSystem.current);
            for (int duplicate = 0; duplicate < 2; duplicate++) {
                Keys(duplicate == 1 ? new[] { Key.LeftAlt } : Array.Empty<Key>());
                await NextFrame();
                int elements = StateSystem.curState.elements.Count; state = StateSystem.curIndex;
                drag.OnPointerDown(pointer);
                movement.MoveTo(start.position + Vector3.up * 8);
                Get<Pivot>(movement, "movementPivot").transform.DOComplete();
                Require((body.transform.position - start.position).magnitude > 1, "Gizmo fixture did not move.");
                if (duplicate == 1) Require(PartsManager.FindLoadedObjects().Count == parts + 1, "Alt-drag fixture did not create a copy.");
                await Escape(() => {
                    Near(body.transform.position, start.position, "Gizmo cancel did not restore pose.");
                    Require(StateSystem.curIndex == state && StateSystem.curState.elements.Count == elements && PartsManager.FindLoadedObjects().Count == parts,
                        "Canceled drag left undo elements or duplicate geometry.");
                    drag.OnDrag(pointer); drag.OnPointerUp(pointer);
                });
                Require(StateSystem.curIndex == state && selection.current?.gameObject == body && transformButton.isOn, "Releasing a canceled drag committed or deselected it.");
            }

            state = StateSystem.curIndex;
            drag.OnPointerDown(pointer);
            movement.MoveTo(start.position + Vector3.right * 3);
            drag.OnPointerUp(pointer);
            Get<Pivot>(movement, "movementPivot").transform.DOComplete();
            Invoke(movement, "Update");
            Require(StateSystem.curIndex == state + 1, "Normal drag confirmation stopped creating an undo step.");
            StateSystem.instance.Undo(); DOTween.CompleteAll();
            Near(body.transform.position, start.position, "Undo after canceled drags failed.");
            StateSystem.instance.Redo(); DOTween.CompleteAll();
            Near(body.transform.position, start.position + Vector3.right * 3, "Redo after canceled drags failed.");
            StateSystem.instance.Undo(); DOTween.CompleteAll();

            var multi = new MultiSelection(null, new[] { body, other }.ToList()) { individual = true };
            selection.SetCurrent(multi, true);
            await Escape(() => Require(selection.current == null && transformButton.isOn && body.transform.parent == null && other.transform.parent == null, "Multi deselect did not detach its pivot or retained scope."));
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true);
            var box = Object.FindFirstObjectByType<BoxSelectUI>(); int releases = 0;
            box.OnReleaseBox += (_, _) => releases++;
            Set(box, "selecting", true);
            await Escape(() => { Invoke(box, "ResolveBoxSelect", false, new Vector2(500,500)); Require(releases == 0 && selection.current?.gameObject == body, "Canceled box selection later fired on release."); });

            // Connect retains the tool and selection while canceling source/animation.
            var connect = Object.FindFirstObjectByType<ConnectTool>();
            Get<Toggle>(connect, "toolbarToggle").isOn = true;
            Set(connect, "hasSource", true);
            await Escape(() => Require(!Get<bool>(connect, "hasSource") && ConnectTool.IsCapturingPoints && selection.current?.gameObject == body, "Connect source cancellation advanced another tier."));
            int beforeConnect = StateSystem.curState.elements.Count;
            Set(connect, "sourcePosition", body.transform.position);
            Invoke(connect, "MoveSelection", body.transform.position + Vector3.up * 9);
            Get<Tween>(connect, "moveTween").Goto(.1f);
            await Escape(() => { Near(body.transform.position, start.position, "Connect animation did not restore pose."); Require(StateSystem.curState.elements.Count == beforeConnect && ConnectTool.IsCapturingPoints, "Connect cancel left undo debris or exited tool."); });
            await Escape(() => Require(selection.current == null && ConnectTool.IsCapturingPoints, "Connect deselect also exited the tool."));
            await Escape(() => Require(!ConnectTool.IsCapturingPoints && !Get<Toggle>(connect, "toolbarToggle").isOn, "Connect tool did not turn off."));

            var a = PartsManager.GeneratePart("SPKT-Normal-10T", new Vector3(400, 400, 400), Quaternion.identity);
            var b = PartsManager.GeneratePart("SPKT-Normal-40T", new Vector3(406, 400, 400), Quaternion.identity);
            Require(ChainLoop.Create(new[] { a.GetComponent<SimulationPart>(), b.GetComponent<SimulationPart>() }) != null, "Simulation fixture chain failed.");
            selection.SetCurrent(new ObjectSelection { gameObject = a, individual = true }, true);
            var simulation = Object.FindFirstObjectByType<SimulationTool>(); simulation.Activate();
            Require(simulation.Current != null, "Simulation fixture did not start.");
            var orientation = a.transform.rotation; simulation.Seek(.5);
            await Escape(() => { Near(a.transform.rotation, orientation, "Simulation did not restore preview."); Require(simulation.Current == null && SimulationTool.IsActive && selection.current?.gameObject == a, "Simulation cancel advanced another tier."); });
            Require(SimulationTool.IsActive, "Simulation exited between Escape presses.");
            await Escape(() => Require(selection.current == null && SimulationTool.IsActive,
                $"Simulation deselect exited the tool: selection={selection.current?.gameObject}, active={SimulationTool.IsActive}."));
            await Escape(() => Require(!SimulationTool.IsActive, "Simulation tool remained active."));

            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true); transformButton.isOn = true;
            var custom = Object.FindFirstObjectByType<CustomModelTool>(); custom.ToggleMounts();
            await Escape(() => Require(!Get<bool>(custom, "placingMounts") && transformButton.isOn, "Mounting mode did not cancel independently."));
            selection.SetCurrent(new ObjectSelection { gameObject = body, individual = true }, true);

            var fieldObject = new GameObject("Escape text fixture", typeof(RectTransform), typeof(InputField));
            var labelObject = new GameObject("Text", typeof(RectTransform), typeof(Text)); labelObject.transform.SetParent(fieldObject.transform);
            var field = fieldObject.GetComponent<InputField>(); field.textComponent = labelObject.GetComponent<Text>();
            field.textComponent.font = TransformFeedback.FeedbackFont; field.text = "before";
            EventSystem.current.SetSelectedGameObject(fieldObject); field.ActivateInputField(); await NextFrame(); field.text = "after";
            await Escape(() => Require(field.text == "before" && EventSystem.current.currentSelectedGameObject == null && selection.current?.gameObject == body && transformButton.isOn,
                "Text cancellation did not restore text or consumed selection/tool."));
            Object.Destroy(fieldObject);
            var buildUI = Object.FindFirstObjectByType<CurrentBuildUI>(FindObjectsInactive.Include); int renames = 0;
            buildUI.gameObject.SetActive(true);
            Get<UnityEngine.Events.UnityEvent<string>>(buildUI, "OnRenameSuccess").AddListener(_ => renames++);
            buildUI.StartRenaming(); await NextFrame();
            var renameField = Get<InputField>(buildUI, "renameField"); renameField.text = "Discard this name";
            await Escape(() => Require(renames == 0 && !renameField.gameObject.activeSelf && selection.current?.gameObject == body && transformButton.isOn,
                "Escape committed a project rename or cleared the part selection."));
            var prefs = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(c => c.name == "Preferences Canvas");
            prefs.gameObject.SetActive(true); prefs.enabled = true;
            await Escape(() => Require(!prefs.gameObject.activeSelf && selection.current != null && transformButton.isOn, "Preferences Escape deselected the build."));
            var menu = Object.FindObjectsByType<MenuBarToggle>(FindObjectsSortMode.None).First().GetComponent<Toggle>(); menu.isOn = true;
            await Escape(() => Require(!menu.isOn && selection.current != null && transformButton.isOn, "Menu Escape also deselected the build."));
            var dropdown = Object.FindFirstObjectByType<AddPartsUI>().groupDropdown;
            dropdown.gameObject.SetActive(true); dropdown.enabled = true; dropdown.interactable = true;
            await NextFrame();
            dropdown.Show();
            Require(dropdown.template.parent.Find("Dropdown List") != null,
                $"Dropdown fixture did not open: active={dropdown.IsActive()}, interactable={dropdown.IsInteractable()}.");
            EventSystem.current.SetSelectedGameObject(dropdown.gameObject);
            await Escape(() => Require(EventSystem.current.currentSelectedGameObject == null && selection.current?.gameObject == body && transformButton.isOn,
                "Dropdown cancellation retained focus or deselected the build."));

            // Rebind cancellation runs during InputSystem.Update, before MonoBehaviour.Update.
            var input = Get<Protobot.InputEvents.InputEvent>(keyboard, "grabInput");
            input.rebindAction.AttemptRebind();
            await Escape(() => Require(!RebindAction.Rebinding && selection.current?.gameObject == body && transformButton.isOn, "Canceling rebinding also deselected the build."));

            Keys(); await NextFrame(); Keys(Key.Escape); EscapeDeselect.TryHandleEscape();
            Require(selection.current == null && transformButton.isOn, "Final selection tier failed.");
            for (int i = 0; i < 4; i++) { await NextFrame(); EscapeDeselect.TryHandleEscape(); }
            Require(transformButton.isOn, "Holding Escape advanced through more than one tier.");
            Keys(); await NextFrame();
            await Escape(() => Require(!transformButton.isOn, "Tool failed after held Escape was released."));
        }

        private static async Task Escape(Action check) {
            Keys(); Keys(Key.Escape);
            Require(EscapeDeselect.TryHandleEscape(), "Escape was not dispatched.");
            check(); await NextFrame(); Keys(); await NextFrame();
        }
        private static async Task NextFrame() { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
        private static void Keys(params Key[] keys) {
            InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(keys));
            typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        }
        private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        private static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
        private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < .001f, message);
        private static void Near(Quaternion a, Quaternion b, string message) => Require(Quaternion.Angle(a, b) < .01f, message);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
