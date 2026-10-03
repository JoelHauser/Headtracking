using System.Threading;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.App.Sources
{
    /// <summary>
    /// OpenTrack (or anything else speaking its "UDP over network" output) as a source: 48-byte
    /// datagrams of x, y, z, yaw, pitch, roll on 127.0.0.1. OpenTrack sends no "face lost" flag,
    /// so loss is judged by the tracker from the pose freezing.
    /// </summary>
    public sealed class OpenTrackSource : ITrackerSource
    {
        private readonly UdpPoseReceiver _receiver;
        private ReceiverStats _lastStats;
        private double _lastStatsTime;
        private double _rate, _distinctRate;

        public OpenTrackSource(int port, ILogSink log)
        {
            _receiver = new UdpPoseReceiver(port, false, log);
        }

        public string Name => "OpenTrack (UDP " + _receiver.Port + ")";

        public WaitHandle NewData => _receiver.NewData;

        public bool Start()
        {
            _lastStatsTime = Clock.Now();
            return _receiver.Start();
        }

        public bool TryGetSnapshot(out PoseSnapshot snapshot)
        {
            return _receiver.TryGetSnapshot(out snapshot);
        }

        public SourceStatus GetStatus()
        {
            double now = Clock.Now();
            ReceiverStats stats = _receiver.GetStats();
            if (now - _lastStatsTime >= 1.0)
            {
                _rate = (stats.Valid - _lastStats.Valid) / (now - _lastStatsTime);
                _distinctRate = (stats.Distinct - _lastStats.Distinct) / (now - _lastStatsTime);
                _lastStats = stats;
                _lastStatsTime = now;
            }

            bool listening = _receiver.IsListening;
            string error = _receiver.BindError;
            if (error == null && listening && stats.Valid == 0)
            {
                error = "Nothing from OpenTrack yet. In OpenTrack set Output to \"UDP over network\", IP 127.0.0.1, port " + _receiver.Port + ", and press Start.";
            }

            return new SourceStatus
            {
                Running = listening,
                Summary = "OpenTrack on UDP " + _receiver.Port + ": " + _rate.ToString("0") + " packets/s, "
                          + _distinctRate.ToString("0") + " new poses/s" + (stats.LastSource != null ? " from " + stats.LastSource : ""),
                Error = error,
                Rate = _distinctRate,
                FaceFound = stats.Valid > 0,
            };
        }

        public void Dispose()
        {
            _receiver.Dispose();
        }
    }
}
