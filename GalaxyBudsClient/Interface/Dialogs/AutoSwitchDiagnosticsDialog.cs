using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using GalaxyBudsClient.Model;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Platform.Interfaces;
using Symbol = FluentIcons.Common.Symbol;
using SymbolIconSource = FluentIcons.Avalonia.Fluent.SymbolIconSource;

namespace GalaxyBudsClient.Interface.Dialogs;

public class AutoSwitchDiagnosticsDialog : TaskDialog
{
    private readonly TextBlock _statusText;
    private readonly TextBox _logBox;
    private readonly DispatcherTimer _timer;

    public AutoSwitchDiagnosticsDialog()
    {
        Header = "Seamless Auto-Switch Diagnostics";
        SubHeader = "Real-time Samsung MDE Multipoint V3 Status & Logs";
        IconSource = new SymbolIconSource { Symbol = Symbol.ArrowSyncCheckmark };
        XamlRoot = TopLevel.GetTopLevel(MainView.Instance);

        var stack = new StackPanel
        {
            Spacing = 10,
            Width = 620
        };

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 5)
        };
        stack.Children.Add(_statusText);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var btnPull = new Button
        {
            Content = "Pull Audio to PC",
            Command = new MiniCommand(async _ =>
            {
                await PlatformImpl.AutoSwitchService.PullToPcAsync("Diagnostics UI");
                UpdateStatus();
            })
        };
        btnPanel.Children.Add(btnPull);

        var btnHandback = new Button
        {
            Content = "Hand back to Phone",
            Command = new MiniCommand(async _ =>
            {
                await PlatformImpl.AutoSwitchService.HandbackToPhoneAsync("Diagnostics UI");
                UpdateStatus();
            })
        };
        btnPanel.Children.Add(btnHandback);

        var btnCopy = new Button
        {
            Content = "Copy Logs",
            Command = new MiniCommand(async _ =>
            {
                var topLevel = TopLevel.GetTopLevel(MainView.Instance);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(_logBox.Text ?? string.Empty);
                }
            })
        };
        btnPanel.Children.Add(btnCopy);

        stack.Children.Add(btnPanel);

        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11,
            Height = 240,
            MaxHeight = 300
        };
        stack.Children.Add(_logBox);

        Content = stack;
        Buttons = [TaskDialogButton.CloseButton];

        UpdateStatus();
        RefreshLogs();

        PlatformImpl.AutoSwitchService.LogMessageAdded += OnLogAdded;
        PlatformImpl.AutoSwitchService.StateChanged += OnStateChanged;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
    }

    private void UpdateStatus()
    {
        var svc = PlatformImpl.AutoSwitchService;
        var sb = new StringBuilder();
        sb.AppendLine($"Service Running: {(svc.IsRunning ? "Yes" : "No")} | MDE Socket: {(svc.IsMdeConnected ? "Connected" : "Standby/Disconnected")}");
        sb.AppendLine($"Audio Focus: {svc.CurrentMdeState} | Active Stream: {svc.CurrentStreamType}");
        sb.Append($"PC Audio Peak: {svc.CurrentAudioPeak:F3} | Fallback Endpoint: {svc.FallbackAudioEndpointName ?? "(None detected)"}");
        _statusText.Text = sb.ToString();
    }

    private void RefreshLogs()
    {
        var logs = PlatformImpl.AutoSwitchService.RecentLogs;
        _logBox.Text = string.Join(Environment.NewLine, logs);
        _logBox.CaretIndex = _logBox.Text.Length;
    }

    private void OnLogAdded(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshLogs();
        });
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus();
        });
    }

    protected override void OnClosing(TaskDialogClosingEventArgs args)
    {
        _timer.Stop();
        PlatformImpl.AutoSwitchService.LogMessageAdded -= OnLogAdded;
        PlatformImpl.AutoSwitchService.StateChanged -= OnStateChanged;
        base.OnClosing(args);
    }

    protected override Type StyleKeyOverride => typeof(TaskDialog);
}
