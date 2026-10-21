using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace CAIVR.Speech
{
    /// <summary>
    /// Mono samples handed from the network to the audio thread.
    ///
    /// The network side writes whenever a chunk arrives; the audio thread reads whatever it needs
    /// for the next block. If the voice server ever falls behind, the buffer goes quiet and waits
    /// for a small cushion before resuming, which sounds like a short pause rather than crackle.
    /// </summary>
    public sealed class PcmStreamBuffer
    {
        readonly object _lock = new object();
        readonly Queue<float[]> _chunks = new Queue<float[]>();
        readonly int _rebufferSamples;
        int _headOffset;
        int _available;
        bool _finished;
        bool _buffering;

        public int SampleRate { get; }

        public PcmStreamBuffer(int sampleRate, float rebufferSeconds)
        {
            SampleRate = sampleRate;
            _rebufferSamples = Math.Max(1, (int)(sampleRate * rebufferSeconds));
        }

        public float AvailableSeconds
        {
            get { lock (_lock) return (float)_available / SampleRate; }
        }

        /// <summary>No more samples will arrive.</summary>
        public bool Finished
        {
            get { lock (_lock) return _finished; }
        }

        /// <summary>Everything that was ever written has been read.</summary>
        public bool Drained
        {
            get { lock (_lock) return _finished && _available == 0; }
        }

        public void Write(float[] samples)
        {
            if (samples == null || samples.Length == 0) return;
            lock (_lock)
            {
                _chunks.Enqueue(samples);
                _available += samples.Length;
            }
        }

        public void Finish()
        {
            lock (_lock) _finished = true;
        }

        /// <summary>
        /// Fills <paramref name="dst"/> for the audio thread, padding with silence if there is not
        /// enough. Returns the RMS loudness of the real audio delivered, for the mouth to follow.
        /// </summary>
        public float Read(float[] dst)
        {
            var written = 0;

            lock (_lock)
            {
                if (_buffering)
                {
                    if (_available >= _rebufferSamples || _finished) _buffering = false;
                    else
                    {
                        Array.Clear(dst, 0, dst.Length);
                        return 0f;
                    }
                }

                while (written < dst.Length && _chunks.Count > 0)
                {
                    var head = _chunks.Peek();
                    var n = Math.Min(head.Length - _headOffset, dst.Length - written);
                    Array.Copy(head, _headOffset, dst, written, n);
                    written += n;
                    _headOffset += n;
                    _available -= n;

                    if (_headOffset >= head.Length)
                    {
                        _chunks.Dequeue();
                        _headOffset = 0;
                    }
                }

                if (written < dst.Length)
                {
                    Array.Clear(dst, written, dst.Length - written);
                    if (!_finished) _buffering = true;
                }
            }

            if (written == 0) return 0f;

            var sum = 0f;
            for (var i = 0; i < written; i++) sum += dst[i] * dst[i];
            return (float)Math.Sqrt(sum / written);
        }
    }

    /// <summary>
    /// Receives the voice server's stream: an 8 byte header (sample rate and channel count, both
    /// little-endian uint32) followed by signed 16-bit little-endian mono PCM, in whatever sized
    /// pieces the network delivers it. A sample can be split across two pieces; that is handled.
    /// </summary>
    public sealed class PcmStreamDownload : DownloadHandlerScript
    {
        readonly float _rebufferSeconds;
        readonly byte[] _header = new byte[8];
        int _headerBytes;
        byte _carry;
        bool _hasCarry;

        /// <summary>Null until the header has arrived.</summary>
        public PcmStreamBuffer Buffer { get; private set; }

        public long BytesReceived { get; private set; }

        /// <summary>The server sent something that is not the format we asked for.</summary>
        public bool BadFormat { get; private set; }

        public PcmStreamDownload(float rebufferSeconds) : base(new byte[16384])
        {
            _rebufferSeconds = rebufferSeconds;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;
            BytesReceived += dataLength;

            var i = 0;
            while (_headerBytes < _header.Length && i < dataLength) _header[_headerBytes++] = data[i++];
            if (_headerBytes < _header.Length) return true;

            if (Buffer == null)
            {
                var rate = _header[0] | (_header[1] << 8) | (_header[2] << 16) | (_header[3] << 24);
                var channels = _header[4] | (_header[5] << 8) | (_header[6] << 16) | (_header[7] << 24);

                if (rate < 8000 || rate > 96000 || channels != 1)
                {
                    BadFormat = true;
                    return false;
                }

                Buffer = new PcmStreamBuffer(rate, _rebufferSeconds);
            }

            var count = Math.Max(0, (dataLength - i + (_hasCarry ? 1 : 0)) / 2);
            if (count > 0)
            {
                var samples = new float[count];
                var o = 0;

                if (_hasCarry)
                {
                    samples[o++] = (short)(_carry | (data[i++] << 8)) / 32768f;
                    _hasCarry = false;
                }

                while (o < count)
                {
                    samples[o++] = (short)(data[i] | (data[i + 1] << 8)) / 32768f;
                    i += 2;
                }

                Buffer.Write(samples);
            }

            if (i < dataLength)
            {
                _carry = data[i];
                _hasCarry = true;
            }

            return true;
        }

        protected override void CompleteContent()
        {
            Buffer?.Finish();
        }
    }
}
