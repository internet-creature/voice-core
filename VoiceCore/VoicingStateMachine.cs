using System.Numerics;

namespace VoiceCore;

/// <summary>What one frame's own evidence says, before hysteresis (spec §3.3 step 3).</summary>
internal enum VoicingEvidence : byte
{
    Silence,
    Unvoiced,
    Voiced,

    /// <summary>Voiced via the breathy path: moderate aperiodicity, STABLE candidate.</summary>
    Breathy,

    /// <summary>Creak candidate that doesn't yet have enough votes: published as Unvoiced.</summary>
    CreakCandidate,

    /// <summary>Creak candidate with ≥ 3 of the last 5 non-silent frames agreeing.</summary>
    Creak,
}

/// <summary>The published decision for one frame.</summary>
/// <param name="Holding">Contrary evidence, but hysteresis kept the previous state.</param>
internal readonly record struct VoicingDecision(VoicingState State, VoicingEvidence Evidence, bool Stable, bool Holding, float Confidence);

/// <summary>
/// The §3.3 voicing decision, in order: level gate, classify on
/// {aperiodicity, f0 stability, ZCR, temporal evidence}, multi-frame creak call,
/// then hysteresis. Stateful across frames; <see cref="Reset"/> on any gap.
/// Allocation-free after construction.
/// </summary>
internal sealed class VoicingStateMachine
{
    private const int StableHistory = 3;

    private readonly AnalysisConfig _config;
    private readonly float[] _recentF0 = new float[StableHistory];     // previous non-silent candidates
    private readonly bool[] _recentInRange = new bool[StableHistory];
    private readonly float[] _median = new float[StableHistory];
    private int _recentCount;
    private int _recentNext;
    private uint _creakVotes;   // bit i = the i-th most recent non-silent frame was a creak candidate
    private int _creakSeen;
    private VoicingState _state = VoicingState.Silence;
    private int _contrary;

    public VoicingStateMachine(AnalysisConfig config) => _config = config;

    public void Reset()
    {
        _recentCount = _recentNext = 0;
        _creakVotes = 0;
        _creakSeen = 0;
        _state = VoicingState.Silence;
        _contrary = 0;
    }

    public VoicingState State => _state;

    /// <summary>True while leaving Voiced or Creak is still pending: the caller must supply a candidate even for a quiet frame.</summary>
    public bool NeedsCandidateWhenQuiet => _state is VoicingState.Voiced or VoicingState.Creak;

    /// <param name="hasEnergy">Passed the level gate.</param>
    /// <param name="candidate">This frame's f0 candidate; required when <paramref name="hasEnergy"/>.</param>
    /// <param name="zcrPerSecond">Zero-crossing rate over the frame's hop.</param>
    /// <param name="levelAboveGateDb">RMS minus the level gate, in dB (negative when below).</param>
    public VoicingDecision Decide(bool hasEnergy, F0Candidate? candidate, float zcrPerSecond, float levelAboveGateDb)
    {
        var evidence = VoicingEvidence.Silence;
        bool stable = false;
        if (hasEnergy)
        {
            var c = candidate ?? throw new ArgumentException("A frame with energy needs a candidate.", nameof(candidate));
            stable = IsStable(c);
            evidence = Classify(c, stable, zcrPerSecond);
            Remember(c, evidence is VoicingEvidence.CreakCandidate or VoicingEvidence.Creak);
            if (evidence == VoicingEvidence.CreakCandidate && CreakVotes() >= _config.CreakVotesRequired)
                evidence = VoicingEvidence.Creak;
        }

        var proposed = evidence switch
        {
            VoicingEvidence.Voiced or VoicingEvidence.Breathy => VoicingState.Voiced,
            VoicingEvidence.Creak => VoicingState.Creak,
            VoicingEvidence.Silence => VoicingState.Silence,
            _ => VoicingState.Unvoiced,  // including a creak candidate without enough votes
        };

        bool holding = false;
        if (_state is VoicingState.Voiced or VoicingState.Creak && proposed != _state)
        {
            if (++_contrary < _config.HysteresisFrames)
                holding = true;
            else
            {
                _state = proposed;
                _contrary = 0;
            }
        }
        else
        {
            _state = proposed;
            _contrary = 0;
        }

        return new VoicingDecision(_state, evidence, stable, holding, Confidence(evidence, candidate, holding, levelAboveGateDb));
    }

