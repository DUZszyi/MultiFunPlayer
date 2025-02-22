using MultiFunPlayer.Common;
using MultiFunPlayer.Common.Messages;
using MultiFunPlayer.Input;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MultiFunPlayer.VideoSource.ViewModels;

[DisplayName("MPC-HC")]
public class MpcVideoSourceViewModel : AbstractVideoSource, IHandle<VideoPlayPauseMessage>, IHandle<VideoSeekMessage>
{
    protected Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IEventAggregator _eventAggregator;
    private readonly Channel<object> _writeMessageChannel;

    public override ConnectionStatus Status { get; protected set; }

    public IPEndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 13579);

    public MpcVideoSourceViewModel(IShortcutManager shortcutManager, IEventAggregator eventAggregator)
        : base(shortcutManager, eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _writeMessageChannel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    protected override async Task RunAsync(CancellationToken token)
    {
        try
        {
            Logger.Info("Connecting to {0} at \"{1}\"", Name, Endpoint);
            if (Endpoint == null)
                throw new Exception("Endpoint cannot be null.");

            using var client = WebUtils.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(1000);

            var uri = new Uri($"http://{Endpoint.Address}:{Endpoint.Port}");
            var response = await UnwrapTimeout(() => client.GetAsync(uri, token));
            response.EnsureSuccessStatusCode();

            Status = ConnectionStatus.Connected;
            while (_writeMessageChannel.Reader.TryRead(out _)) ;

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(client, cancellationSource.Token), WriteAsync(client, cancellationSource.Token));
            cancellationSource.Cancel();

            if (task.Exception?.TryUnwrapAggregateException(out var e) == true)
                e.Throw();
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException e) { Logger.Debug(e, $"{Name} failed with exception"); }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        _eventAggregator.Publish(new VideoFileChangedMessage(null));
        _eventAggregator.Publish(new VideoPlayingMessage(isPlaying: false));
    }

    private async Task ReadAsync(HttpClient client, CancellationToken token)
    {
        var variablesUri = new Uri($"http://{Endpoint.Address}:{Endpoint.Port}/variables.html");
        var variableRegex = new Regex(@"<p id=""(.+?)"">(.+?)<\/p>", RegexOptions.Compiled);
        var playerState = new PlayerState();

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token);

                var response = await UnwrapTimeout(() => client.GetAsync(variablesUri, token));
                if (response == null)
                    continue;

                response.EnsureSuccessStatusCode();
                var message = await response.Content.ReadAsStringAsync(token);

                Logger.Trace("Received \"{0}\" from \"{1}\"", message, Name);
                var variables = variableRegex.Matches(message).OfType<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

                if (variables.TryGetValue("state", out var stateString) && int.TryParse(stateString, out var state) && state != playerState.State)
                {
                    _eventAggregator.Publish(new VideoPlayingMessage(state == 2));
                    playerState.State = state;
                }

                if (playerState.State < 0)
                    continue;

                if (variables.TryGetValue("filepath", out var path))
                {
                    if (string.IsNullOrWhiteSpace(path))
                        path = null;

                    if (path != playerState.Path)
                    {
                        _eventAggregator.Publish(new VideoFileChangedMessage(path));
                        playerState.Path = path;
                    }
                }

                if (variables.TryGetValue("duration", out var durationString) && long.TryParse(durationString, out var duration) && duration >= 0 && duration != playerState.Duration)
                {
                    _eventAggregator.Publish(new VideoDurationMessage(TimeSpan.FromMilliseconds(duration)));
                    playerState.Duration = duration;
                }

                if (variables.TryGetValue("position", out var positionString) && long.TryParse(positionString, out var position) && position >= 0 && position != playerState.Position)
                {
                    _eventAggregator.Publish(new VideoPositionMessage(TimeSpan.FromMilliseconds(position)));
                    playerState.Position = position;
                }

                if (variables.TryGetValue("playbackrate", out var playbackrateString) && float.TryParse(playbackrateString, out var speed) && speed > 0 && speed != playerState.Speed)
                {
                    _eventAggregator.Publish(new VideoSpeedMessage(speed));
                    playerState.Speed = speed;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(HttpClient client, CancellationToken token)
    {
        var commandUriBase = $"http://{Endpoint.Address}:{Endpoint.Port}/command.html?wm_command=";

        try
        {
            while (!token.IsCancellationRequested)
            {
                await _writeMessageChannel.Reader.WaitToReadAsync(token);
                var message = await _writeMessageChannel.Reader.ReadAsync(token);

                var commandString = message switch
                {
                    VideoPlayPauseMessage playPauseMessage => $"{(int)(playPauseMessage.State ? MpcCommand.Play : MpcCommand.Pause)}",
                    VideoSeekMessage seekMessage when seekMessage.Position.HasValue => $"{(int)MpcCommand.Seek}&position={seekMessage.Position?.ToString(@"hh\:mm\:ss")}",
                    _ => null
                };

                var commandUri = new Uri($"{commandUriBase}{commandString}");
                Logger.Trace("Sending \"{0}\" to \"{1}\"", commandString, Name);

                var response = await UnwrapTimeout(() => client.GetAsync(commandUri, token));
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) { }
    }

    protected override void HandleSettings(JObject settings, SettingsAction action)
    {
        if (action == SettingsAction.Saving)
        {
            if (Endpoint != null)
                settings[nameof(Endpoint)] = new JValue(Endpoint.ToString());
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<IPEndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;
        }
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token)
    {
        try
        {
            if (Endpoint == null)
                return await ValueTask.FromResult(false);

            var uri = new Uri($"http://{Endpoint.Address}:{Endpoint.Port}");

            using var client = WebUtils.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(50);

            var response = await client.GetAsync(uri, token);
            response.EnsureSuccessStatusCode();

            return await ValueTask.FromResult(true);
        }
        catch
        {
            return await ValueTask.FromResult(false);
        }
    }

    private async Task<HttpResponseMessage> UnwrapTimeout(Func<Task<HttpResponseMessage>> action)
    {
        //https://github.com/dotnet/runtime/issues/21965

        try
        {
            return await action();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException operationCanceledException)
            {
                var innerException = operationCanceledException.InnerException;
                if (innerException is TimeoutException)
                    innerException.Throw();

                operationCanceledException.Throw();
            }

            throw;
        }
    }

    protected override void RegisterShortcuts(IShortcutManager s)
    {
        base.RegisterShortcuts(s);

        #region Endpoint
        s.RegisterAction($"{Name}::Endpoint::Set", b => b.WithSetting<string>(s => s.WithLabel("Endpoint").WithDescription("ip:port")).WithCallback((_, endpointString) =>
        {
            if (IPEndPoint.TryParse(endpointString, out var endpoint))
                Endpoint = endpoint;
        }));
        #endregion
    }

    public async void Handle(VideoSeekMessage message)
    {
        if (Status == ConnectionStatus.Connected)
            await _writeMessageChannel.Writer.WriteAsync(message);
    }

    public async void Handle(VideoPlayPauseMessage message)
    {
        if (Status == ConnectionStatus.Connected)
            await _writeMessageChannel.Writer.WriteAsync(message);
    }

    private class PlayerState
    {
        public string Path { get; set; }
        public long? Position { get; set; }
        public float? Speed { get; set; }
        public int? State { get; set; }
        public long? Duration { get; set; }
    }

    private enum MpcCommand
    {
        Seek = -1,
        Play = 887,
        Pause = 888,
    }
}
