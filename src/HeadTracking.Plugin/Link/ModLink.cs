using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HeadTracking.Shared;

namespace HeadTracking.Link
{
    /// <summary>
    /// The mod's end of the link to HeadTracking.exe. One background thread receives poses and
    /// settings; the main thread reads the newest of each and sends status and commands back.
    /// Never touches Unity. See <see cref="LinkProtocol"/> for the wire format.
    /// </summary>
    internal sealed class ModLink : IDisposable
    {
        private const int PollMicroseconds = 250 * 1000;

        private readonly ILogSink _log;
        private readonly object _lock = new object();
        private readonly IPEndPoint _app;

        private Socket _socket;
        private Socket _sender;
        private Thread _thread;
        private volatile bool _stopping;

        private PoseMessage _pose;
        private double _poseTime = double.NegativeInfinity;
        private GameSettingsMessage _settings;
        private long _poses, _settingsCount, _foreign;

        public int Port { get; }
        public string BindError { get; private set; }

        public ModLink(int port, int appPort, ILogSink log)
        {
            Port = port;
            _app = new IPEndPoint(IPAddress.Loopback, appPort);
            _log = log;
        }

        public bool Start()
        {
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, Port));
                _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
            catch (Exception e)
            {
                socket?.Close();
                BindError = e is SocketException se && (se.SocketErrorCode == SocketError.AddressAlreadyInUse || se.SocketErrorCode == SocketError.AccessDenied)
                    ? "UDP port " + Port + " is already in use by another program; change 'Mod port' here and in HeadTracking.exe."
                    : "Cannot listen on 127.0.0.1:" + Port + ": " + e.GetType().Name + ": " + e.Message;
                return false;
            }

            BindError = null;
            _socket = socket;
            _thread = new Thread(() => RunGuarded(socket)) { IsBackground = true, Name = "HeadTracking link " + Port };
            _thread.Start();
            return true;
        }

        /// <summary>The newest pose and when it arrived (<see cref="Clock.Now"/>).</summary>
        public bool TryGetPose(out PoseMessage pose, out double arrival)
        {
            lock (_lock)
            {
                pose = _pose;
                arrival = _poseTime;
            }

            return !double.IsNegativeInfinity(arrival);
        }

        /// <summary>The newest settings, or null before the app has sent any.</summary>
        public GameSettingsMessage Settings
        {
            get
            {
                lock (_lock)
                {
                    return _settings;
                }
            }
        }

        public long PosesReceived => Interlocked.Read(ref _poses);

        public void Send(LinkCommand command)
        {
            SendBytes(LinkProtocol.Encode(command));
        }

        public void Send(StatusMessage status)
        {
            SendBytes(LinkProtocol.Encode(status));
        }

        private void SendBytes(byte[] bytes)
        {
            try
            {
                _sender?.SendTo(bytes, _app);
            }
            catch (SocketException)
            {
                // Nobody listening (the app is closed) is normal; the status is simply not seen.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                _socket?.Close();
                _sender?.Close();
            }
            catch (Exception)
            {
            }

            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join(1000);
            }
        }

        /// <summary>Nothing may escape: an unhandled exception on a background thread ends the game.</summary>
        private void RunGuarded(Socket socket)
        {
            try
            {
                Run(socket);
            }
            catch (Exception e)
            {
                if (!_stopping)
                {
                    _log.Log(LogLevel.Error, "Link thread failed and stopped; head tracking gets no new data until the game restarts: " + e);
                }
            }
        }

        private void Run(Socket socket)
        {
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int errors = 0;
            _log.Log(LogLevel.Info, "Link thread listening on 127.0.0.1:" + Port + ".");

            while (!_stopping)
            {
                int length;
                try
                {
                    if (!socket.Poll(PollMicroseconds, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    length = socket.ReceiveFrom(buffer, ref from);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
                {
                    // ConnectionReset: Windows reporting that an earlier status packet found no
                    // listener (the app is closed). Harmless.
                    continue;
                }
                catch (Exception e)
                {
                    if (_stopping)
                    {
                        break;
                    }

                    if (++errors <= 5 || errors % 100 == 0)
                    {
                        _log.Log(LogLevel.Warning, "Link receive failed (" + errors + " so far): " + e.GetType().Name + ": " + e.Message);
                    }

                    Thread.Sleep(500);
                    continue;
                }

                Handle(buffer, length);
            }

            _log.Log(LogLevel.Info, "Link thread on port " + Port + " stopped.");
        }

        private void Handle(byte[] buffer, int length)
        {
            if (!LinkProtocol.TryReadHeader(buffer, length, out LinkMessageType type))
            {
                long foreign = Interlocked.Increment(ref _foreign);
                if (foreign == 1 || foreign % 1000 == 0)
                {
                    _log.Log(LogLevel.Warning, "Ignored " + foreign + " datagram(s) on port " + Port + " that are not from HeadTracking.exe ("
                                               + length + " bytes"
                                               + (length == 48 ? "; that is OpenTrack's format: point OpenTrack at HeadTracking.exe (port 4242), not at the game" : "")
                                               + ").");
                }

                return;
            }

            switch (type)
            {
                case LinkMessageType.Pose:
                    if (LinkProtocol.TryDecode(buffer, length, out PoseMessage pose))
                    {
                        double now = Clock.Now();
                        lock (_lock)
                        {
                            _pose = pose;
                            _poseTime = now;
                        }

                        if (Interlocked.Increment(ref _poses) == 1)
                        {
                            _log.Log(LogLevel.Info, "First pose from HeadTracking.exe: state " + pose.State + ", yaw " + pose.Yaw.ToString("0.0")
                                                    + ", pitch " + pose.Pitch.ToString("0.0") + ", zoom " + pose.Zoom.ToString("0.00") + ".");
                        }
                    }

                    break;

                case LinkMessageType.GameSettings:
                    if (LinkProtocol.TryDecode(buffer, length, out GameSettingsMessage settings))
                    {
                        GameSettingsMessage previous;
                        lock (_lock)
                        {
                            previous = _settings;
                            _settings = settings;
                        }

                        Interlocked.Increment(ref _settingsCount);
                        if (previous == null || previous.Revision != settings.Revision)
                        {
                            _log.Log(LogLevel.Info, "Settings from HeadTracking.exe (revision " + settings.Revision + "): pause while aiming "
                                                    + settings.PauseWhileAiming + ", cursor " + settings.PauseWhenCursorVisible + ", unfocused "
                                                    + settings.PauseWhenUnfocused + ", fade " + settings.PauseFadeMs + " ms, zoom "
                                                    + (settings.ZoomEnabled ? "on, up to " + settings.ZoomMaxFovReduction + " deg" : "off")
                                                    + ", recenter key " + settings.RecenterKey + " (" + settings.RecenterModifiers + ")"
                                                    + ", toggle key " + settings.ToggleKey + " (" + settings.ToggleModifiers + ").");
                        }
                    }

                    break;
            }
        }
    }
}
