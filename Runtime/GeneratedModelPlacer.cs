using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

namespace MeshPresso
{
    /// <summary>
    /// Saves the downloaded FBX (+ textures) under Assets/, imports it through the
    /// AssetDatabase and places an instance in the open scene. FBX cannot be imported
    /// at runtime in a built player, so this only works inside the Unity Editor.
    /// </summary>
    public static class GeneratedModelPlacer
    {
        public class DownloadedFile
        {
            public byte[] Data;
            public string FileName;
        }

        public class PlacementResult
        {
            public bool Ok;
            public string Error;
            public GameObject Instance;
            public string AssetPath;
        }

        public static PlacementResult ImportAndPlace(
            string prompt,
            DownloadedFile model,
            List<DownloadedFile> textures,
            VoiceTo3DSettings settings,
            SpawnPose spawnPose,
            string rawResponseJson = null)
        {
            var result = new PlacementResult();
#if UNITY_EDITOR
            try
            {
                string folder = $"{settings.ResolveGeneratedFolder()}/{DateTime.Now:yyyyMMdd_HHmmss}";
                Directory.CreateDirectory(Path.Combine(ProjectRoot(), folder));

                // Write texture files first so the model import can see them.
                var texturePaths = new List<string>();
                if (textures != null)
                {
                    foreach (var texture in textures)
                    {
                        string texPath = folder + "/" + Sanitize(texture.FileName);
                        File.WriteAllBytes(Path.Combine(ProjectRoot(), texPath), texture.Data);
                        texturePaths.Add(texPath);
                    }
                }

                string modelPath = folder + "/" + Sanitize(model.FileName);
                File.WriteAllBytes(Path.Combine(ProjectRoot(), modelPath), model.Data);
                result.AssetPath = modelPath;

                // Keep the raw API response next to the model for debugging.
                if (!string.IsNullOrEmpty(rawResponseJson))
                    File.WriteAllText(Path.Combine(ProjectRoot(), folder + "/genpresso_response.json"), rawResponseJson);

                foreach (string texPath in texturePaths)
                    AssetDatabase.ImportAsset(texPath, ImportOptions());

                AssetDatabase.ImportAsset(modelPath, ImportOptions());

                texturePaths = ResolveTextures(modelPath, folder, texturePaths);

                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (modelAsset == null)
                {
                    result.Error = $"Unity could not import the generated model at {modelPath}.";
                    return result;
                }

                // Once the textures exist as files Unity re-links the FBX's own materials to
                // them, so only build a replacement when the import came out untextured.
                bool modelIsTextured = HasTexturedMaterial(modelAsset);
                string materialPath = modelIsTextured ? null : CreateMaterialFromTextures(folder, texturePaths);

                if (!modelIsTextured && string.IsNullOrEmpty(materialPath))
                {
                    Debug.LogWarning($"[MeshPresso] {Path.GetFileName(modelPath)} imported without textures and none " +
                                     "could be recovered from the file. The model will render untextured.");
                }

                var instance = UnityEngine.Object.Instantiate(modelAsset);
                instance.name = BuildObjectName(prompt);
                ApplyMaterial(instance, materialPath);
                ApplySpawnPose(instance, modelAsset.transform.rotation, spawnPose, settings);

                if (settings.keepPlacementAfterPlayMode && Application.isPlaying)
                {
                    VoiceTo3DPlacementPersistence.Register(
                        modelPath, materialPath, instance.name, instance.transform);
                }

                result.Instance = instance;
                result.Ok = true;
            }
            catch (Exception e)
            {
                result.Error = "Import/placement failed: " + e.Message;
            }
#else
            result.Error = "FBX import is only supported inside the Unity Editor. " +
                           "For built players, switch geometry_file_format to glb and add a runtime glTF importer (e.g. glTFast).";
#endif
            return result;
        }

#if UNITY_EDITOR
        static ImportAssetOptions ImportOptions()
        {
            return ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate;
        }

        static string ProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        static bool LooksLikeNormalMap(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            return name.Contains("normal");
        }

        static bool ContainsAny(string path, params string[] keywords)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            foreach (string keyword in keywords)
            {
                if (name.Contains(keyword)) return true;
            }
            return false;
        }

        /// <summary>
        /// True only for standalone image assets. A "t:Texture2D" search also matches
        /// models that merely *contain* texture sub-assets, and those must not be
        /// mistaken for usable texture files.
        /// </summary>
        public static bool IsStandaloneTexture(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && AssetImporter.GetAtPath(assetPath) is TextureImporter;
        }

