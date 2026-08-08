using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshPresso.EditorTools
{
    public static class VoiceTo3DSetup
    {
        const string SettingsPath = "Assets/MeshPresso/VoiceTo3DSettings.asset";

        [MenuItem("MeshPresso/Voice To 3D/Setup Scene")]
        public static void SetupScene()
        {
            var settings = GetOrCreateSettings();

            var controller = Object.FindFirstObjectByType<VoiceTo3DController>();
            if (controller == null)
            {
                var gameObject = new GameObject("VoiceTo3DController");
                controller = gameObject.AddComponent<VoiceTo3DController>();
                Undo.RegisterCreatedObjectUndo(gameObject, "Create VoiceTo3DController");
            }

            controller.settings = settings;
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(controller.gameObject);

            Debug.Log("[MeshPresso] Voice To 3D is set up. Enter your Genpresso API key on the selected " +
                      "VoiceTo3DSettings asset, then press Play and hold SPACE to talk.");
        }

        [MenuItem("MeshPresso/Voice To 3D/Select Settings")]
        public static void SelectSettings()
        {
            Selection.activeObject = GetOrCreateSettings();
        }

        /// <summary>
        /// Fixes an already-generated model whose textures are missing: extracts the
        /// textures embedded in its FBX, builds a URP material and (when a scene object
        /// is selected) assigns it to the renderers. Select either the placed scene
        /// object or the FBX asset in the Project window first.
        /// </summary>
        [MenuItem("MeshPresso/Voice To 3D/Repair Selected Model Textures")]
        public static void RepairSelectedModelTextures()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("[MeshPresso] Select a generated model first (the scene object or its FBX asset).");
                return;
            }

            bool isAsset = AssetDatabase.Contains(selected);
            string assetPath = isAsset
                ? AssetDatabase.GetAssetPath(selected)
                : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(selected);

            // Models placed during Play Mode are plain Object.Instantiate clones with no
            // prefab connection, so resolve their source FBX through the shared mesh.
            if (!isAsset && string.IsNullOrEmpty(assetPath))
                assetPath = ResolveModelPathFromMesh(selected);

            // Any imported model counts, not just ModelImporter ones: glb — the default
            // format — comes in through glTFast's ScriptedImporter.
            var modelAsset = string.IsNullOrEmpty(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

            if (modelAsset == null)
            {
                Debug.LogWarning("[MeshPresso] The selection does not resolve to an imported model asset. " +
                                 "Select the placed model (or its glb/fbx in the Project window) and try again.");
                return;
            }

            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');

            var texturePaths = GeneratedModelPlacer.ResolveTextures(
                assetPath, folder, GeneratedModelPlacer.FindTexturesInFolder(folder));

            // Extracting the textures usually lets the importer re-link the model's own
            // materials, and glTF models are textured from the start. Prefer those.
            if (GeneratedModelPlacer.HasTexturedMaterial(modelAsset))
            {
                if (isAsset)
                {
                    Debug.Log($"[MeshPresso] {Path.GetFileName(assetPath)} is textured again. " +
                              "Select the placed scene object and run this menu to refresh it.");
                    return;
                }

                Undo.RegisterFullObjectHierarchyUndo(selected, "Repair Model Textures");
                bool restored = GeneratedModelPlacer.RestoreModelMaterials(selected, modelAsset);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log(restored
                    ? $"[MeshPresso] Restored the model's own textured materials on \"{selected.name}\"."
                    : $"[MeshPresso] {Path.GetFileName(assetPath)} is textured again, but its renderers no longer " +
                      $"match \"{selected.name}\". Delete the object and re-place it from the FBX.");
                return;
            }

            if (texturePaths.Count == 0)
            {
                Debug.LogWarning($"[MeshPresso] No textures found next to or embedded in {assetPath}.");
                return;
            }

            string materialPath = GeneratedModelPlacer.CreateMaterialFromTextures(folder, texturePaths);
            if (string.IsNullOrEmpty(materialPath))
            {
                Debug.LogWarning("[MeshPresso] Could not build a material from the textures " +
                                 "(no base color map among: " + string.Join(", ", texturePaths.Select(Path.GetFileName)) + ").");
                return;
            }

            if (!isAsset)
            {
                Undo.RegisterFullObjectHierarchyUndo(selected, "Repair Model Textures");
                GeneratedModelPlacer.ApplyMaterial(selected, materialPath);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log($"[MeshPresso] Applied {materialPath} to \"{selected.name}\".");
            }
            else
            {
                Debug.Log($"[MeshPresso] Created {materialPath}. Assign it to scene instances of the model, " +
                          "or select the placed scene object and run this menu again.");
            }
        }

        /// <summary>
        /// Finds the imported model an instantiated clone came from by looking up the
        /// asset that owns one of its meshes.
        /// </summary>
        static string ResolveModelPathFromMesh(GameObject instance)
        {
            var meshFilter = instance.GetComponentInChildren<MeshFilter>(true);
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;

            if (mesh == null)
            {
                var skinned = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (skinned != null) mesh = skinned.sharedMesh;
            }

            return mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
        }

        static VoiceTo3DSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<VoiceTo3DSettings>(SettingsPath);
            if (settings != null) return settings;

            // A freshly installed package has no Assets/MeshPresso yet, and CreateAsset
            // refuses to make one for you.
            EnsureFolder(Path.GetDirectoryName(SettingsPath).Replace('\\', '/'));

            settings = ScriptableObject.CreateInstance<VoiceTo3DSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>Creates a project folder and every missing parent above it.</summary>
        static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
