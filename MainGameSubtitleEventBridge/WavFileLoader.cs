using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace MainGameSubtitleEventBridge
{
    internal static class WavFileLoader
    {
        internal static bool TryLoadClip(string path, out AudioClip clip, out string error)
        {
            clip = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "path is empty";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "file not found";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                error = "read failed: " + ex.Message;
                return false;
            }

            if (!TryParse(bytes, out int channels, out int sampleRate, out float[] pcm, out error))
            {
                return false;
            }

            try
            {
                int sampleCount = pcm.Length / channels;
                if (sampleCount <= 0)
                {
                    error = "empty audio payload";
                    return false;
                }

                clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), sampleCount, channels, sampleRate, false);
                clip.SetData(pcm, 0);
                return true;
            }
            catch (Exception ex)
            {
                error = "clip create failed: " + ex.Message;
                return false;
            }
        }

        private static bool TryParse(
            byte[] data,
            out int channels,
            out int sampleRate,
            out float[] pcm,
            out string error)
        {
            channels = 0;
            sampleRate = 0;
            pcm = null;
            error = string.Empty;

            if (data == null || data.Length < 44)
            {
                error = "invalid wav header length";
                return false;
            }

            if (Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
            {
                error = "missing RIFF";
                return false;
            }

            if (Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
            {
                error = "missing WAVE";
                return false;
            }

            int pos = 12;
            short audioFormat = 0;
            short bitsPerSample = 0;
            int dataOffset = -1;
            int dataSize = 0;

            while (pos + 8 <= data.Length)
            {
                string chunkId = Encoding.ASCII.GetString(data, pos, 4);
                int chunkSize = BitConverter.ToInt32(data, pos + 4);
                pos += 8;

                if (chunkSize < 0 || pos + chunkSize > data.Length)
                {
                    error = "broken chunk: " + chunkId;
                    return false;
                }

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                    {
                        error = "fmt chunk too short";
                        return false;
                    }

                    audioFormat = BitConverter.ToInt16(data, pos + 0);
                    channels = BitConverter.ToInt16(data, pos + 2);
                    sampleRate = BitConverter.ToInt32(data, pos + 4);
                    bitsPerSample = BitConverter.ToInt16(data, pos + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = pos;
                    dataSize = chunkSize;
                    break;
                }

                pos += chunkSize;
                if ((chunkSize & 1) != 0 && pos < data.Length)
                {
                    pos++;
                }
            }

            if (dataOffset < 0 || dataSize <= 0)
            {
                error = "data chunk missing";
                return false;
            }

            if (channels <= 0 || sampleRate <= 0)
            {
                error = "fmt chunk missing or invalid";
                return false;
            }

            if (!TryDecodePcm(data, dataOffset, dataSize, channels, audioFormat, bitsPerSample, out pcm, out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryDecodePcm(
            byte[] data,
            int offset,
            int size,
            int channels,
            short format,
            short bitsPerSample,
            out float[] pcm,
            out string error)
        {
            pcm = null;
            error = string.Empty;

            if (size <= 0)
            {
                error = "empty pcm payload";
                return false;
            }

            if (format == 1 && bitsPerSample == 8)
            {
                int count = size;
                count -= count % channels;
                pcm = new float[count];
                for (int i = 0; i < count; i++)
                {
                    pcm[i] = (data[offset + i] - 128) / 128f;
                }
                return true;
            }

            if (format == 1 && bitsPerSample == 16)
            {
                int count = size / 2;
                count -= count % channels;
                pcm = new float[count];
                for (int i = 0; i < count; i++)
                {
                    pcm[i] = BitConverter.ToInt16(data, offset + (i * 2)) / 32768f;
                }
                return true;
            }

            if (format == 1 && bitsPerSample == 24)
            {
                int count = size / 3;
                count -= count % channels;
                pcm = new float[count];
                for (int i = 0; i < count; i++)
                {
                    int o = offset + (i * 3);
                    int v = data[o] | (data[o + 1] << 8) | (data[o + 2] << 16);
                    if ((v & 0x800000) != 0)
                    {
                        v |= unchecked((int)0xFF000000);
                    }

                    pcm[i] = v / 8388608f;
                }
                return true;
            }

            if (format == 1 && bitsPerSample == 32)
            {
                int count = size / 4;
                count -= count % channels;
                pcm = new float[count];
                for (int i = 0; i < count; i++)
                {
                    pcm[i] = BitConverter.ToInt32(data, offset + (i * 4)) / 2147483648f;
                }
                return true;
            }

            if (format == 3 && bitsPerSample == 32)
            {
                int count = size / 4;
                count -= count % channels;
                pcm = new float[count];
                for (int i = 0; i < count; i++)
                {
                    pcm[i] = BitConverter.ToSingle(data, offset + (i * 4));
                }
                return true;
            }

            error = "unsupported wav format. fmt=" + format + " bits=" + bitsPerSample;
            return false;
        }
    }
}
