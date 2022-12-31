using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using log4net;

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
using IGReinforced.Recording.Video.NvColorSpace;

namespace IGReinforced.Recording.Highlights
{
    public class HighlightManager
    {
        public static string LocalPath { get; set; } = string.Empty;
        public static string FFmpegExecutablePath { get; set; } = string.Empty;

        public static List<string> TempoaryPaths { get; private set; } = new List<string>();
        public static Stopwatch Flow { get; private set; } = new Stopwatch();

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

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

        public static async Task<string> ConvertToVideoAsync(string tempoaryPath)
        {
            if (!File.Exists(tempoaryPath)) return string.Empty;

            byte[] buffer = File.ReadAllBytes(tempoaryPath);
            Highlight highlight = MessagePackSerializer.Deserialize<Highlight>(buffer, BitmapConverter.LZ4_OPTIONS);
            string fileName = GetHighlightName(highlight, "mp4");

            string videoPath = await Task.Run(() => ProcessVideo(highlight));
            string audioPath = await ProcessAudioAsync(highlight);
            string outPath = await CombineAsync(videoPath, audioPath, fileName);

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
            Mat ConvertLegacy(IntPtr ptr, int size, int width, int height)
            {
                Mat mat = new Mat(height, width, MatType.CV_8UC4, ptr);
                Mat mat2 = mat.CvtColor(ColorConversionCodes.RGBA2BGRA);

                mat.Dispose();
                return mat2;
            }
            Mat ConvertNvColorSpace(IntPtr ptr, int size, int width, int height, out IntPtr bgra)
            {
                bgra = Marshal.AllocHGlobal(size);
                int status = NvColorSpace.RGBA32ToBGRA32(ptr, bgra, width, height);
                Mat mat = new Mat(height, width, MatType.CV_8UC4, bgra);

                return mat;
            }

            string path = GetTempFile("mp4");

            VideoWriter writer = new VideoWriter(path, FourCC.H265, Rescreen.FpsIfUnfixed60, Rescreen.ScreenSize.ToCvSize());

            Rescreen.IsSaving = true;
            Rescreen.OnDecoded = (e) =>
            {
                Mat mat = ConvertNvColorSpace(e.Item1, e.Item2, Rescreen.Decoder.width, Rescreen.Decoder.height, out IntPtr bgra);

                writer.Write(mat);
                Marshal.FreeHGlobal(bgra);
                mat.Dispose();
            };

            foreach (Buffered buffered in highlight.ScreenBuffers)
            {
                byte[] buffer = buffered.Buffer.Decompress();
                GCHandle pinnedArray = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                IntPtr ptr = pinnedArray.AddrOfPinnedObject();

                Rescreen.Decoder.Decode(ptr, buffer.Length);
                pinnedArray.Free();
            }

            writer.Release();
            Rescreen.OnDecoded = null;
            Rescreen.IsSaving = false;

            return path;
        }

        private static async Task<string> ProcessAudioAsync(Highlight highlight)
        {
            string path = GetTempFile("mp3");
            byte[] buffer = MergeAudio(highlight);

            using (var writer = new LameMP3FileWriter(path, WasapiCapture.DeviceOutWaveFormat, 128))
            {
                await writer.WriteAsync(buffer, 0, buffer.Length);
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

        private static async Task<string> CombineAsync(string inVideoPath, string inAudioPath, string outFileName)
        {
            string outPath = Path.Combine(LocalPath, outFileName);
            string args = $"-i \"{inVideoPath}\" -i \"{inAudioPath}\" -preset ultrafast -tune fastdecode -shortest \"{outPath}\"";
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "ffmpeg.exe",
                WorkingDirectory = FFmpegExecutablePath,
                Arguments = args
            };

            if (File.Exists(outPath))
            {
                File.Delete(outPath);
                log.Info($"{outFileName} has overwritten because of same file name.");
            }
            if (!Directory.Exists(LocalPath))
            {
                Directory.CreateDirectory(LocalPath);

                var info = new DirectoryInfo(LocalPath);
                log.Info($"{info.Name} Directory has created.");
            }

            using (Process process = Process.Start(startInfo))
            {
                await process.WaitForExitAsync();
            }

            return outPath;
        }
    }
}
