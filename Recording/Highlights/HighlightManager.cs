using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MessagePack;

using OpenCvSharp;

using NAudio;
using NAudio.Lame;
using NAudio.Mixer;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using IGReinforced.Extensions;
using IGReinforced.Recording.Audio.Wasapi;
using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video;

using NvDecoder = IGReinforced.Recording.Video.NvPipe.Decoder;
using NvPipeCodec = IGReinforced.Recording.Video.NvPipe.Codec;
using NvPipeCompression = IGReinforced.Recording.Video.NvPipe.Compression;
using NvPipeFormat = IGReinforced.Recording.Video.NvPipe.Format;

namespace IGReinforced.Recording.Highlights
{
    public class HighlightManager
    {
        public static string LocalPath { get; set; } = string.Empty;
        public static string FFmpegExecutablePath { get; set; } = string.Empty;

        public static List<string> TempoaryPaths { get; private set; } = new List<string>();
        public static Stopwatch Flow { get; private set; } = new Stopwatch();

        public static Highlight FromRescreen()
        {
            List<Buffered> screen = Rescreen.ScreenQueue.ToList();
            List<Buffered> audio = Rescreen.AudioQueue.ToList();
            List<Buffered> mic = Rescreen.MicQueue.ToList();
            string gameName = "Desktop"; //need to support

            return new Highlight(screen, audio, mic, gameName);
        }

        public static void AddHighlight()
        {
            Flow.Stop();

            string path = GetTempFile("buf");
            Highlight highlight = FromRescreen();
            byte[] buffer = MessagePackSerializer.Serialize(highlight, BitmapConverter.LZ4_OPTIONS);

            File.WriteAllBytes(path, buffer);
            TempoaryPaths.Add(path);

            Rescreen.ClearAllBuffer();

            highlight.ScreenBuffers.Clear();
            highlight.AudioBuffers.Clear();
            highlight.MicBuffers.Clear();

            Flow.Restart();
        }

        private static string GetTempFile(string extension)
        {
            return $"{Path.GetTempPath()}{Guid.NewGuid()}.{extension}";
        }

        public static void ConvertAllToVideo()
        {
            foreach (string tempoaryPath in TempoaryPaths) ConvertToVideo(tempoaryPath);
            TempoaryPaths.Clear();
        }

        public static string ConvertToVideo(string tempoaryPath)
        {
            if (!File.Exists(tempoaryPath)) return string.Empty;

            byte[] buffer = File.ReadAllBytes(tempoaryPath);
            Highlight highlight = MessagePackSerializer.Deserialize<Highlight>(buffer, BitmapConverter.LZ4_OPTIONS);
            string fileName = GetHighlightName(highlight, "mp4");

            string videoPath = ProcessVideo(highlight);
            string audioPath = ProcessAudio(highlight);
            string outPath = Combine(videoPath, audioPath, fileName);

            File.Delete(videoPath);
            File.Delete(audioPath);
            File.Delete(tempoaryPath);

            highlight.ScreenBuffers.Clear();
            highlight.AudioBuffers.Clear();
            highlight.MicBuffers.Clear();

            return outPath;
        }

        public static string GetHighlightName(Highlight highlight, string extension)
        {
            DateTime date = highlight.ScreenBuffers.LastOrDefault()?.Time ?? DateTime.Now;
            string dateName = date.ToString("yy-MM-dd-HH-mm-ss");

            return $"{highlight.GameName} {dateName}.{extension}";
        }

        private static string ProcessVideo(Highlight highlight)
        {
            string path = GetTempFile("mp4");

            VideoWriter writer = new VideoWriter(path, FourCC.H265, Rescreen.FpsIfUnfixed60, Rescreen.ScreenSize.ToCvSize());
            NvDecoder decoder = new NvDecoder(Rescreen.ScreenSize.Width, Rescreen.ScreenSize.Height, NvPipeCodec.H264, NvPipeFormat.RGBA32); //not support BGRA32 yet

            decoder.onDecoded += (s, e) =>
            {
                IntPtr ptr = e.Item1;
                int size = e.Item2;
                Mat mat = new Mat(decoder.height, decoder.width, MatType.CV_8UC4, ptr);
                Mat mat2 = mat.CvtColor(ColorConversionCodes.RGBA2BGRA);

                writer.Write(mat2);

                mat2.Dispose();
                mat.Dispose();
            };

            foreach (Buffered buffered in highlight.ScreenBuffers)
            {
                byte[] buffer = buffered.Buffer.Decompress();
                GCHandle pinnedArray = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                IntPtr ptr = pinnedArray.AddrOfPinnedObject();

                decoder.Decode(ptr, buffer.Length);
                pinnedArray.Free();
            }

            writer.Release();
            decoder.Close();

            return path;
        }

        private static string ProcessAudio(Highlight highlight)
        {
            string path = GetTempFile("mp3");
            byte[] buffer = MergeAudio(highlight);

            using (var writer = new LameMP3FileWriter(path, WasapiCapture.DeviceOutWaveFormat, 128))
            {
                writer.Write(buffer, 0, buffer.Length);
            }

            return path;
        }

        private static byte[] MergeAudio(Highlight highlight)
        {
            byte[] buffer = new byte[0];

            var bwpIn = new BufferedWaveProvider(WasapiCapture.DeviceInWaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(Rescreen.ReplayLength + 2)
            };
            var bwpOut = new BufferedWaveProvider(WasapiCapture.DeviceOutWaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(Rescreen.ReplayLength + 2)
            };
            var mixer = new MixingSampleProvider(WasapiCapture.DeviceOutWaveFormat)
            {
                ReadFully = true
            };

            void Apply(BufferedWaveProvider bwp, List<Buffered> buffers2)
            {
                foreach (Buffered buffered in buffers2)
                {
                    byte[] buffer2 = buffered.Buffer.Decompress();
                    bwp.AddSamples(buffer2, 0, buffer2.Length);
                }
            }

            Apply(bwpIn, highlight.MicBuffers);
            Apply(bwpOut, highlight.AudioBuffers);

            var volumeIn = new VolumeSampleProvider(bwpIn.ToSampleProvider())
            {
                Volume = 0.5f
            };
            var volumeOut = new VolumeSampleProvider(bwpOut.ToSampleProvider())
            {
                Volume = 0.5f
            };

            var resampledIn = new MediaFoundationResampler(volumeIn.ToWaveProvider(), WasapiCapture.DeviceOutWaveFormat);

            mixer.AddMixerInput(resampledIn);
            mixer.AddMixerInput(volumeOut);

            int bufferLength = Math.Max(bwpIn.BufferedBytes, bwpOut.BufferedBytes);

            if (bufferLength > 0)
            {
                float[] bufferFloated = new float[bufferLength];
                int read = mixer.Read(bufferFloated, 0, bufferLength);

                buffer = new byte[read];
                Buffer.BlockCopy(bufferFloated, 0, buffer, 0, read);
            }

            mixer.RemoveAllMixerInputs();
            resampledIn.Dispose();
            bwpIn.ClearBuffer();
            bwpOut.ClearBuffer();

            return buffer;
        }

        private static string Combine(string inVideoPath, string inAudioPath, string outFileName)
        {
            string outPath = Path.Combine(LocalPath, outFileName);



            return outPath;
        }
    }
}