        /// <summary>Lists the standalone image assets sitting in a folder.</summary>
        public static List<string> FindTexturesInFolder(string folder)
        {
            return AssetDatabase.FindAssets("t:Texture2D", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(IsStandaloneTexture)
                .ToList();
        }

        /// <summary>
        /// Picks the base color map. Never returns a normal/metallic/roughness map,
        /// because feeding one of those into _BaseMap renders as visible garbage.
        /// </summary>
        public static string FindBaseColorPath(List<string> texturePaths)
        {
            if (texturePaths == null) return null;
            return texturePaths.FirstOrDefault(p => ContainsAny(p, "basecolor", "base_color", "albedo", "diffuse"))
                ?? texturePaths.FirstOrDefault(p => !LooksLikeNormalMap(p)
                                                    && !ContainsAny(p, "metal", "rough", "occlusion", "ao", "specular", "emissive"));
        }

        /// <summary>
        /// Returns the textures usable for material building. Rodin normally embeds the
        /// PBR maps inside the FBX rather than shipping separate files, so this extracts
        /// them whenever the downloaded set has no usable base color.
        /// </summary>
        public static List<string> ResolveTextures(string modelPath, string folder, List<string> downloadedPaths)
        {
            var paths = (downloadedPaths ?? new List<string>()).Where(IsStandaloneTexture).ToList();
            FixNormalMapImportSettings(paths);

            if (FindBaseColorPath(paths) != null) return paths;

            var extracted = ExtractEmbeddedTextures(modelPath, folder);
            if (extracted.Count > 0)
            {
                FixNormalMapImportSettings(extracted);
                return extracted;
            }
            return paths;
        }

        /// <summary>
        /// Extracts textures embedded in the model file, reimports the model so its own
        /// materials re-link to them, and returns the extracted asset paths.
        /// </summary>
        public static List<string> ExtractEmbeddedTextures(string modelPath, string folder)
        {
            var extracted = new List<string>();
            if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter importer)) return extracted;

            try
            {
                importer.ExtractTextures(folder);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MeshPresso] Embedded texture extraction failed: " + e.Message);
                return extracted;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            // Reimport so the model's own materials pick up the now-external textures.
            AssetDatabase.ImportAsset(modelPath, ImportOptions());

