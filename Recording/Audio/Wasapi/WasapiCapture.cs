using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

using NAudioWasapiCapture = NAudio.CoreAudioApi.WasapiCapture;

namespace IGReinforced.Recording.Audio.Wasapi
{
    public class WasapiCapture
    {
        //일부 코드는 https://github.com/Luigi38/ProjectReinforced/blob/main/Recording.Audio.cs 에서 가져왔습니다.

        internal static MMDevice DefaultMMDeviceIn //Mic
        {
            get
            {
                try
                {
                    return NAudioWasapiCapture.GetDefaultCaptureDevice();
                }
                catch
                {
                    return null;
                }
            }
        }

        internal static MMDevice DefaultMMDeviceOut //Speaker
        {
            get
            {
                try
                {
                    return WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
                }
                catch
                {
                    Debug.WriteLine("[Error] Can't get DefaultMMDevice");
                    return null;
                }
            }
        }

        internal static WaveFormat DeviceInWaveFormat => DefaultMMDeviceIn?.AudioClient?.MixFormat?.AsStandardWaveFormat();
        internal static WaveFormat DeviceOutWaveFormat => DefaultMMDeviceOut?.AudioClient?.MixFormat?.AsStandardWaveFormat();
        internal static WaveFormat WaveFormat => _capture?.WaveFormat;

        /// <summary>
        /// 소리 녹음용 (스피커)
        /// </summary>
        private static WasapiLoopbackCapture _capture;
        /// <summary>
        /// 소리 녹음용 (마이크)
        /// </summary>
        private static NAudioWasapiCapture _captureMic;

        private static string _prevDeviceInId = string.Empty;
        private static string _prevDeviceOutId = string.Empty;

        /// <summary>
        /// DataAvailable에서 소리가 없을 경우 사용
        /// </summary>
        private static Stopwatch _sw = new Stopwatch();
        private static Stopwatch _swMic = new Stopwatch();

        public static event EventHandler<byte[]> AudioDataAvailable;
        public static event EventHandler<byte[]> MicDataAvailable;

        public static bool IsInitialized { get; private set; } = false;

        /// <summary>
        /// 입력 캡처 장치를 초기화 합니다.
        /// </summary>
        public static void InitializeIn()
        {
            MMDevice device = DefaultMMDeviceIn;

            if (device != null)
            {
                _captureMic = new NAudioWasapiCapture(device);
                _prevDeviceInId = device.ID;
            }

            IsInitialized = true;
        }

        /// <summary>
        /// 출력 캡처 장치를 초기화 합니다.
        /// </summary>
        public static void InitializeOut()
        {
            MMDevice device = DefaultMMDeviceOut;

            if (device != null)
            {
                _capture = new WasapiLoopbackCapture(device);
                _prevDeviceInId = device.ID;
            }

            IsInitialized = true;
        }

        public static bool Record()
        {
            MMDevice deviceIn = DefaultMMDeviceIn;
            MMDevice deviceOut = DefaultMMDeviceOut;

            if (!IsInitialized || deviceOut == null)
            {
                return false;
            }

            if (deviceIn != null && _prevDeviceInId != deviceIn.ID)
            {
                InitializeIn();
            }
            if (_prevDeviceOutId != deviceOut.ID)
            {
                InitializeOut();
            }

            _capture.DataAvailable += WhenAudioDataAvailable;
            if (_captureMic != null) _captureMic.DataAvailable += WhenMicDataAvailable;

            _capture.StartRecording();
            _captureMic?.StartRecording();

            _sw.Start();
            _swMic.Start();

            return true;
        }

        public static void Stop()
        {
            if (!IsInitialized) return;

            _capture.StopRecording();
            _captureMic?.StopRecording();

            _capture.DataAvailable -= WhenAudioDataAvailable;
            if (_captureMic != null) _captureMic.DataAvailable -= WhenMicDataAvailable;

            _capture.Dispose();
            _captureMic?.Dispose();
            
            IsInitialized = false;

            _sw.Reset();
            _swMic.Reset();
        }

        private static byte[] ProcessBuffer(WaveInEventArgs e, int milliseconds)
        {
            byte[] buffer = new byte[e.BytesRecorded];

            if (e.BytesRecorded == 0)
            {
                int bytesRecorded = ConvertLatencyToByteSize(milliseconds);

                buffer = new byte[bytesRecorded];
            }
            else
            {
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
            }

            return buffer;
        }

        private static void WhenAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            _sw.Stop();

            int milliseconds = (int)_sw.ElapsedMilliseconds;
            byte[] buffer = ProcessBuffer(e, milliseconds);

            AudioDataAvailable?.Invoke(sender, buffer);

            _sw.Restart();
        }

        private static void WhenMicDataAvailable(object sender, WaveInEventArgs e)
        {
            _swMic.Stop();

            int milliseconds = (int)_swMic.ElapsedMilliseconds;
            byte[] buffer = ProcessBuffer(e, milliseconds);

            MicDataAvailable?.Invoke(sender, buffer);

            _swMic.Restart();
        }

        public static int ConvertLatencyToByteSize(int milliseconds)
        {
            WaveFormat wf;

            wf = DeviceOutWaveFormat;
            //wf = WaveFormat;

            //return wf.ConvertLatencyToByteSize(milliseconds);

            int bytesPerMillisecond = wf.AverageBytesPerSecond / 1000;
            int bytesRecorded = milliseconds * bytesPerMillisecond;

            return bytesRecorded;
        }

        /// <summary>
        /// Byte[] Size -> Latency (Milliseconds)
        /// </summary>
        /// <param name="size">array's size (length)</param>
        /// <returns></returns>
        public static int ConvertByteSizeToLatency(int size)
        {
            return 1000 * size / DeviceOutWaveFormat.AverageBytesPerSecond;
        }
    }
}
