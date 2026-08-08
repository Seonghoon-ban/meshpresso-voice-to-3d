using UnityEngine;

namespace MeshPresso
{
    public class RecordedAudio
    {
        public float[] Samples;
        public int Channels;
        public int SampleRate;
        public float DurationSeconds => Samples == null || SampleRate <= 0
            ? 0f
            : (float)Samples.Length / Channels / SampleRate;
    }

    /// <summary>Push-to-talk style recorder around UnityEngine.Microphone (default device).</summary>
    public class MicrophoneRecorder
    {
        AudioClip _clip;
        int _sampleRate;

        public bool IsRecording { get; private set; }

        public bool StartRecording(int sampleRate, int maxSeconds, out string error)
        {
            error = null;
            if (IsRecording)
            {
                error = "Already recording.";
                return false;
            }
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                error = "No microphone device found.";
                return false;
            }

            _sampleRate = sampleRate;
            _clip = Microphone.Start(null, false, Mathf.Max(1, maxSeconds), sampleRate);
            if (_clip == null)
            {
                error = "Microphone.Start failed.";
                return false;
            }

            IsRecording = true;
            return true;
        }

        /// <summary>Stops recording and returns the captured audio trimmed to what was actually spoken.</summary>
        public RecordedAudio StopRecording()
        {
            if (!IsRecording) return null;

            int position = Microphone.GetPosition(null);
            Microphone.End(null);
            IsRecording = false;

            if (_clip == null) return null;

            int channels = _clip.channels;
            if (position <= 0 || position > _clip.samples) position = _clip.samples;
            if (position <= 0)
            {
                Object.Destroy(_clip);
                _clip = null;
                return null;
            }

            var samples = new float[position * channels];
            _clip.GetData(samples, 0);
            Object.Destroy(_clip);
            _clip = null;

            return new RecordedAudio
            {
                Samples = samples,
                Channels = channels,
                SampleRate = _sampleRate
            };
        }

        public void Abort()
        {
            if (!IsRecording) return;
            Microphone.End(null);
            IsRecording = false;
            if (_clip != null)
            {
                Object.Destroy(_clip);
                _clip = null;
            }
        }
    }
}
