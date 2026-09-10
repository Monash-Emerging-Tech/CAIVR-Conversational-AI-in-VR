using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace CAIVR.Speech
{
    /// <summary>
    /// Turns recorded microphone samples into a WAV file in memory.
    ///
    /// Unity can record audio but cannot export it, and every speech API wants a
    /// real audio file rather than a float array, so this gap has to be filled
    /// by hand. 16-bit PCM, which every transcription service accepts.
    /// </summary>
    public static class WavEncoder
    {
        const int BitsPerSample = 16;

        /// <summary>
        /// Encodes <paramref name="sampleCount"/> samples from the front of the
        /// clip. Recording buffers are allocated for the worst case and usually
        /// only partly filled, so encoding the whole clip would append seconds
        /// of silence and slow transcription down for no reason.
        /// </summary>
        public static byte[] Encode(AudioClip clip, int sampleCount)
        {
            if (clip == null) return Array.Empty<byte>();

            sampleCount = Mathf.Clamp(sampleCount, 0, clip.samples);
            if (sampleCount == 0) return Array.Empty<byte>();

            var channels = clip.channels;
            var samples = new float[sampleCount * channels];
            clip.GetData(samples, 0);

            return Encode(samples, channels, clip.frequency);
        }

        public static byte[] Encode(float[] samples, int channels, int frequency)
        {
            using var stream = new MemoryStream(44 + samples.Length * 2);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);

            var dataBytes = samples.Length * (BitsPerSample / 8);
            var byteRate = frequency * channels * (BitsPerSample / 8);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);                                   // PCM header size
            writer.Write((short)1);                             // PCM, uncompressed
            writer.Write((short)channels);
            writer.Write(frequency);
            writer.Write(byteRate);
            writer.Write((short)(channels * (BitsPerSample / 8)));
            writer.Write((short)BitsPerSample);

            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);

            foreach (var sample in samples)
            {
                // Clamp before scaling: a sample slightly over 1.0 would wrap to
                // a large negative value and produce an audible click.
                var clamped = Mathf.Clamp(sample, -1f, 1f);
                writer.Write((short)(clamped * short.MaxValue));
            }

            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>Root-mean-square level of a window, for silence detection.</summary>
        public static float Rms(float[] samples, int start, int count)
        {
            if (samples == null || count <= 0) return 0f;

            start = Mathf.Max(0, start);
            count = Mathf.Min(count, samples.Length - start);
            if (count <= 0) return 0f;

            var sum = 0f;
            for (var i = start; i < start + count; i++) sum += samples[i] * samples[i];

            return Mathf.Sqrt(sum / count);
        }
    }
}
