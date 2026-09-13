namespace CoopBots.Kernel.Vendor;

internal sealed class SearchFramePressureSignal
{
    private const int BaselineWindowSize = 31;
    private const int MinimumBaselineSampleCount = 8;
    private const double DefaultBaselineFrameGapMilliseconds = 1000d / 60d;
    private const double MinimumPressureFrameGapMilliseconds = 33d;
    private const double RelativePressureFactor = 1.5d;
    private const double MaximumBaselineSampleMilliseconds = 250d;

    // Only the main thread writes this ring. Workers only observe the epochs below.
    private readonly double[] _idleFrameGapMilliseconds = new double[BaselineWindowSize];
    private int _idleFrameGapCount;
    private int _idleFrameGapWriteIndex;
    private long _frameSequence;
    private int _pressureEpoch;
    private int _recoveryEnabled = 1;
    private int _frameRecoveryAllowed = 1;
    private double _baselineFrameGapMilliseconds = DefaultBaselineFrameGapMilliseconds;
    private double _pressureFrameGapMilliseconds = MinimumPressureFrameGapMilliseconds;

    public double BaselineFrameGapMilliseconds
        => Volatile.Read(ref _baselineFrameGapMilliseconds);

    public double PressureFrameGapMilliseconds
        => Volatile.Read(ref _pressureFrameGapMilliseconds);

    public bool RecoveryEnabled => Volatile.Read(ref _recoveryEnabled) != 0;
    public bool FrameRecoveryAllowed => Volatile.Read(ref _frameRecoveryAllowed) != 0;

    public int BaselineSampleCount => _idleFrameGapCount;
    internal int PressureEpochForTesting => Volatile.Read(ref _pressureEpoch);

    public void ResetPressure(bool recoveryEnabled = true)
    {
        double baseline = EstimateIdleBaseline();
        Volatile.Write(ref _baselineFrameGapMilliseconds, baseline);
        Volatile.Write(
            ref _pressureFrameGapMilliseconds,
            Math.Max(MinimumPressureFrameGapMilliseconds, baseline * RelativePressureFactor));
        Volatile.Write(ref _pressureEpoch, 0);
        Volatile.Write(ref _recoveryEnabled, recoveryEnabled ? 1 : 0);
        Volatile.Write(ref _frameRecoveryAllowed, recoveryEnabled ? 1 : 0);
    }

    public void ObserveFrame(
        double milliseconds,
        bool searchActive,
        bool frameRecoveryAllowed = true)
    {
        Interlocked.Increment(ref _frameSequence);
        Volatile.Write(ref _frameRecoveryAllowed, frameRecoveryAllowed ? 1 : 0);
        if (!frameRecoveryAllowed)
            return;
        if (!searchActive)
        {
            RecordIdleFrame(milliseconds);
            return;
        }
        if (Volatile.Read(ref _recoveryEnabled) != 0
            && milliseconds >= Volatile.Read(ref _pressureFrameGapMilliseconds))
            Interlocked.Increment(ref _pressureEpoch);
    }

    public bool WaitForRecovery(ref int observedPressureEpoch)
    {
        if (Volatile.Read(ref _recoveryEnabled) == 0
            || Volatile.Read(ref _frameRecoveryAllowed) == 0)
        {
            // Consume any pressure raised before the window lost focus. Otherwise every
            // persistent worker would pay one stale recovery wait while the game is
            // intentionally background-capped.
            observedPressureEpoch = Volatile.Read(ref _pressureEpoch);
            return false;
        }
        int pressureEpoch = Volatile.Read(ref _pressureEpoch);
        if (pressureEpoch == observedPressureEpoch)
            return false;
        observedPressureEpoch = pressureEpoch;
        long frameSequence = Volatile.Read(ref _frameSequence);
        SpinWait.SpinUntil(
            () => Volatile.Read(ref _frameSequence) != frameSequence,
            millisecondsTimeout: 25);
        return true;
    }

    private void RecordIdleFrame(double milliseconds)
    {
        if (!double.IsFinite(milliseconds)
            || milliseconds <= 0d
            || milliseconds > MaximumBaselineSampleMilliseconds)
        {
            return;
        }
        _idleFrameGapMilliseconds[_idleFrameGapWriteIndex] = milliseconds;
        _idleFrameGapWriteIndex = (_idleFrameGapWriteIndex + 1) % BaselineWindowSize;
        if (_idleFrameGapCount < BaselineWindowSize)
            _idleFrameGapCount++;
    }

    private double EstimateIdleBaseline()
    {
        if (_idleFrameGapCount < MinimumBaselineSampleCount)
            return DefaultBaselineFrameGapMilliseconds;
        Span<double> samples = stackalloc double[BaselineWindowSize];
        _idleFrameGapMilliseconds.AsSpan(0, _idleFrameGapCount).CopyTo(samples);
        Span<double> observed = samples[.._idleFrameGapCount];
        observed.Sort();
        return observed[observed.Length / 2];
    }
}
