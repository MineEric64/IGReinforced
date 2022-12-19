using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MessagePack;

using IGReinforced.Recording.Types;

namespace IGReinforced.Recording.Highlights
{
    [MessagePackObject]
    public class Highlight
    {
        [Key(0)]
        public List<Buffered> ScreenBuffers { get; set; }
        [Key(1)]
        public List<Buffered> AudioBuffers { get; set; }
        [Key(2)]
        public List<Buffered> MicBuffers { get; set; }

        [Key(3)]
        public string GameName { get; set; }

        public Highlight(List<Buffered> screenBuffers, List<Buffered> audioBuffers, List<Buffered> micBuffers, string gameName = "Desktop")
        {
            ScreenBuffers = screenBuffers;
            AudioBuffers = audioBuffers;
            MicBuffers = micBuffers;
            GameName = gameName;
        }
    }
}
