using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace IGReinforced.Extensions
{
    public class KeyManager
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static bool IsDown(Key key)
        {
            int vKey = KeyInterop.VirtualKeyFromKey(key);
            short result = GetAsyncKeyState(vKey);
            //byte[] buffer = BitConverter.GetBytes(result);

            //buffer[0] == 1 : The key was pressed after the previous call to GetAsyncKeyState
            //buffer[1] == 0x80 : The key is down

            return result != 0;
        }
    }
}
