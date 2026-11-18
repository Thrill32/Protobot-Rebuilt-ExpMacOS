using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Protobot.Collaboration;
using UnityEngine;

namespace ProtobotEditorTools {
    public static class CollaborationCustomImportValidation {
        // Mono wraps bounded stream errors differently from .NET. Run this in Unity.
        public static void Validate() {
            var asset = new GeometryAsset { name = "Custom import limit regression",
                vertices = new[] { -1.0, -1, -1, 1, -1, -1, 0, 1, 0 }, triangles = new[] { 0, 1, 2 } };
            asset.hash = DocumentModel.GeometryHash(asset);
            byte[] bytes = GeometryTransfer.Encode(asset);
            Require(LocalJson.Encode(asset, bytes.Length).SequenceEqual(bytes), "Exact bounded serialization changed geometry.");
            try { LocalJson.Encode(asset, bytes.Length - 1); throw new Exception("Oversized serialization succeeded."); }
            catch (CollaborationException error) {
                Require(error.Code == "local-serialization-limit" && WireFailure.Code(error) == error.Code,
                    "Unity obscured the geometry limit behind serializer exception text.");
            }
            // Force overflow during array serialization, while Mono's generated
            // contract writer is still inside reflection (rather than final flush).
            asset.vertices = Enumerable.Repeat(1.0, 30000).ToArray();
            try { LocalJson.Encode(asset, 1024); throw new Exception("Mid-array serialization exceeded its limit."); }
            catch (CollaborationException error) { Require(error.Code == "local-serialization-limit", "Mid-array limit error was wrapped."); }
            Debug.Log("Custom import limit validation passed: exact byte boundary and actionable Mono serializer rejection.");
        }
        // Optional diagnosis against an untouched player's managed assembly. The
        // value is local fixture text, never a server reply or user document.
        public static void TraceOriginalSerializer() {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--original-collaboration-assembly");
            Require(index >= 0 && index + 1 < args.Length, "Supply the preserved original assembly.");
            var original = Assembly.Load(System.IO.File.ReadAllBytes(args[index + 1]));
            Debug.Log("Original serializer assembly MVID: " + original.ManifestModule.ModuleVersionId);
            var type = original.GetType("Protobot.Collaboration.GeometryAsset");
            var asset = Activator.CreateInstance(type);
            type.GetField("name").SetValue(asset, "Original array serialization regression");
            type.GetField("hash").SetValue(asset, new string('0', 64));
            type.GetField("vertices").SetValue(asset, Enumerable.Repeat(1.0, 2934000).ToArray());
            type.GetField("triangles").SetValue(asset, Enumerable.Range(0, 978000).ToArray());
            var encode = original.GetType("Protobot.Collaboration.LocalJson").GetMethod("Encode").MakeGenericMethod(type);
            try { encode.Invoke(null, new object[] { asset, GeometryTransfer.LegacyMaxAssetBytes, CancellationToken.None }); }
            catch (TargetInvocationException error) {
                Debug.Log("Original bounded serializer failure: " + error.InnerException);
                return;
            }
            throw new Exception("Original bounded serializer unexpectedly succeeded.");
        }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
