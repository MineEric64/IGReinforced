using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MessagePack;

using IGReinforced.Extensions;

namespace IGReinforced.Recording.Types
{
    [MessagePackObject]
    public class Buffered
    {
        [Key(0)]
        public byte[] Buffer { get; set; }
        [Key(1)]
        public DateTime Time { get; set; }

        public Buffered(byte[] buffer)
        {
            Buffer = buffer;
            Time = DateTime.Now;
        }

        public Buffered(byte[] buffer, DateTime time)
        {
            Buffer = buffer;
            Time = DateTime.Now;
        }
    }
}
