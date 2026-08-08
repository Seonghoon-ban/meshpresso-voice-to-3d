using UnityEngine;

namespace MeshPresso
{
    public enum RodinTier
    {
        Gen25Minimum,
        Gen25ExtremeLow,
        Gen25Low
    }

    public enum RodinMaterialMode
    {
        PBR,
        Shaded,
        All,
        None
    }

    [CreateAssetMenu(fileName = "VoiceTo3DSettings", menuName = "MeshPresso/Voice To 3D Settings")]
    public class VoiceTo3DSettings : ScriptableObject
    {
        [Header("Genpresso")]
        [Tooltip("Genpresso API key (starts with gp_). Leave empty to fall back to the GENPRESSO_API_KEY environment variable.")]
        public string genpressoApiKey = "";

        [Tooltip("Genpresso API root. Both the chat and media calls hang off this.")]
        public string apiBaseUrl = "https://genpresso.ai/api/v1";

        [Header("Prompt generation (chat)")]
        [Tooltip("Model slug as published by its maker — no provider prefix.")]
        public string promptModel = "google/gemini-3.5-flash-lite";

        [TextArea(5, 12)]
        public string promptSystemInstruction =
            "You are a prompt engineer for a text-to-3D generative model (Hyper3D Rodin). " +
            "The user speaks a description of an object they want, possibly in Korean or another language. " +
            "Listen to the audio and produce ONE concise English prompt describing a single 3D object. " +
            "Rules: output ONLY the prompt text, with no quotes, markdown or explanations; " +
            "describe the object's shape, key parts, materials, colors and style; " +
            "do not describe scenes, backgrounds, lighting or cameras; keep it under 400 characters. " +
            "If the speech is unclear, make the most reasonable guess about the intended object.";

        [Header("3D generation (media)")]
        [Tooltip("Media model path. Genpresso re-hosts fal.ai models under 'gp/' — the original 'fal-ai/' prefix returns 404.")]
        public string mediaModel = "gp/hyper3d/rodin/v2.5/text-to-3d/fast";

        public RodinTier tier = RodinTier.Gen25ExtremeLow;

        [Tooltip("glb, usdz, fbx, obj or stl. glb needs glTFast; fbx uses Unity's built-in importer. Editor only either way.")]
        public string geometryFileFormat = "glb";
        public RodinMaterialMode materialMode = RodinMaterialMode.PBR;

        [Tooltip("Give up if generation has not completed after this many seconds.")]
        public int generationTimeoutSeconds = 600;

        [Tooltip("Project folder that generated models are saved into. Must live under Assets/.")]
        public string generatedFolder = "Assets/MeshPresso/Generated";

        [Header("Recording")]
        public int sampleRate = 16000;
        public int maxRecordSeconds = 120;
        [Tooltip("Recordings shorter than this are discarded.")]
        public float minRecordSeconds = 0.4f;

        [Header("Placement")]
        [Tooltip("Distance in front of the main camera where the model is placed. " +
                 "Ignored when the controller has a Spawn Anchor assigned.")]
        public float spawnDistance = 2.5f;
        [Tooltip("Rest the model's bottom on the spawn point instead of centering it there. " +
                 "Turn this on when the spawn anchor sits on a floor or table.")]
        public bool placeBottomAtSpawnPoint = false;
        [Tooltip("Extra rotation applied on top of the axis conversion the importer already baked in " +
                 "(-90 on X for Rodin's Z-up FBX, none for glTF/GLB). Leave at zero unless a model still lands wrong.")]
        public Vector3 placementRotationEuler = Vector3.zero;
        [Tooltip("Uniformly rescale so the model's largest dimension equals this size (meters). 0 = keep original scale.")]
        public float targetSize = 1.0f;
        [Tooltip("Re-create the placed object in the scene after leaving Play Mode, so it is not lost.")]
        public bool keepPlacementAfterPlayMode = true;

        public string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(genpressoApiKey)) return genpressoApiKey.Trim();
            return System.Environment.GetEnvironmentVariable("GENPRESSO_API_KEY");
        }

        /// <summary>
        /// The generated-content folder, normalised to a project-relative path under Assets/.
        /// Generated files cannot go inside the package itself — an installed package is read-only.
        /// </summary>
        public string ResolveGeneratedFolder()
        {
            string folder = string.IsNullOrWhiteSpace(generatedFolder)
                ? "Assets/MeshPresso/Generated"
                : generatedFolder.Trim().Replace('\\', '/').Trim('/');

            if (folder != "Assets" && !folder.StartsWith("Assets/")) folder = "Assets/" + folder;
            return folder;
        }

        /// <summary>Builds an absolute API URL from a path relative to the API root.</summary>
        public string Url(string relativePath)
        {
            string root = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? "https://genpresso.ai/api/v1"
                : apiBaseUrl.Trim().TrimEnd('/');
            return root + "/" + relativePath.TrimStart('/');
        }

        public string TierApiString()
        {
            switch (tier)
            {
                case RodinTier.Gen25Minimum: return "Gen-2.5-Minimum";
                case RodinTier.Gen25Low: return "Gen-2.5-Low";
                default: return "Gen-2.5-Extreme-Low";
            }
        }

        public string MaterialApiString()
        {
            return materialMode.ToString();
        }
    }
}