    private VoicingEvidence Classify(F0Candidate c, bool stable, float zcrPerSecond)
    {
        if (c.Aperiodicity < _config.VoicedAperiodicityMax)
            return VoicingEvidence.Voiced;
        if (c.Aperiodicity < _config.BreathyAperiodicityMax)
        {
            if (stable)
                return VoicingEvidence.Breathy;
            if (zcrPerSecond < _config.LowZcrPerSecond)
                return VoicingEvidence.CreakCandidate;
        }
        return VoicingEvidence.Unvoiced;
    }

    /// <summary>
    /// STABLE (§3.3): within ±50 cents of the median of the previous 3 non-silent
    /// candidates, all of them (and this one) inside the search range.
    /// </summary>
    private bool IsStable(F0Candidate c)
    {
        if (_recentCount < StableHistory || c.Range != F0Range.In)
            return false;
        for (int i = 0; i < StableHistory; i++)
        {
            if (!_recentInRange[i])
                return false;
            _median[i] = _recentF0[i];
        }
        // median of three, in place
        if (_median[0] > _median[1]) (_median[0], _median[1]) = (_median[1], _median[0]);
        if (_median[1] > _median[2]) (_median[1], _median[2]) = (_median[2], _median[1]);
        if (_median[0] > _median[1]) (_median[0], _median[1]) = (_median[1], _median[0]);
        return Math.Abs(1200 * Math.Log2(c.F0Hz / _median[1])) <= _config.StableCents;
    }

    private void Remember(F0Candidate c, bool creakCandidate)
    {
        _recentF0[_recentNext] = c.F0Hz;
        _recentInRange[_recentNext] = c.Range == F0Range.In;
        _recentNext = (_recentNext + 1) % StableHistory;
        _recentCount = Math.Min(_recentCount + 1, StableHistory);

        _creakVotes = (_creakVotes << 1) | (creakCandidate ? 1u : 0u);
        _creakSeen = Math.Min(_creakSeen + 1, _config.CreakWindowFrames);
    }

    private int CreakVotes()
    {
        uint mask = _config.CreakWindowFrames >= 32 ? uint.MaxValue : (1u << _config.CreakWindowFrames) - 1;
        return BitOperations.PopCount(_creakVotes & mask);
    }

    /// <summary>
    /// Raw voicing confidence (§3.9, uncalibrated): how far the frame's evidence sits
    /// from the threshold that would change it, mapped to 0.5–1; 0.5 while holding.
    /// Calibration to a probability happens at build step 5.
    /// </summary>
    private float Confidence(VoicingEvidence evidence, F0Candidate? candidate, bool holding, float levelAboveGateDb)
    {
        if (holding)
            return 0.5f;
        float ap = candidate?.Aperiodicity ?? 1f;
        float margin = evidence switch
        {
            VoicingEvidence.Silence => -levelAboveGateDb / 10f,
            VoicingEvidence.Voiced => (_config.VoicedAperiodicityMax - ap) / _config.VoicedAperiodicityMax,
            VoicingEvidence.Breathy => Math.Min(ap - _config.VoicedAperiodicityMax, _config.BreathyAperiodicityMax - ap)
                                       / (_config.BreathyAperiodicityMax - _config.VoicedAperiodicityMax) * 2,
            VoicingEvidence.Creak => (CreakVotes() - _config.CreakVotesRequired + 1f) / _config.CreakWindowFrames,
            VoicingEvidence.CreakCandidate => (_config.CreakVotesRequired - CreakVotes()) / (float)_config.CreakWindowFrames,
            _ => (ap - _config.BreathyAperiodicityMax) / (1 - _config.BreathyAperiodicityMax),
        };
        return 0.5f + 0.5f * Math.Clamp(margin, 0f, 1f);
    }
}
