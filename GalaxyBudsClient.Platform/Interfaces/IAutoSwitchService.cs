using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GalaxyBudsClient.Platform.Interfaces;

public enum AutoSwitchMultipointState
{
    Null = -1,
    Foreground = 0,
    Background = 1,
    Invalid = 255
}

[Flags]
public enum AutoSwitchStreamType
{
    None = 0,
    A2DP = 1,
    HFP = 2,
    CIS_Media = 4,
    BIS = 8,
    CIS_Call = 16,
    AutoSwitchRequest = 128
}

public interface IAutoSwitchService : IDisposable
{
    bool IsSupported { get; }
    bool IsRunning { get; }
    bool IsMdeConnected { get; }
    bool AutoSwitchEnabled { get; set; }
    bool ReleaseA2dpOnPhoneTakeover { get; set; }
    float AudioPeakThreshold { get; set; }
    AutoSwitchMultipointState CurrentMdeState { get; }
    AutoSwitchStreamType CurrentStreamType { get; }
    float CurrentAudioPeak { get; }
    string? FallbackAudioEndpointName { get; }

    IReadOnlyList<string> RecentLogs { get; }

    event EventHandler<string>? LogMessageAdded;
    event EventHandler? StateChanged;

    Task StartAsync(ulong btMac, string? deviceName);
    Task StopAsync();
    Task<bool> PullToPcAsync(string reason = "Manual UI Request");
    Task<bool> HandbackToPhoneAsync(string reason = "Manual UI Request");
}
