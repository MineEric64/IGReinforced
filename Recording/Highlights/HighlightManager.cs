using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MessagePack;

using IGReinforced.Extensions;
using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video;
using System.Reflection.Emit;

namespace IGReinforced.Recording.Highlights
{
    public class HighlightManager
    {
        public static List<string> HighlightPaths { get; private set; } = new List<string>();
        public static Stopwatch Flow { get; private set; } = new Stopwatch();

        public static Highlight FromRescreen()
        {
            List<Buffered> screen = Rescreen.ScreenQueue.ToList();
            List<Buffered> audio = Rescreen.AudioQueue.ToList();

            return new Highlight(screen, audio);
        }

        public static void AddHighlight()
        {
            Flow.Stop();

            string path = GetTempBufferedFile();
            Highlight highlight = FromRescreen();
            byte[] buffer = MessagePackSerializer.Serialize(highlight, BitmapConverter.LZ4_OPTIONS);

            File.WriteAllBytes(path, buffer);
            HighlightPaths.Add(path);

            Rescreen.ClearAllBuffer();
            highlight.ScreenBuffers.Clear();
            highlight.AudioBuffers.Clear();

            Flow.Restart();
        }

        private static string GetTempBufferedFile()
        {
            return $"{Path.GetTempPath()}{Guid.NewGuid()}.buf";
        }
    }
}
