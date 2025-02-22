using MultiFunPlayer.Common;
using MultiFunPlayer.Common.Messages;
using MultiFunPlayer.Input;
using Newtonsoft.Json.Linq;
using PropertyChanged;
using Stylet;
using System.ComponentModel;
using System.Reflection;

namespace MultiFunPlayer.VideoSource;

public abstract class AbstractVideoSource : Screen, IVideoSource, IHandle<AppSettingsMessage>
{
    private readonly AsyncManualResetEvent _statusEvent;
    private CancellationTokenSource _cancellationSource;
    private Task _task;

    [SuppressPropertyChangedWarnings] public abstract ConnectionStatus Status { get; protected set; }
    public bool AutoConnectEnabled { get; set; } = false;

    protected AbstractVideoSource(IShortcutManager shortcutManager, IEventAggregator eventAggregator)
    {
        _statusEvent = new AsyncManualResetEvent();

        eventAggregator.Subscribe(this);
        PropertyChanged += (s, e) =>
        {
            if (string.Equals(e.PropertyName, "Status", StringComparison.OrdinalIgnoreCase))
                _statusEvent.Reset();
        };

        RegisterShortcuts(shortcutManager);
    }

    public string Name => GetType().GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;

    protected abstract Task RunAsync(CancellationToken token);

    public async virtual Task ConnectAsync()
    {
        if (Status != ConnectionStatus.Disconnected)
            return;

        Status = ConnectionStatus.Connecting;
        _cancellationSource = new CancellationTokenSource();
        _task = Task.Factory.StartNew(() => RunAsync(_cancellationSource.Token),
            _cancellationSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)
            .Unwrap();
        _ = _task.ContinueWith(_ => DisconnectAsync()).Unwrap();

        await Task.CompletedTask;
    }

    public async virtual Task DisconnectAsync()
    {
        if (Status == ConnectionStatus.Disconnected || Status == ConnectionStatus.Disconnecting)
            return;

        Status = ConnectionStatus.Disconnecting;

        _cancellationSource?.Cancel();

        if (_task != null)
            await _task;

        await Task.Delay(250);
        _cancellationSource?.Dispose();

        _cancellationSource = null;
        _task = null;

        Status = ConnectionStatus.Disconnected;
    }

    public async virtual ValueTask<bool> CanConnectAsync(CancellationToken token) => await ValueTask.FromResult(false);
    public async virtual ValueTask<bool> CanConnectAsyncWithStatus(CancellationToken token)
    {
        if (Status != ConnectionStatus.Disconnected)
            return await ValueTask.FromResult(false);

        Status = ConnectionStatus.Connecting;
        await Task.Delay(100, token);
        var result = await CanConnectAsync(token);
        Status = ConnectionStatus.Disconnected;

        return result;
    }

    public async Task WaitForStatus(IEnumerable<ConnectionStatus> statuses, CancellationToken token)
    {
        while (!statuses.Contains(Status))
            await _statusEvent.WaitAsync(token);
    }

    protected abstract void HandleSettings(JObject settings, SettingsAction action);
    public void Handle(AppSettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            if (!message.Settings.EnsureContainsObjects("VideoSource", Name)
             || !message.Settings.TryGetObject(out var settings, "VideoSource", Name))
                return;

            settings[nameof(AutoConnectEnabled)] = new JValue(AutoConnectEnabled);

            HandleSettings(settings, message.Action);
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!message.Settings.TryGetObject(out var settings, "VideoSource", Name))
                return;

            if (settings.TryGetValue<bool>(nameof(AutoConnectEnabled), out var autoConnectEnabled))
                AutoConnectEnabled = autoConnectEnabled;

            HandleSettings(settings, message.Action);
        }
    }

    protected virtual void RegisterShortcuts(IShortcutManager s)
    {
        #region AutoConnectEnabled
        s.RegisterAction($"{Name}::AutoConnectEnabled::Set", b => b.WithSetting<bool>(s => s.WithLabel("Enable auto connect")).WithCallback((_, enabled) => AutoConnectEnabled = enabled));
        s.RegisterAction($"{Name}::AutoConnectEnabled::Toggle", b => b.WithCallback(_ => AutoConnectEnabled = !AutoConnectEnabled));
        #endregion
    }

    protected async virtual void Dispose(bool disposing)
    {
        await DisconnectAsync();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