            extracted = FindTexturesInFolder(folder);
            if (extracted.Count > 0)
                Debug.Log($"[MeshPresso] Extracted {extracted.Count} embedded texture(s) from {Path.GetFileName(modelPath)}.");
            else
                Debug.LogWarning($"[MeshPresso] {Path.GetFileName(modelPath)} carries no embedded textures to extract.");
            return extracted;
        }

        /// <summary>Switches textures whose name contains "normal" to the NormalMap importer type.</summary>
        public static void FixNormalMapImportSettings(List<string> texturePaths)
        {
            if (texturePaths == null) return;
            foreach (string texPath in texturePaths.Where(LooksLikeNormalMap))
            {
                if (AssetImporter.GetAtPath(texPath) is TextureImporter importer &&
                    importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.SaveAndReimport();
                }
            }
        }

        public static string CreateMaterialFromTextures(string folder, List<string> texturePaths)
        {
            if (texturePaths == null || texturePaths.Count == 0) return null;

            // No base color means no material: leaving the model's own imported
            // material in place beats assigning a normal map as albedo.
            string basePath = FindBaseColorPath(texturePaths);
            if (basePath == null) return null;
            string normalPath = texturePaths.FirstOrDefault(LooksLikeNormalMap);

            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
            if (baseTex == null) return null;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) return null;

            string materialPath = folder + "/GeneratedMaterial.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var material = existing != null ? existing : new Material(shader);

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", baseTex);
            else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", baseTex);

            if (!string.IsNullOrEmpty(normalPath))
            {
                var normalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                if (normalTex != null && material.HasProperty("_BumpMap"))
                {
                    material.SetTexture("_BumpMap", normalTex);
                    material.EnableKeyword("_NORMALMAP");
                }
            }

            if (existing == null) AssetDatabase.CreateAsset(material, materialPath);
            else EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return materialPath;
        }

        // Base color slot names across the shaders models arrive on: URP Lit, Built-in
        // Standard, HDRP Lit, and glTFast's glTF shaders.
        static readonly string[] BaseColorProperties =
            { "_BaseMap", "_MainTex", "_BaseColorMap", "baseColorTexture" };

        /// <summary>True when any of the model's own materials already has a base texture.</summary>
        public static bool HasTexturedMaterial(GameObject modelAsset)
        {
            if (modelAsset == null) return false;

            foreach (var renderer in modelAsset.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (string property in BaseColorProperties)
                    {
                        if (material.HasProperty(property) && material.GetTexture(property) != null) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Puts a clone's renderers back onto the materials of the model it was
        /// instantiated from, discarding any material previously stamped over them.
        /// </summary>
        public static bool RestoreModelMaterials(GameObject instance, GameObject modelAsset)
        {
            var instanceRenderers = instance.GetComponentsInChildren<Renderer>(true);
            var assetRenderers = modelAsset.GetComponentsInChildren<Renderer>(true);
            if (instanceRenderers.Length == 0 || instanceRenderers.Length != assetRenderers.Length) return false;

            for (int i = 0; i < instanceRenderers.Length; i++)
                instanceRenderers[i].sharedMaterials = assetRenderers[i].sharedMaterials;
            return true;
        }

        public static void ApplyMaterial(GameObject instance, string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath)) return;
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return;

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                var materials = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
            }
        }

        static void ApplySpawnPose(
            GameObject instance, Quaternion importRotation, SpawnPose spawnPose, VoiceTo3DSettings settings)
        {
            // importRotation is the axis conversion the importer baked into the model root
            // (-90 on X for Rodin's Z-up FBX, identity for glTF). Dropping it is what laid
            // models on their side, so it is kept and only then adjusted by the user offset.
            instance.transform.rotation =
                spawnPose.Rotation * importRotation * Quaternion.Euler(settings.placementRotationEuler);

            var bounds = ComputeBounds(instance);
            if (settings.targetSize > 0f && bounds.HasValue)
            {
                float maxDimension = Mathf.Max(bounds.Value.size.x, bounds.Value.size.y, bounds.Value.size.z);
                if (maxDimension > 0.0001f)
                    instance.transform.localScale *= settings.targetSize / maxDimension;
                bounds = ComputeBounds(instance);
            }

            if (!bounds.HasValue)
            {
                instance.transform.position = spawnPose.Position;
                return;
            }

            // Line the model's center up with the spawn point, then optionally drop it so it
            // rests on that point instead of straddling it — what you want when the anchor
            // sits on a floor or table.
            Vector3 anchorInModel = bounds.Value.center;
            if (settings.placeBottomAtSpawnPoint) anchorInModel.y = bounds.Value.min.y;

            instance.transform.position += spawnPose.Position - anchorInModel;
        }

        static Bounds? ComputeBounds(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return null;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static string Sanitize(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "file.bin";
            foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            return fileName;
        }
#endif

        static string BuildObjectName(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return "VoiceTo3D_Model";
            string snippet = new string(prompt
                .Take(40)
                .Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ')
                .ToArray());
            snippet = string.Join(" ", snippet.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrEmpty(snippet) ? "VoiceTo3D_Model" : "VoiceTo3D_" + snippet;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Objects instantiated during Play Mode disappear when Play Mode ends.
    /// This records every placement in SessionState and re-creates it in the
    /// edit-mode scene once Play Mode exits, so generated models stay placed.
    /// </summary>
    [InitializeOnLoad]
    public static class VoiceTo3DPlacementPersistence
    {
        const string SessionKey = "MeshPresso.VoiceTo3D.PendingPlacements";

        static VoiceTo3DPlacementPersistence()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Register(string assetPath, string materialPath, string objectName, Transform transform)
        {
            var records = JArray.Parse(SessionState.GetString(SessionKey, "[]"));
            records.Add(new JObject
            {
                ["assetPath"] = assetPath,
                ["materialPath"] = materialPath,
                ["name"] = objectName,
                ["position"] = new JArray(transform.position.x, transform.position.y, transform.position.z),
                ["rotation"] = new JArray(transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w),
                ["scale"] = new JArray(transform.localScale.x, transform.localScale.y, transform.localScale.z)
            });
            SessionState.SetString(SessionKey, records.ToString(Formatting.None));
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;

            string raw = SessionState.GetString(SessionKey, "[]");
            SessionState.EraseString(SessionKey);

            JArray records;
            try { records = JArray.Parse(raw); }
            catch { return; }
            if (records.Count == 0) return;

            foreach (var record in records)
            {
                string assetPath = record["assetPath"]?.ToString();
                if (string.IsNullOrEmpty(assetPath)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset == null) continue;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                instance.name = record["name"]?.ToString() ?? asset.name;
                instance.transform.position = ReadVector3(record["position"], Vector3.zero);
                instance.transform.rotation = ReadQuaternion(record["rotation"]);
                instance.transform.localScale = ReadVector3(record["scale"], Vector3.one);

                string materialPath = record["materialPath"]?.ToString();
                if (!string.IsNullOrEmpty(materialPath))
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (material != null)
                    {
                        foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                        {
                            var materials = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
                            for (int i = 0; i < materials.Length; i++) materials[i] = material;
                            renderer.sharedMaterials = materials;
                        }
                    }
                }

                Undo.RegisterCreatedObjectUndo(instance, "Place Voice To 3D Model");
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[MeshPresso] Re-placed {records.Count} generated model(s) into the edit-mode scene.");
        }

        static Vector3 ReadVector3(JToken token, Vector3 fallback)
        {
            if (token is JArray array && array.Count == 3)
                return new Vector3((float)array[0], (float)array[1], (float)array[2]);
            return fallback;
        }

        static Quaternion ReadQuaternion(JToken token)
        {
            if (token is JArray array && array.Count == 4)
                return new Quaternion((float)array[0], (float)array[1], (float)array[2], (float)array[3]);
            return Quaternion.identity;
        }
    }
#endif
}
