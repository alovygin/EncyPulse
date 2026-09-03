using System;
namespace EncyPulse.Core;

/// <summary>
/// ENCY has no "simulation finished" event. This watches the count of operations whose machining
/// result exists while ENCY is in simulation mode: when the count grows and then stops changing for
/// the quiet window, one simulation run has ended.
///
/// A toolpath calculation also produces machining results, so a run during which Calculated flags
/// changed is "tainted" and reported as nothing: the batch logic covers it.
/// Drive it from one thread (the poll timer).
/// </summary>
public sealed class SimulationDetector
{
    private readonly Func<TimeSpan> _quiet;
    private bool _active;
    private bool _tainted;
    private int _last = -1;
    private int _baseline;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastChange;

    public SimulationDetector(Func<TimeSpan> quiet) => _quiet = quiet;

    public bool IsActive => _active;

    /// <summary>Set when the last emit was skipped because a calculation caused the change.</summary>
    public string? LastSkipReason { get; private set; }

    /// <param name="simulating">ENCY is in simulation work mode.</param>
    /// <param name="simulated">Enabled leaf operations with a machining result.</param>
    /// <param name="total">Enabled leaf operations in the project.</param>
    /// <param name="errors">Simulated operations flagged with an error.</param>
    /// <param name="calculationActivity">Calculated/HasToolpath flags changed since the previous sample.</param>
    public SimulationResult? Sample(bool simulating, int simulated, int total, int errors, string projectName,
        DateTimeOffset now, bool calculationActivity = false)
    {
        LastSkipReason = null;
        if (calculationActivity) _tainted = true;

        if (!simulating)
        {
            // Mode left while a run was in progress: report what was simulated so far.
            SimulationResult? r = null;
            if (_active) r = Emit(_last, Math.Max(total, _last), errors, projectName, now);
            Reset();
            return r;
        }

        if (_last < 0)
        {
            _last = simulated;
            return null;
        }

        if (simulated > _last)
        {
            if (!_active)
            {
                _active = true;
                _startedAt = now;
                _baseline = _last;
                _tainted = calculationActivity;
            }
            _last = simulated;
            _lastChange = now;
            return null;
        }

        if (simulated < _last)
        {
            // Results were reset; a new run may start from here.
            _last = simulated;
            _active = false;
            _tainted = false;
            return null;
        }

        if (_active && now - _lastChange >= _quiet())
            return Emit(simulated, total, errors, projectName, now);

        return null;
    }

    private SimulationResult? Emit(int simulated, int total, int errors, string projectName, DateTimeOffset now)
    {
        _active = false;
        var tainted = _tainted;
        _tainted = false;
        var count = Math.Max(0, simulated - _baseline);
        if (count == 0) return null;
        if (tainted)
        {
            LastSkipReason = $"machining results of {count} operations appeared together with a toolpath calculation; not a simulation";
            return null;
        }
        var end = _lastChange > _startedAt ? _lastChange : now;
        return new SimulationResult(projectName, end - _startedAt, count, total, errors, now);
    }

    private void Reset()
    {
        _active = false;
        _tainted = false;
        _last = -1;
        _baseline = 0;
    }
}
