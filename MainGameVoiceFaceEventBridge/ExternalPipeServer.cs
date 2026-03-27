using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace MainGameVoiceFaceEventBridge
{
    internal sealed class ExternalPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Action<string> _onLine;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logError;

        private Thread _worker;
        private volatile bool _stopRequested;

        internal ExternalPipeServer(
            string pipeName,
            Action<string> onLine,
            Action<string> logInfo,
            Action<string> logWarn,
            Action<string> logError)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "kks_voice_face_events" : pipeName.Trim();
            _onLine = onLine;
            _logInfo = logInfo;
            _logWarn = logWarn;
            _logError = logError;
        }

        internal bool IsForPipe(string pipeName)
        {
            return string.Equals(_pipeName, pipeName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        internal void Start()
        {
            if (_worker != null)
            {
                return;
            }

            _stopRequested = false;
            _worker = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "MainGameVoiceFaceEventBridge.PipeServer"
            };
            _worker.Start();
        }

        internal void Stop()
        {
            if (_worker == null)
            {
                return;
            }

            _stopRequested = true;
            WakeServerWait();

            if (!_worker.Join(2000))
            {
                _logWarn?.Invoke("[pipe] worker stop timeout");
            }

            _worker = null;
        }

        private void RunLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        if (_stopRequested)
                        {
                            return;
                        }

                        _logInfo?.Invoke("[pipe] connected");

                        using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true))
                        {
                            while (!_stopRequested && server.IsConnected)
                            {
                                string line = reader.ReadLine();
                                if (line == null)
                                {
                                    break;
                                }

                                _onLine?.Invoke(line);
                            }
                        }

                        _logInfo?.Invoke("[pipe] disconnected");
                    }
                }
                catch (IOException ex)
                {
                    if (_stopRequested)
                    {
                        return;
                    }

                    _logWarn?.Invoke("[pipe] io error: " + ex.Message);
                    Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    if (_stopRequested)
                    {
                        return;
                    }

                    _logError?.Invoke("[pipe] worker error: " + ex.Message);
                    Thread.Sleep(300);
                }
            }
        }

        private void WakeServerWait()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    client.Connect(100);
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
