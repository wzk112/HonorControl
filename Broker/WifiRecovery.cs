namespace HonorControl.Network;

internal sealed class WifiRecovery
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);
    private WifiCandidate? _last;
    private DateTimeOffset _seen;
    private DateTimeOffset? _lost;
    private DateTimeOffset _lastAttempt;

    public WifiCandidate? Evaluate(WifiSnapshot scan, DateTimeOffset now, bool recover, bool betterWifi, bool protectedScene, bool otherUplink)
    {
        var connected = scan.Networks.FirstOrDefault(network => network.Connected);
        if (connected is not null)
        {
            _last = connected.Saved ? connected : null;
            _seen = now;
            _lost = null;
            if (!betterWifi || protectedScene || connected.Signal >= 35 || now - _lastAttempt < Retry) return null;
            return scan.Networks.FirstOrDefault(network => network.Saved && !network.Connected &&
                network.InterfaceId == connected.InterfaceId && network.Signal >= connected.Signal + 20);
        }

        if (!recover || otherUplink || _last is null || now - _seen > Remember || !scan.Disconnected.Contains(_last.InterfaceId))
        {
            _lost = null;
            return null;
        }
        _lost ??= now;
        if (now - _lost < Grace || now - _lastAttempt < Retry) return null;
        return scan.Networks.FirstOrDefault(network => network.Saved && network.Signal >= 20 &&
            network.InterfaceId == _last.InterfaceId && network.Profile == _last.Profile);
    }

    public void MarkAttempt(DateTimeOffset time) => _lastAttempt = time;
    public void Reset() { _last = null; _lost = null; _lastAttempt = default; }
}
