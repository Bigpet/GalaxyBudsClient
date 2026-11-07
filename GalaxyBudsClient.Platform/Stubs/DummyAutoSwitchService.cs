using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.Platform.Stubs;

public class DummyAutoSwitchService : IAutoSwitchService
{
    public bool IsSupported => false;
    public bool IsRunning => false;
    public bool IsMdeConnected => false;
    public bool AutoSwitchEnabled { get; set; }
    public bool ReleaseA2dpOnPhoneTakeover { get; set; }
    public float AudioPeakThreshold { get; set; } = 0.015f;
    public AutoSwitchMultipointState CurrentMdeState => AutoSwitchMultipointState.Null;
    public AutoSwitchStreamType CurrentStreamType => AutoSwitchStreamType.None;
    public float CurrentAudioPeak => 0f;
    public string? FallbackAudioEndpointName => null;
    public IReadOnlyList<string> RecentLogs => [];

#pragma warning disable CS0067
    public event EventHandler<string>? LogMessageAdded;
    public event EventHandler? StateChanged;
#pragma warning restore CS0067

    public Task StartAsync(ulong btMac, string? deviceName) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<bool> PullToPcAsync(string reason = "Manual UI Request") => Task.FromResult(false);
    public Task<bool> HandbackToPhoneAsync(string reason = "Manual UI Request") => Task.FromResult(false);

    public void Dispose() { }
}
