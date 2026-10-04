using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HeadTracking.App
{
    /// <summary>
    /// The app's own CPU use as a share of one core, for the status line.
    /// Windows' usual figure (TotalProcessorTime) is charged at its 15.6 ms clock tick: work that
    /// wakes on a timer and finishes before the next tick goes largely uncounted, so the old
    /// status line could be far off. Cycle counts are exact; the processor's rated clock (which
    /// is what the cycle counter runs at) turns them into time. Falls back to the tick figure
    /// where the rated clock cannot be read.
    /// </summary>
    internal sealed class CpuMeter
    {
        [DllImport("kernel32.dll")]
        private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private readonly double _cyclesPerSecond;
        private ulong _lastCycles;
        private TimeSpan _lastCpu;
        private double _lastTime;

        public CpuMeter(double now)
        {
            _cyclesPerSecond = RatedHz();
            _lastTime = now;
            _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
            QueryProcessCycleTime(GetCurrentProcess(), out _lastCycles);
        }

        public bool CycleAccurate => _cyclesPerSecond > 0;

        /// <summary>Percent of one core used since the last call.</summary>
        public double Percent(double now)
        {
            double span = Math.Max(0.001, now - _lastTime);
            _lastTime = now;
            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
            double byTicks = (cpu - _lastCpu).TotalSeconds / span * 100.0;
            _lastCpu = cpu;

            if (_cyclesPerSecond > 0 && QueryProcessCycleTime(GetCurrentProcess(), out ulong cycles))
            {
                double used = (cycles - _lastCycles) / _cyclesPerSecond;
                _lastCycles = cycles;
                return used / span * 100.0;
            }

            return byTicks;
        }

        private static double RatedHz()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    return key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz * 1e6 : 0;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
