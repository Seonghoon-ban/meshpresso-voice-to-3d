using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MeshPresso
{
    /// <summary>
    /// Hold SPACE and describe an object out loud. On release the recording goes to
    /// Genpresso's chat endpoint to craft a text-to-3D prompt, that prompt goes to
    /// Genpresso's Rodin media model, and the resulting model is imported and placed
    /// in the scene. One API key covers both calls.
    /// </summary>
    public class VoiceTo3DController : MonoBehaviour
    {
        public VoiceTo3DSettings settings;

        [Tooltip("Where generated models appear. Its position and rotation are captured the moment " +
                 "generation starts, so moving it afterwards will not drag an in-flight generation " +
                 "along. Leave empty to spawn in front of the main camera instead.")]
        public Transform spawnAnchor;

        public enum PipelineState
        {
            Idle,
            Recording,
            GeneratingPrompt,
            Generating3D,
            Downloading,
            Placing,
            Completed,
            Error
        }

        public PipelineState State { get; private set; } = PipelineState.Idle;
        public string CurrentPrompt { get; private set; }
        public string StatusMessage { get; private set; } = "";

        readonly MicrophoneRecorder _recorder = new MicrophoneRecorder();
        float _recordStartTime;
        bool _spaceWasHeld;

        void Awake()
        {
#if UNITY_EDITOR
            if (settings == null)
            {
                settings = UnityEditor.AssetDatabase.LoadAssetAtPath<VoiceTo3DSettings>(
                    "Assets/MeshPresso/VoiceTo3DSettings.asset");
            }
#endif
            if (settings == null)
            {
                Debug.LogError("[MeshPresso] VoiceTo3DController has no settings assigned. " +
                               "Create one via Assets > Create > MeshPresso > Voice To 3D Settings.");
                enabled = false;
            }
        }

        void Update()
        {
            bool spaceHeld = IsSpaceHeld();
            // Start on the press edge, not while held: a failed start lands in Error, which is
            // itself a startable state, so a level check would retry (and log) every frame.
            bool spacePressed = spaceHeld && !_spaceWasHeld;
            _spaceWasHeld = spaceHeld;

            if (spacePressed && CanStartRecording())
            {
                StartRecording();
            }
            else if (!spaceHeld && State == PipelineState.Recording)
            {
                StopRecordingAndRun();
            }
            else if (State == PipelineState.Recording &&
                     Time.realtimeSinceStartup - _recordStartTime >= settings.maxRecordSeconds - 0.1f)
            {
                StopRecordingAndRun();
            }
        }

        static bool IsSpaceHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
#else
            return Input.GetKey(KeyCode.Space);
#endif
        }

        bool CanStartRecording()
        {
            return State == PipelineState.Idle
                || State == PipelineState.Completed
                || State == PipelineState.Error;
        }

        void StartRecording()
        {
            if (!_recorder.StartRecording(settings.sampleRate, settings.maxRecordSeconds, out string error))
            {
                SetError(error);
                return;
            }
            _recordStartTime = Time.realtimeSinceStartup;
            State = PipelineState.Recording;
            StatusMessage = "Listening... release SPACE when done.";
        }

        void StopRecordingAndRun()
        {
            var audio = _recorder.StopRecording();
            if (audio == null || audio.DurationSeconds < settings.minRecordSeconds)
            {
                State = PipelineState.Idle;
                StatusMessage = "Recording too short - hold SPACE and speak.";
                return;
            }
            StartCoroutine(RunPipeline(audio));
        }

        IEnumerator RunPipeline(RecordedAudio audio)
        {
            CurrentPrompt = null;

            // Freeze where this model will land before any network call, so the result does
            // not follow the camera or anchor around during the minute or two of generation.
            SpawnPose spawnPose = CaptureSpawnPose();

            // 1. Speech -> text-to-3D prompt. The model hears the audio directly.
            State = PipelineState.GeneratingPrompt;
            StatusMessage = $"Sending {audio.DurationSeconds:0.0}s of speech to {settings.promptModel}...";
            byte[] wav = WavUtility.FromSamples(audio.Samples, audio.SampleRate, audio.Channels);

            var promptResult = new GenpressoChatClient.Result();
            yield return GenpressoChatClient.GeneratePrompt(settings, wav, promptResult);
            if (!promptResult.Ok)
            {
                SetError(promptResult.Error);
                yield break;
            }

            CurrentPrompt = promptResult.Prompt;
            Debug.Log("[MeshPresso] Generated 3D prompt: " + CurrentPrompt);

            // 2. Prompt -> 3D model via Genpresso's Rodin media model.
            State = PipelineState.Generating3D;
            StatusMessage = "Submitting to " + settings.mediaModel + "...";
            var generation = new GenpressoMediaClient.GenerationResult();
            yield return GenpressoMediaClient.GenerateModel(
                settings,
                CurrentPrompt,
                status => StatusMessage = "Genpresso: " + status,
                generation);
            if (!generation.Ok)
            {
                SetError(generation.Error);
                yield break;
            }

            // 3. Download the mesh and its textures.
            State = PipelineState.Downloading;
            StatusMessage = "Downloading " + generation.ModelMesh.FileName + "...";
            var modelDownload = new GenpressoMediaClient.DownloadResult();
            yield return GenpressoMediaClient.DownloadFile(generation.ModelMesh.Url, modelDownload);
            if (!modelDownload.Ok)
            {
                SetError(modelDownload.Error);
                yield break;
            }

            var textures = new List<GeneratedModelPlacer.DownloadedFile>();
            foreach (var textureRef in generation.Textures)
            {
                // Rodin sometimes lists extra model variants (e.g. a shaded FBX) under
                // "textures"; only image files are useful for material building.
                if (!IsImageFile(textureRef.FileName))
                {
                    Debug.Log("[MeshPresso] Ignoring non-image file in the textures list: " + textureRef.FileName);
                    continue;
                }

                StatusMessage = "Downloading " + textureRef.FileName + "...";
                var textureDownload = new GenpressoMediaClient.DownloadResult();
                yield return GenpressoMediaClient.DownloadFile(textureRef.Url, textureDownload);
                if (textureDownload.Ok)
                {
                    textures.Add(new GeneratedModelPlacer.DownloadedFile
                    {
                        Data = textureDownload.Data,
                        FileName = textureRef.FileName
                    });
                }
                else
                {
                    Debug.LogWarning("[MeshPresso] Skipping texture: " + textureDownload.Error);
                }
            }

            // 4. Import into the project and place in the scene.
            State = PipelineState.Placing;
            StatusMessage = "Importing FBX and placing it in the scene...";
            yield return null; // let the UI update before the synchronous import

            var placement = GeneratedModelPlacer.ImportAndPlace(
                CurrentPrompt,
                new GeneratedModelPlacer.DownloadedFile
                {
                    Data = modelDownload.Data,
                    FileName = generation.ModelMesh.FileName
                },
                textures,
                settings,
                spawnPose,
                generation.RawJson);

            if (!placement.Ok)
            {
                SetError(placement.Error);
                yield break;
            }

            State = PipelineState.Completed;
            StatusMessage = $"Placed \"{placement.Instance.name}\" (asset: {placement.AssetPath}). Hold SPACE to make another.";
            Debug.Log("[MeshPresso] " + StatusMessage);
        }

        /// <summary>
        /// The anchor's pose if one is assigned, otherwise a point in front of the main
        /// camera, turned to face it.
        /// </summary>
        SpawnPose CaptureSpawnPose()
        {
            if (spawnAnchor != null)
                return SpawnPose.At(spawnAnchor.position, spawnAnchor.rotation);

            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[MeshPresso] No Spawn Anchor and no main camera — placing at the world origin.");
                return SpawnPose.At(Vector3.forward * settings.spawnDistance, Quaternion.identity);
            }

            Vector3 point = camera.transform.position + camera.transform.forward * settings.spawnDistance;
            Vector3 toCamera = camera.transform.position - point;
            toCamera.y = 0f;
            Quaternion facing = toCamera.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(toCamera.normalized)
                : Quaternion.identity;
            return SpawnPose.At(point, facing);
        }

        static bool IsImageFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg"
                || extension == ".tga" || extension == ".bmp" || extension == ".exr";
        }

        void SetError(string error)
        {
            State = PipelineState.Error;
            StatusMessage = error;
            Debug.LogError("[MeshPresso] " + error);
        }

        void OnDisable()
        {
            _recorder.Abort();
            _spaceWasHeld = false;
        }

        void OnGUI()
        {
            const float width = 560f;
            var boxRect = new Rect(10, 10, width, 120);
            GUI.Box(boxRect, GUIContent.none);

            var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 13 };
            var headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };

            GUILayout.BeginArea(new Rect(20, 16, width - 20, 110));
            string header = State == PipelineState.Recording
                ? $"● REC {Time.realtimeSinceStartup - _recordStartTime:0.0}s"
                : State.ToString();
            GUILayout.Label($"Voice To 3D - {header}", headerStyle);

            if (State == PipelineState.Idle)
                GUILayout.Label("Hold SPACE and describe the 3D object you want, then release.", labelStyle);

            if (!string.IsNullOrEmpty(CurrentPrompt))
                GUILayout.Label("Prompt: " + CurrentPrompt, labelStyle);

            if (!string.IsNullOrEmpty(StatusMessage))
                GUILayout.Label(StatusMessage, labelStyle);
            GUILayout.EndArea();
        }
    }
}
