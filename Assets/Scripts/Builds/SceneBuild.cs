using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using UnityEditor;

namespace Protobot.Builds {
    public static class SceneBuild {
        public static Action<BuildData> OnGenerateBuild;

        public static BuildData DefaultBuild => new BuildData {
            name = "DefaultBuild",
            camera = new CameraData {
                xPos = 0,
                yPos = 0,
                zPos = 0,
                xRot = 30,
                yRot = 45,
                zRot = 0,
                zoom = -15,
                isOrtho = false
            },
            lastWriteTime = DateTime.Now.ToString("MMMM dd, yyyy h:mm tt")
        };

        /// <summary>
        /// Generates all the objects into the scene using given BuildData
        /// </summary>
        public static void GenerateBuild(BuildData buildData) {
            // Validate embedded geometry before replacing any current scene objects.
            foreach (var part in buildData.parts ?? Array.Empty<ObjectData>())
                if (part.IsCustomModel) CustomModel.Validate(part.customModel);
            CustomModelTool.CancelActive();
            BuildTransferTool.CancelPending();
            if (Placement.IsCopyingSelection) UnityEngine.Object.FindFirstObjectByType<Placement>()?.StopPlacing();
            Protobot.Simulation.SimulationTool.ExitForEdit();
            Protobot.Simulation.ChainLoop.ClearAll();
            Debug.Log("Generating a build with " +
                      (buildData.parts == null ? 0 : buildData.parts.Length) + " parts");

            foreach (var manager in UnityEngine.Object.FindObjectsByType<Protobot.SelectionSystem.SelectionManager>(FindObjectsSortMode.None))
                manager.ClearCurrent(false, true, true);
            PartsManager.DestroyLoadedObjects();

            //Camera Data
            CameraData camData = buildData.camera;

            Vector3 savedCamPos = new Vector3((float)camData.xPos, (float)camData.yPos, (float)camData.zPos);
            Vector3 savedCamAngle = new Vector3((float)camData.xRot, (float)camData.yRot, (float)camData.zRot);

            PivotCamera.Main.SetTransform(savedCamPos, savedCamAngle, (float)camData.zoom);

            var projectionSwitcher = PivotCamera.Main.GetComponent<ProjectionSwitcher>();

            if (camData.isOrtho)
                projectionSwitcher.SwitchToOrtho(0);
            else
                projectionSwitcher.SwitchToPers(0);

            //Parts
            if (buildData.parts != null) {
                List<ObjectData> connectingObjects = new();

                foreach (ObjectData part in buildData.parts) {
                    if (part.partId == "Error") continue; //this is jank but I'm avoiding editing main scene
                    if (!part.IsCustomModel && PartsManager.GetPartType(part.partId)?.connectingPart == true) {
                        connectingObjects.Add(part);
                    }
                    else
                        GenerateObject(part, buildData);
                }

                foreach (ObjectData part in connectingObjects)
                    GenerateObject(part, buildData);

            }
            
            foreach (var chain in buildData.chains ?? Array.Empty<ChainData>()) Protobot.Simulation.ChainLoop.FromData(chain);
            OnGenerateBuild?.Invoke(buildData);

            if (AppPlatform.OnMac)
            {
                try
                {
                    GameObject.Find("Mac Support Check").GetComponent<MacRebind>().RebindCheck();
                }
                catch (Exception ex)
                {
                    Debug.LogError("An error occurred while performing Mac rebind check: " + ex.Message);
                }
            }
        }

        internal static GameObject GenerateObject(ObjectData objectData, BuildData buildData)
        {
            GameObject generatedObject = objectData.IsCustomModel
                ? CustomModel.Create(objectData.customModel, objectData.GetPos(), objectData.GetRot())
                : PartsManager.GeneratePart(objectData.partId, objectData.GetPos(), objectData.GetRot());
            generatedObject.GetComponent<SavedObject>().ConnectionId = objectData.connectionId;
            generatedObject.GetComponent<SavedObject>().state = objectData.states;
            if (buildData.HasColors)
            { 
                if (objectData.hasPaint) PartColors.ApplyPaint(generatedObject, objectData.GetColor());
                else if (PartColors.SampleMaterial(generatedObject) != null) {
                    OwnedPartMaterials.CaptureGenerated(generatedObject);
                    var material = PartColors.SampleMaterial(generatedObject);
                    var color = objectData.GetColor();
                    // Build files store RGB; retain the generated material's
                    // transparency when restoring an unpainted/legacy color.
                    color.a = material.color.a;
                    material.color = color;
                }
            }
            return generatedObject;
        }
            


        /// <summary>
        /// Generates an empty build with default camera data into the scene
        /// </summary>
        public static BuildData GenerateDefault(string buildName) {
            var newBuild = DefaultBuild;
            newBuild.name = buildName;

            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            newBuild.fileName = new string(buildName.Where(m => !invalidChars.Contains(m)).ToArray<char>());

            GenerateBuild(newBuild);

            return newBuild;
        }

        /// <summary>
        /// Converts scene objects into BuildData
        /// </summary>
        /// <remarks>Does not contain lastWriteTime, createTime, fileName, or name</remarks>
        public static BuildData ToBuildData(IEnumerable<GameObject> objects = null) {
            //Camera
            PivotCamera cam = PivotCamera.Main;
            ProjectionSwitcher projectionSwitcher = cam.GetComponent<ProjectionSwitcher>();

            CameraData newCameraData = new CameraData {
                xPos = cam.focusPosition.x,
                yPos = cam.focusPosition.y,
                zPos = cam.focusPosition.z,

                xRot = cam.lookAngle.x,
                yRot = cam.lookAngle.y,
                zRot = cam.lookAngle.z,

                zoom = cam.focusDistance,

                isOrtho = projectionSwitcher.isOrtho
            };

            //Parts
            List<GameObject> sceneObjs = objects != null ? objects.Distinct().ToList() : PartsManager.FindLoadedObjects();

            ObjectData[] newParts = new ObjectData[sceneObjs.Count];

            for (int i = 0; i < newParts.Length; i++) {
                Transform tForm = sceneObjs[i].transform;
                SavedObject savedData = tForm.GetComponent<SavedObject>();
                var savedColor = PartColors.GetColor(tForm.gameObject);

                var savedPose = Protobot.Simulation.SimulationTool.SavedPose(sceneObjs[i]);
                var position = savedPose.position;
                var eulerAngles = savedPose.rotation.eulerAngles;
                newParts[i] = new ObjectData {
                    partId = savedData.id,
                    states = savedData.state,
                    connectionId = savedData.ConnectionId,
                    customModel = tForm.TryGetComponent<CustomModel>(out var custom) ? custom.Snapshot() : null,
                    hasPaint = savedData.hasPaint,

                    xPos = position.x,
                    yPos = position.y,
                    zPos = position.z,

                    xRot = eulerAngles.x,
                    yRot = eulerAngles.y,
                    zRot = eulerAngles.z,

                    rColor = savedColor.r,
                    bColor = savedColor.b,
                    gColor = savedColor.g,
                };
            }


            var ids = newParts.Select(part => part.connectionId).ToHashSet();
            return new BuildData {
                camera = newCameraData,
                parts = newParts,
                chains = Protobot.Simulation.ChainLoop.SaveAll().Where(chain => chain.members.All(ids.Contains)).ToArray(),
            };
        }
    }
}
