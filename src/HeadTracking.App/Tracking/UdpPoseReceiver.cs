using System;
using HeadTracking.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HeadTracking.Tracking
{
    /// <summary>The newest pose and when it arrived, copied out under the receiver's lock.</summary>
    public struct PoseSnapshot
    {
        public bool HasPose;
        public Pose Pose;

        /// <summary><see cref="Clock.Now"/> when the newest valid packet arrived.</summary>
        public double ArrivalTime;

        /// <summary>
        /// <see cref="Clock.Now"/> when a packet last carried a pose different from the one before
        /// it. OpenTrack keeps sending at about 250 Hz whatever the tracker does, and resends its
        /// last value unchanged when the face is lost, so this going stale is how loss shows up.
        /// </summary>
        public double LastChangeTime;

        /// <summary>
        /// True when the source says outright whether it can see a face (the built-in webcam
        /// tracker does). Then <see cref="Valid"/> is believed and the frozen-pose guess is not
        /// needed. OpenTrack's UDP output carries no such flag, so it stays false there.
        /// </summary>
        public bool ReportsValidity;
        public bool Valid;

        /// <summary>
        /// The source's own one-sigma uncertainty of this pose's rotation, in degrees; 0 when it
        /// does not say (OpenTrack). Scales the tracker's steadiness filter.
        /// </summary>
        public double RotationSigma;
    }

    public struct ReceiverStats
    {
        public long Packets;
        public long Valid;
        public long Distinct;
        public long TooShort;
        public long Oversized;
        public long NotFinite;
        public long OutOfRange;
        public string LastSource;

        public long Bad => TooShort + NotFinite + OutOfRange;
    }

    /// <summary>
    /// Listens for OpenTrack's UDP output on its own background thread. Never touches Unity. The
    /// main thread reads the newest pose with <see cref="TryGetSnapshot"/>; nothing is buffered,
    /// because only the newest pose is ever wanted.
    /// </summary>
    public sealed class UdpPoseReceiver : IDisposable
    {
        private const int PollMicroseconds = 250 * 1000;

        private readonly ILogSink _log;
        private readonly Func<double> _clock;
        private readonly object _lock = new object();

        private Socket _socket;
        private Thread _thread;
        private volatile bool _stopping;

        private PoseSnapshot _snapshot;
        private long _packets, _valid, _distinct, _tooShort, _oversized, _notFinite, _outOfRange;
        private string _lastSource;

        public int Port { get; }
        public bool AllInterfaces { get; }

        /// <summary>Why the last <see cref="Start"/> failed, for the status line. Null after a success.</summary>
        public string BindError { get; private set; }

        public bool IsListening => _thread != null && _thread.IsAlive && !_stopping;

        /// <summary>Set on every valid pose, so a consumer can wait for data instead of polling.</summary>
        public AutoResetEvent NewData { get; } = new AutoResetEvent(false);

        public UdpPoseReceiver(int port, bool allInterfaces, ILogSink log, Func<double> clock = null)
        {
            Port = port;
            AllInterfaces = allInterfaces;
            _log = log;
            _clock = clock ?? Clock.Now;
        }

        /// <summary>
        /// Binds synchronously, so a port in use is reported to the caller straight away, then
        /// starts the thread. False if the bind failed; <see cref="BindError"/> says why.
        /// </summary>
        public bool Start()
        {
            if (_thread != null)
            {
                throw new InvalidOperationException("Already started.");
            }

            IPAddress address = AllInterfaces ? IPAddress.Any : IPAddress.Loopback;
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    // Without this, Windows lets a second program bind the same port and the two
                    // then split the packets between them, which looks like a tracker dropping
                    // frames. Best effort: the game runs on Unity's Mono, not .NET Framework, and a
                    // runtime that rejects the option must not stop the listener altogether.
                    socket.ExclusiveAddressUse = true;
                }
                catch (Exception e)
                {
                    _log.Log(LogLevel.Info, "Exclusive port use not available on this runtime (" + e.GetType().Name + ": " + e.Message
                                            + "); listening without it.");
                }

                socket.Bind(new IPEndPoint(address, Port));
            }
            catch (Exception e)
            {
                socket?.Close();
                BindError = DescribeBindFailure(e);
                return false;
            }

            BindError = null;
            _socket = socket;
            _stopping = false;
            // The socket is handed to the thread rather than read from _socket there: a Stop()
            // straight after Start() clears _socket before the new thread has run.
            _thread = new Thread(() => RunGuarded(socket))
            {
                IsBackground = true,
                Name = "HeadTracking UDP " + Port,
            };
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            Socket socket = _socket;
            _socket = null;
            try
            {
                socket?.Close();
            }
            catch (Exception)
            {
                // Closing is what wakes the thread; a socket that is already gone is fine.
            }

            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(1000))
            {
                _log.Log(LogLevel.Warning, "The UDP thread did not stop within a second. It is a background thread and will end with the game.");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public bool TryGetSnapshot(out PoseSnapshot snapshot)
        {
            lock (_lock)
            {
                snapshot = _snapshot;
            }

            return snapshot.HasPose;
        }

        public ReceiverStats GetStats()
        {
            return new ReceiverStats
            {
                Packets = Interlocked.Read(ref _packets),
                Valid = Interlocked.Read(ref _valid),
                Distinct = Interlocked.Read(ref _distinct),
                TooShort = Interlocked.Read(ref _tooShort),
                Oversized = Interlocked.Read(ref _oversized),
                NotFinite = Interlocked.Read(ref _notFinite),
                OutOfRange = Interlocked.Read(ref _outOfRange),
                LastSource = Volatile.Read(ref _lastSource),
            };
        }

        /// <summary>
        /// Nothing may escape this thread: an unhandled exception on a background thread ends
        /// the whole process, which here is the game.
        /// </summary>
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
                    _log.Log(LogLevel.Error, "UDP thread failed and stopped; head tracking gets no new poses until the game restarts: " + e);
                }
            }
        }

        private void Run(Socket socket)
        {
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            string lastSource = null;
            int socketErrors = 0;

            _log.Log(LogLevel.Info, "UDP thread started on " + (AllInterfaces ? "0.0.0.0" : "127.0.0.1") + ":" + Port + ".");

            while (!_stopping)
            {
                int length;
                try
                {
                    // Poll rather than a receive timeout: a timeout throws, and on Mono an
                    // exception four times a second for as long as OpenTrack is closed is not free.
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
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset
                                                || e.SocketErrorCode == SocketError.MessageSize)
                {
                    // ConnectionReset is Windows reporting an ICMP "port unreachable" from an
                    // earlier send on this socket; we never send, but it is harmless either way.
                    continue;
                }
                catch (Exception e)
                {
                    if (_stopping)
                    {
                        break;
                    }

                    socketErrors++;
                    if (socketErrors <= 5 || socketErrors % 100 == 0)
                    {
                        _log.Log(LogLevel.Warning, "UDP receive failed (" + socketErrors + " so far): " + e.GetType().Name + ": " + e.Message);
                    }

                    Thread.Sleep(500);
                    continue;
                }

                string source = from.ToString();
                if (source != lastSource)
                {
                    LogNewSource(source, lastSource, length, (IPEndPoint)from);
                    lastSource = source;
                    Volatile.Write(ref _lastSource, source);
                }

                Handle(buffer, length);
            }

            _log.Log(LogLevel.Info, "UDP thread on port " + Port + " stopped.");
        }

        private void LogNewSource(string source, string previous, int length, IPEndPoint endpoint)
        {
            string message = previous == null
                ? "First packet received from " + source + " (" + length + " bytes)."
                : "Packets now come from " + source + " instead of " + previous
                  + ". OpenTrack opens a new sending port each time tracking is started, so this usually means it was restarted.";

            if (!IPAddress.IsLoopback(endpoint.Address))
            {
                message += " That address is not this PC; another machine on the network is sending head poses.";
            }

            _log.Log(LogLevel.Info, message);
        }

        private void Handle(byte[] buffer, int length)
        {
            Interlocked.Increment(ref _packets);
            PacketResult result = OpenTrackPacket.TryParse(buffer, length, out Pose pose);

            switch (result)
            {
                case PacketResult.TooShort:
                    ReportBad(Interlocked.Increment(ref _tooShort), "was " + length + " bytes, shorter than OpenTrack's " + OpenTrackPacket.Size
                              + ". Is something other than OpenTrack's \"UDP over network\" output sending to this port?");
                    return;
                case PacketResult.NotFinite:
                    ReportBad(Interlocked.Increment(ref _notFinite), "held NaN or infinity and was ignored.");
                    return;
                case PacketResult.OutOfRange:
                    ReportBad(Interlocked.Increment(ref _outOfRange), "held an angle beyond 360 degrees or a position beyond 100 m and was ignored.");
                    return;
                case PacketResult.OkOversized:
                    if (Interlocked.Increment(ref _oversized) == 1)
                    {
                        _log.Log(LogLevel.Warning, "Packets are " + length + " bytes, longer than OpenTrack's " + OpenTrackPacket.Size
                                                   + ". Using the first " + OpenTrackPacket.Size + ". Logged once.");
                    }

                    break;
            }

            double now = _clock();
            bool changed;
            lock (_lock)
            {
                changed = !_snapshot.HasPose || !pose.SameBits(_snapshot.Pose);
                if (changed)
                {
                    _snapshot.LastChangeTime = now;
                }

                _snapshot.HasPose = true;
                _snapshot.Pose = pose;
                _snapshot.ArrivalTime = now;
            }

            NewData.Set();
            long valid = Interlocked.Increment(ref _valid);
            if (changed)
            {
                Interlocked.Increment(ref _distinct);
            }

            if (valid == 1)
            {
                _log.Log(LogLevel.Info, "First pose: " + pose + ".");
            }
        }

        private void ReportBad(long countOfThisKind, string what)
        {
            if (countOfThisKind <= 3 || countOfThisKind % 1000 == 0)
            {
                _log.Log(LogLevel.Warning, "Bad packet #" + countOfThisKind + " of this kind " + what);
            }
        }

        private string DescribeBindFailure(Exception e)
        {
            string where = (AllInterfaces ? "0.0.0.0" : "127.0.0.1") + ":" + Port;
            if (e is SocketException se)
            {
                switch (se.SocketErrorCode)
                {
                    case SocketError.AddressAlreadyInUse:
                    case SocketError.AccessDenied:
                        return "UDP port " + Port + " (" + where + ") is already in use by another program. "
                               + "Close that program, or pick another port here and the same one in OpenTrack's UDP output settings. ("
                               + se.SocketErrorCode + ")";
                    case SocketError.AddressNotAvailable:
                        return "Cannot listen on " + where + ": address not available. (" + se.SocketErrorCode + ")";
                }

                return "Cannot listen on " + where + ": " + se.SocketErrorCode + " (" + se.Message + ")";
            }

            return "Cannot listen on " + where + ": " + e.GetType().Name + ": " + e.Message;
        }
    }
}
