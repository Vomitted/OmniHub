// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Fan;

/// <summary>
/// The fan loop's recent decisions, kept so they can be read back as a table.
///
/// Every tick already produced a complete account of itself -- what the sensor said, what the curve
/// acted on, what it commanded, anything that went wrong -- and the Fans page showed the latest as
/// one sentence, overwritten two seconds later. The sawtooth the spike filter fixed was visible only
/// by replaying the thermal log offline; with the last thirty ticks on screen it is visible as it
/// happens.
///
/// Kept by the service rather than by the page, so the history is complete the first time the page
/// is opened, and bounded, so a session of weeks costs thirty entries.
/// </summary>
public sealed class FanTickLog
{
    public const int DefaultCapacity = 30;

    private readonly (DateTime AtUtc, FanTick Tick)[] _ring;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public FanTickLog(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "A log needs room for at least one tick.");
        _ring = new (DateTime, FanTick)[capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>Records one tick. Called from the fan loop, so it only stores; nothing here can throw or wait.</summary>
    public void Add(DateTime atUtc, FanTick tick)
    {
        lock (_gate)
        {
            _ring[_next] = (atUtc, tick);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    /// <summary>What has been recorded, newest first.</summary>
    public IReadOnlyList<(DateTime AtUtc, FanTick Tick)> Newest()
    {
        lock (_gate)
        {
            var newest = new List<(DateTime, FanTick)>(_count);
            for (int i = 1; i <= _count; i++)
                newest.Add(_ring[(_next - i + _ring.Length) % _ring.Length]);
            return newest;
        }
    }
}
