using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video;

namespace IGReinforced.Recording.Highlights
{
    public class HighlightManager
    {
        public static Highlight FromRescreen()
        {
            List<Buffered> screen = Rescreen.ScreenQueue.ToList();
            List<Buffered> audio = Rescreen.AudioQueue.ToList();

            return new Highlight(screen, audio);
        }
    }
}
