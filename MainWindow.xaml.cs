using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using log4net;

using IGReinforced.Extensions;
using IGReinforced.Recording.Highlights;
using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video;

namespace IGReinforced
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private Timer _timer;

        public MainWindow()
        {
            InitializeComponent();

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            AppDomain.CurrentDomain.UnhandledException += MainWindow_UnhandledException;
        }


        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _timer = new Timer(Timer_Tick, null, 0, 100);
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Process.GetCurrentProcess().Kill();
        }

        private void MainWindow_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;

            log.Error("Error occured", ex);
            MessageBox.Show(ex.ToCleanString(), "IGReinforced: Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void replayOnoff_Checked(object sender, RoutedEventArgs e)
        {
            Task.Run(async () =>
            {
                async Task WaitForStopAsync()
                {
                    while (true)
                    {
                        if (!Rescreen.IsRecording) break;
                        await Task.Delay(10);
                    }
                }

                await WaitForStopAsync();
                Rescreen.Start();
            });
        }

        private void replayOnoff_Unchecked(object sender, RoutedEventArgs e)
        {
            Rescreen.Stop();
        }

        private void Timer_Tick(object state)
        {
            Key[] GetKeys()
            {
                List<Key> keyList = new List<Key>();
                string text = Dispatcher.Invoke(() => hotkeyBox.Text);
                string[] texts = text.Split(new string[] { "+" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string s in texts)
                {
                    string str = s.Trim();

                    if (Enum.TryParse(str, true, out Key key))
                    {
                        keyList.Add(key);
                    }
                    else if (s == "Alt")
                    {
                        keyList.Add(Key.LeftAlt);
                    }
                }

                return keyList.ToArray();
            }
            bool IsAllDown(Key[] keys2)
            {
                foreach (Key key in keys2) if (!KeyManager.IsDown(key)) return false;
                return true;
            }

            Key[] keys = GetKeys();

            if (IsAllDown(keys) && keys.Length > 0 && Rescreen.IsRecording)
            {
                int videoLength = (int)HighlightManager.Flow.Elapsed.TotalSeconds;
                string info = Rescreen.GetRecordedInfo();

                HighlightManager.AddHighlight();

                log.Info($"Highlight ({videoLength}s) Recorded. Info : {info}");
                Debug.WriteLine(HighlightManager.HighlightPaths.Last());
            }
        }
    }
}
