using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using HeadTracking.Shared;

namespace HeadTracking.App
{
    /// <summary>
    /// The app's end of the link to the BepInEx plugin (see <see cref="LinkProtocol"/>). Sends
    /// poses and settings to the game's port; a background thread receives the plugin's status
    /// and its hotkey commands on the status port.
    /// </summary>
    public sealed class GameLink : IDisposable
    {
        private const int PollMicroseconds = 250 * 1000;

        private readonly ILogSink _log;
        private readonly IPEndPoint _game;
        private readonly Socket _sender;
        private readonly object _lock = new object();
        private readonly ConcurrentQueue<LinkCommand> _commands = new ConcurrentQueue<LinkCommand>();

        private Socket _receiver;
        private Thread _thread;
        private volatile bool _stopping;
        private StatusMessage _status;
        private double _statusTime = double.NegativeInfinity;
        private long _posesSent;

        public int GamePort { get; }
        public int StatusPort { get; }
        public string BindError { get; private set; }

        public GameLink(int gamePort, int statusPort, ILogSink log)
        {
            GamePort = gamePort;
            StatusPort = statusPort;
            _log = log;
            _game = new IPEndPoint(IPAddress.Loopback, gamePort);
            _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        public long PosesSent => Interlocked.Read(ref _posesSent);

        public bool Start()
        {
            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, StatusPort));
                _receiver = socket;
            }
            catch (SocketException e)
            {
                BindError = e.SocketErrorCode == SocketError.AddressAlreadyInUse || e.SocketErrorCode == SocketError.AccessDenied
                    ? "UDP port " + StatusPort + " is in use. Is HeadTracking.exe already running?"
                    : "Cannot listen on 127.0.0.1:" + StatusPort + ": " + e.Message;
                _log.Log(LogLevel.Error, BindError);
                return false;
            }

            _thread = new Thread(RunGuarded) { IsBackground = true, Name = "Game link" };
            _thread.Start();
            _log.Log(LogLevel.Info, "Game link: poses to 127.0.0.1:" + GamePort + ", listening for the game on 127.0.0.1:" + StatusPort + ".");
            return true;
        }

        public void Send(PoseMessage pose)
        {
            SendBytes(LinkProtocol.Encode(pose));
            Interlocked.Increment(ref _posesSent);
        }

        public void Send(GameSettingsMessage settings)
        {
            SendBytes(LinkProtocol.Encode(settings));
        }

        public bool TryTakeCommand(out LinkCommand command)
        {
            return _commands.TryDequeue(out command);
        }

        /// <summary>The newest status from the game and when it arrived.</summary>
        public bool TryGetStatus(out StatusMessage status, out double arrival)
        {
            lock (_lock)
            {
                status = _status;
                arrival = _statusTime;
            }

            return !double.IsNegativeInfinity(arrival);
        }

        private void SendBytes(byte[] bytes)
        {
            try
            {
                _sender.SendTo(bytes, _game);
            }
            catch (SocketException)
            {
                // No game listening: normal while the game is closed.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void RunGuarded()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                if (!_stopping)
                {
                    _log.Log(LogLevel.Error, "Game link thread failed: " + e);
                }
            }
        }

        private void Run()
        {
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            bool seenGame = false;
            while (!_stopping)
            {
                int length;
                try
                {
                    if (!_receiver.Poll(PollMicroseconds, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    length = _receiver.ReceiveFrom(buffer, ref from);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.MessageSize)
                {
                    continue;
                }

                if (!LinkProtocol.TryReadHeader(buffer, length, out LinkMessageType type))
                {
                    continue;
                }

                if (type == LinkMessageType.Status && LinkProtocol.TryDecode(buffer, length, out StatusMessage status))
                {
                    lock (_lock)
                    {
                        _status = status;
                        _statusTime = Clock.Now();
                    }

                    if (!seenGame)
                    {
                        seenGame = true;
                        _log.Log(LogLevel.Info, "The game is connected: Head Tracking plugin " + status.ModVersion + ".");
                    }
                }
                else if (type == LinkMessageType.Command && LinkProtocol.TryDecode(buffer, length, out LinkCommand command))
                {
                    _commands.Enqueue(command);
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                _receiver?.Close();
                _sender.Close();
            }
            catch (Exception)
            {
            }

            _thread?.Join(1000);
        }
    }
}
