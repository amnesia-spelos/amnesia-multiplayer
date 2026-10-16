namespace Multimnesia.Client;

// A token bucket for one kind of message on one connection: a burst received at once, refilled at a steady rate.
public sealed class InboundAllowance(int burst, double perSecond, DateTime startedAt)
{
    private double _remaining = burst;
    private DateTime _refilledAt = startedAt;

    // Takes one message received at now; false, taking nothing, when it goes over the allowance.
    public bool TryTake(DateTime now)
    {
        _remaining = Math.Min(burst, _remaining + (now - _refilledAt).TotalSeconds * perSecond);
        _refilledAt = now;
        if (_remaining < 1) return false;
        _remaining--;
        return true;
    }
}
