using System.IO;
using System.Text;
using UnityEngine;

namespace MeshPresso
{
    /// <summary>Encodes raw float samples into a 16-bit PCM RIFF/WAV byte array.</summary>
    public static class WavUtility
    {
        public static byte[] FromSamples(float[] samples, int sampleRate, int channels)
        {
            int byteCount = samples.Length * 2;
            using (var stream = new MemoryStream(44 + byteCount))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + byteCount);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2); // byte rate
                writer.Write((short)(channels * 2));     // block align
                writer.Write((short)16);                 // bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(byteCount);

                for (int i = 0; i < samples.Length; i++)
                {
                    short value = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
                    writer.Write(value);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
