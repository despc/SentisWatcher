using System;
using System.Diagnostics;

namespace SentisWatcher
{
    /// <summary>
    /// The plugin's own time on the game thread: average and worst frame since the last report. A frame in
    /// which the garbage collector ran is counted apart: its time is mostly the collector's, which any
    /// allocation of any code could have triggered.
    /// </summary>
    public sealed class FrameCost
    {
        public static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(10);

        private long _frames, _ticks, _max, _maxWithGc, _gcFrames;
        private int _gcBefore;
        private DateTime _lastReport = DateTime.UtcNow;

        /// <summary>Call before the frame's work.</summary>
        public long Start()
        {
            _gcBefore = GC.CollectionCount(0);
            return Stopwatch.GetTimestamp();
        }

        public void Stop(long started)
        {
            var ticks = Stopwatch.GetTimestamp() - started;
            _frames++;
            _ticks += ticks;
            if (GC.CollectionCount(0) != _gcBefore)
            {
                _gcFrames++;
                if (ticks > _maxWithGc) _maxWithGc = ticks;
            }
            else if (ticks > _max)
            {
                _max = ticks;
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public double AverageMs => _frames == 0 ? 0 : Ms(_ticks) / _frames;
        public double MaxMs => Ms(_max);
        public long Frames => _frames;

        public string Describe() =>
            $"game thread {AverageMs:0.000} ms a frame on average, {MaxMs:0.00} ms at most over {_frames} frames" +
            (_gcFrames > 0 ? $" (a collection ran in {_gcFrames} of them, at most {Ms(_maxWithGc):0.00} ms)" : "");

        /// <summary>True once every <see cref="ReportEvery"/>; the counts then start over.</summary>
        public bool Due(DateTime now)
        {
            if (now - _lastReport < ReportEvery) return false;
            _lastReport = now;
            return true;
        }

        public void Reset()
        {
            _frames = _ticks = _max = _maxWithGc = _gcFrames = 0;
        }
    }
}
