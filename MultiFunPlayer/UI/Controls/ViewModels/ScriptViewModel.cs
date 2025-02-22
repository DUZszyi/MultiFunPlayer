using MultiFunPlayer.Common;
using Stylet;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.IO.Compression;
using PropertyChanged;
using Newtonsoft.Json.Linq;
using MultiFunPlayer.Common.Messages;
using Newtonsoft.Json;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.ComponentModel;
using MultiFunPlayer.OutputTarget;
using NLog;
using System.Runtime.CompilerServices;
using MultiFunPlayer.Input;
using MultiFunPlayer.MotionProvider;
using MultiFunPlayer.VideoSource.MediaResource;
using MaterialDesignThemes.Wpf;
using System.Reflection;
using MultiFunPlayer.VideoSource.MediaResource.Modifier.ViewModels;

namespace MultiFunPlayer.UI.Controls.ViewModels;

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
public class ScriptViewModel : Screen, IDeviceAxisValueProvider, IDisposable,
    IHandle<VideoPositionMessage>, IHandle<VideoPlayingMessage>, IHandle<VideoFileChangedMessage>, IHandle<VideoDurationMessage>, 
    IHandle<VideoSpeedMessage>, IHandle<AppSettingsMessage>, IHandle<SyncRequestMessage>, IHandle<ScriptLoadMessage>
{
    protected Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IEventAggregator _eventAggregator;
    private readonly IMediaResourceFactory _mediaResourceFactory;
    private Thread _updateThread;
    private CancellationTokenSource _cancellationSource;
    private float _playbackSpeedCorrection;

    public bool IsPlaying { get; set; }
    public float CurrentPosition { get; set; }
    public float PlaybackSpeed { get; set; }
    public float VideoDuration { get; set; }

    public ObservableConcurrentDictionary<DeviceAxis, AxisModel> AxisModels { get; set; }
    public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, AxisState> AxisStates { get; }
    public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, KeyframeCollection> AxisKeyframes { get; }

    public Dictionary<string, Type> VideoPathModifierTypes { get; }
    public IMotionProviderManager MotionProviderManager { get; }

    public MediaResourceInfo VideoFile { get; set; }

    [JsonProperty] public float GlobalOffset { get; set; }
    [JsonProperty] public bool ValuesContentVisible { get; set; }
    [JsonProperty] public bool VideoContentVisible { get; set; } = true;
    [JsonProperty] public bool AxisContentVisible { get; set; } = false;
    [JsonProperty] public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, AxisSettings> AxisSettings { get; }
    [JsonProperty(ItemTypeNameHandling = TypeNameHandling.Objects)] public ObservableConcurrentCollection<IMediaPathModifier> VideoPathModifiers => _mediaResourceFactory.PathModifiers;
    [JsonProperty] public ObservableConcurrentCollection<ScriptLibrary> ScriptLibraries { get; }
    [JsonProperty] public SyncSettings SyncSettings { get; set; }
    [JsonProperty] public bool HeatmapShowStrokeLength { get; set; }
    [JsonProperty] public int HeatmapBucketCount { get; set; } = 333;
    [JsonProperty] public bool HeatmapInvertY { get; set; } = false;
    [JsonProperty] public bool AutoSkipToScriptStartEnabled { get; set; } = true;
    [JsonProperty] public float AutoSkipToScriptStartOffset { get; set; } = 5;

    public bool IsSyncing => AxisStates.Values.Any(s => s.SyncTime < SyncSettings.Duration);
    public float SyncProgress => !IsSyncing ? 100 : GetSyncProgress(AxisStates.Values.Min(s => s.SyncTime), SyncSettings.Duration) * 100;

    public ScriptViewModel(IShortcutManager shortcutManager, IMotionProviderManager motionProviderManager, IMediaResourceFactory mediaResourceFactory, IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.Subscribe(this);

        MotionProviderManager = motionProviderManager;
        _mediaResourceFactory = mediaResourceFactory;

        AxisModels = new ObservableConcurrentDictionary<DeviceAxis, AxisModel>(DeviceAxis.All.ToDictionary(a => a, _ => new AxisModel()));
        VideoPathModifierTypes = ReflectionUtils.FindImplementations<IMediaPathModifier>()
                                                .ToDictionary(t => t.GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName, t => t);

        ScriptLibraries = new ObservableConcurrentCollection<ScriptLibrary>();
        SyncSettings = new SyncSettings();

        VideoFile = null;

        VideoDuration = float.NaN;
        CurrentPosition = float.NaN;
        PlaybackSpeed = 1;
        _playbackSpeedCorrection = 1;

        IsPlaying = false;

        AxisStates = AxisModels.CreateView(model => model.State);
        AxisSettings = AxisModels.CreateView(model => model.Settings);
        AxisKeyframes = AxisModels.CreateView(model => model.Script?.Keyframes, "Script");

        foreach (var (_, settings) in AxisSettings)
            settings.PropertyChanged += OnAxisSettingsPropertyChanged;

        _cancellationSource = new CancellationTokenSource();
        _updateThread = new Thread(() => UpdateThread(_cancellationSource.Token)) { IsBackground = true };
        _updateThread.Start();

        ResetSync(false);
        RegisterShortcuts(shortcutManager);
    }

    private void UpdateThread(CancellationToken token)
    {
        var stopwatch = new Stopwatch();
        const float uiUpdateInterval = 1f / 60f;
        var uiUpdateTime = 0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float ElapsedSeconds() => stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;

        stopwatch.Start();
        while (!token.IsCancellationRequested)
        {
            var dirty = UpdateValues();
            UpdateUi();
            UpdateSync();

            stopwatch.Restart();
            Thread.Sleep(IsPlaying || dirty ? 2 : 10);
        }

        bool UpdateValues()
        {
            if (IsPlaying)
                CurrentPosition += ElapsedSeconds() * PlaybackSpeed * _playbackSpeedCorrection;

            var dirty = false;
            foreach (var axis in DeviceAxis.All)
            {
                var state = AxisStates[axis];
                var settings = AxisSettings[axis];

                lock (state)
                {
                    state.IsDirty = false;

                    var lastValue = state.Value;
                    var newValue = float.NaN;
                    if (!settings.Bypass)
                    {
                        state.IsDirty |= UpdateScript(axis, state, settings, lastValue, ref newValue);
                        state.IsDirty |= UpdateMotionProvider(axis, state, settings, lastValue, ref newValue);
                    }

                    state.IsDirty |= UpdateSync(axis, state, lastValue, ref newValue);
                    state.IsDirty |= UpdateAutoHome(axis, state, settings, lastValue, ref newValue);
                    state.IsDirty |= UpdateSmartLimit(axis, state, settings, lastValue, ref newValue);

                    SpeedLimit(axis, state, settings, lastValue, ref newValue);
                    if (!float.IsFinite(lastValue) && float.IsFinite(newValue))
                        state.IsDirty = true;

                    if(state.IsDirty)
                        state.Value = newValue;

                    dirty |= state.IsDirty;
                }
            }

            return dirty;

            void SpeedLimit(DeviceAxis axis, AxisState state, AxisSettings settings, float lastValue, ref float newValue)
            {
                static bool SpeedLimitImpl(DeviceAxis axis, AxisSettings settings, float deltaTime, float lastValue, ref float newValue)
                {
                    if (!settings.SpeedLimitEnabled)
                        return false;

                    var step = newValue - lastValue;
                    if (!float.IsFinite(step))
                        return false;
                    if (MathF.Abs(step) < 0.000001f)
                        return false;

                    var speed = step / deltaTime;
                    var maxSpeed = 1 / settings.MaximumSecondsPerStroke;
                    if (MathF.Abs(speed / maxSpeed) < 1)
                        return false;
                    if (!float.IsFinite(maxSpeed))
                        return false;

                    newValue = lastValue + maxSpeed * deltaTime * MathF.Sign(speed);
                    return true;
                }

                var deltaTime = ElapsedSeconds();
                var lastIsSpeedLimited = state.IsSpeedLimited;
                state.IsSpeedLimited = SpeedLimitImpl(axis, settings, deltaTime, lastValue, ref newValue);
                if (lastIsSpeedLimited != state.IsSpeedLimited)
                    state.NotifyIsSpeedLimitedChanged();
            }

            bool UpdateSmartLimit(DeviceAxis axis, AxisState state, AxisSettings settings, float lastValue, ref float newValue)
            {
                if (!settings.SmartLimitEnabled)
                    return false;
                if (!float.IsFinite(newValue))
                    return false;
                if (!DeviceAxis.TryParse("L0", out var strokeAxis) || axis == strokeAxis)
                    return false;

                var limitState = AxisStates[strokeAxis];
                var limitValue = limitState.Value;
                var factor = MathUtils.Map(limitValue, 0.25f, 0.9f, 1f, 0f);
                newValue = MathUtils.Lerp(newValue, axis.DefaultValue, factor);
                return MathF.Abs(lastValue - newValue) > 0.000001f;
            }

            bool UpdateScript(DeviceAxis axis, AxisState state, AxisSettings settings, float lastValue, ref float newValue)
            {
                if (!IsPlaying)
                    return false;

                if (state.AfterScript)
                    return false;

                if (!AxisKeyframes.TryGetValue(axis, out var keyframes) || keyframes == null || keyframes.Count == 0)
                    return false;

                var axisPosition = GetAxisPosition(axis);
                var beforeIndex = state.Index;
                state.Index = keyframes.AdvanceIndex(state.Index, axisPosition);

                if (beforeIndex == -1 && state.Index >= 0)
                {
                    Logger.Debug("Resetting sync on script start [Axis: {0}]", axis);
                    state.SyncTime = 0;
                }

                if (!keyframes.ValidateIndex(state.Index) || !keyframes.ValidateIndex(state.Index + 1))
                {
                    if (state.Index + 1 >= keyframes.Count)
                    {
                        Logger.Debug("Resetting sync on script end [Axis: {0}]", axis);
                        state.Invalidate(end: true);
                        state.SyncTime = 0;
                    }

                    return false;
                }

                newValue = keyframes.Interpolate(state.Index, axisPosition, settings.InterpolationType);
                if (settings.Inverted)
                    newValue = 1 - newValue;

                return MathF.Abs(lastValue - newValue) > 0.000001f;
            }

            bool UpdateMotionProvider(DeviceAxis axis, AxisState state, AxisSettings settings, float lastValue, ref float newValue)
            {
                if (settings.SelectedMotionProvider == null)
                    return false;
                if (!settings.UpdateMotionProviderWhenPaused && !IsPlaying)
                    return false;
                if (!settings.UpdateMotionProviderWithoutScript && !state.InsideScript)
                    return false;
                if (settings.UpdateMotionProviderWithAxis != null && !AxisStates[settings.UpdateMotionProviderWithAxis].IsDirty)
                    return false;

                var providerResult = MotionProviderManager.Update(axis, settings.SelectedMotionProvider, ElapsedSeconds());
                if (providerResult is not float providerValue)
                    return false;

                newValue = IsPlaying && state.InsideScript
                    ? MathUtils.Lerp(newValue, providerValue, MathUtils.Clamp01(settings.MotionProviderBlend / 100))
                    : providerValue;

                return MathF.Abs(lastValue - newValue) > 0.000001f;
            }

            bool UpdateAutoHome(DeviceAxis axis, AxisState state, AxisSettings settings, float lastValue, ref float newValue)
            {
                if (state.IsDirty || (state.InsideScript && IsPlaying))
                {
                    state.AutoHomeTime = 0;
                    return false;
                }

                if (!float.IsFinite(state.Value))
                    return false;

                if (!settings.AutoHomeEnabled)
                    return false;

                if (settings.AutoHomeDuration < 0.0001f)
                {
                    newValue = axis.DefaultValue;
                    return newValue != lastValue;
                }

                state.AutoHomeTime += ElapsedSeconds();
                var t = (state.AutoHomeTime - settings.AutoHomeDelay) / settings.AutoHomeDuration;
                if (t < 0 || t > 1)
                    return false;

                newValue = MathUtils.Lerp(state.Value, axis.DefaultValue, MathF.Pow(2, 10 * (t - 1)));
                return MathF.Abs(lastValue - newValue) > 0.000001f;
            }

            bool UpdateSync(DeviceAxis axis, AxisState state, float lastValue, ref float newValue)
            {
                if (!float.IsFinite(newValue))
                    return false;
                if (state.SyncTime >= SyncSettings.Duration)
                    return false;

                newValue = MathUtils.Lerp(!float.IsFinite(lastValue) ? axis.DefaultValue : lastValue, newValue, GetSyncProgress(state.SyncTime, SyncSettings.Duration));
                return MathF.Abs(lastValue - newValue) > 0.000001f;
            }
        }

        void UpdateUi()
        {
            uiUpdateTime += ElapsedSeconds();
            if (uiUpdateTime < uiUpdateInterval)
                return;

            uiUpdateTime = 0;
            if (ValuesContentVisible)
            {
                Execute.OnUIThread(() =>
                {
                    foreach (var axis in DeviceAxis.All)
                        AxisStates[axis].NotifyValueChanged();
                });
            }
        }

        void UpdateSync()
        {
            var dirty = false;
            foreach (var (axis, state) in AxisStates)
            {
                lock (state)
                {
                    if (state.SyncTime >= SyncSettings.Duration)
                        continue;

                    state.SyncTime += ElapsedSeconds();
                    dirty = true;
                }
            }

            if (dirty)
            {
                NotifyOfPropertyChange(nameof(IsSyncing));
                NotifyOfPropertyChange(nameof(SyncProgress));
            }
        }
    }

    #region Events
    public void Handle(VideoFileChangedMessage message)
    {
        var resource = _mediaResourceFactory.CreateFromPath(message.Path);
        if (VideoFile == null && resource == null)
            return;
        if (VideoFile != null && resource != null)
            if (string.Equals(VideoFile.Name, resource.Name, StringComparison.OrdinalIgnoreCase)
             && string.Equals(VideoFile.Source, resource.Source, StringComparison.OrdinalIgnoreCase))
                return;

        Logger.Info("Received VideoFileChangedMessage [Source: \"{0}\" Name: \"{1}\"]", resource?.Source, resource?.Name);

        VideoFile = resource;
        if (SyncSettings.SyncOnVideoFileChanged)
            ResetSync(isSyncing: VideoFile != null);

        ResetScript(null);
        ReloadScript(null);

        if (VideoFile == null)
        {
            VideoDuration = float.NaN;
            CurrentPosition = float.NaN;
            PlaybackSpeed = 1;
        }

        InvalidateState(null);
    }

    public void Handle(VideoPlayingMessage message)
    {
        if (IsPlaying == message.IsPlaying)
            return;

        Logger.Info("Received VideoPlayingMessage [IsPlaying: {0}]", message.IsPlaying);

        if (IsPlaying != message.IsPlaying)
            if (SyncSettings.SyncOnVideoPlayPause)
                ResetSync();

        IsPlaying = message.IsPlaying;
    }

    public void Handle(VideoDurationMessage message)
    {
        var newDuration = (float)(message.Duration?.TotalSeconds ?? float.NaN);
        if (VideoDuration == newDuration)
            return;

        Logger.Info("Received VideoDurationMessage [Duration: {0}]", message.Duration?.ToString());

        VideoDuration = newDuration;
        ScheduleAutoSkipToScriptStart();
    }

    public void Handle(VideoSpeedMessage message)
    {
        if (PlaybackSpeed == message.Speed)
            return;

        Logger.Info("Received VideoSpeedMessage [Speed: {0}]", message.Speed);
        PlaybackSpeed = message.Speed;
    }

    public void Handle(VideoPositionMessage message)
    {
        void UpdateCurrentPosition(float newPosition)
        {
            _playbackSpeedCorrection = 1;

            foreach (var axis in DeviceAxis.All)
                Monitor.Enter(AxisStates[axis]);

            try
            {
                foreach (var axis in DeviceAxis.All)
                    SearchForValidIndex(axis, AxisStates[axis], newPosition);

                CurrentPosition = newPosition;
            }
            finally
            {
                foreach (var axis in DeviceAxis.All)
                    Monitor.Exit(AxisStates[axis]);
            }
        }

        var newPosition = (float)(message.Position?.TotalSeconds ?? float.NaN);
        Logger.Trace("Received VideoPositionMessage [Position: {0}]", message.Position?.ToString());

        if (!float.IsFinite(newPosition))
        {
            CurrentPosition = float.NaN;
            return;
        }

        if (!float.IsFinite(CurrentPosition))
        {
            ResetSync();
            UpdateCurrentPosition(newPosition);
            return;
        }

        var error = float.IsFinite(CurrentPosition) ? newPosition - CurrentPosition : 0;
        var wasSeek = MathF.Abs(error) > 1.0f;
        if (wasSeek)
        {
            Logger.Debug("Detected seek: {0}", error);
            if (SyncSettings.SyncOnSeek)
                ResetSync();

            UpdateCurrentPosition(newPosition);
        }
        else
        {
            _playbackSpeedCorrection = MathUtils.Clamp(_playbackSpeedCorrection + error * 0.1f, 0.9f, 1.1f);
        }

        foreach (var axis in DeviceAxis.All)
        {
            var state = AxisStates[axis];
            if (state.Invalid)
                SearchForValidIndex(axis, state, newPosition);
        }
    }

    public void Handle(AppSettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            message.Settings["Script"] = JObject.FromObject(this);
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!message.Settings.TryGetObject(out var settings, "Script"))
                return;

            if (settings.TryGetValue(nameof(AxisSettings), out var axisSettingsToken))
            {
                foreach (var property in axisSettingsToken.Children<JProperty>())
                {
                    if (!DeviceAxis.TryParse(property.Name, out var axis))
                        continue;

                    property.Value.Populate(AxisSettings[axis]);
                }
            }

            if (settings.TryGetValue<List<IMediaPathModifier>>(nameof(VideoPathModifiers), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Objects }, out var videoPathModifiers))
            {
                VideoPathModifiers.Clear();
                foreach (var modifier in videoPathModifiers)
                    VideoPathModifiers.Add(modifier);
            }

            if (settings.TryGetValue<List<ScriptLibrary>>(nameof(ScriptLibraries), out var scriptDirectories))
            {
                foreach (var library in scriptDirectories)
                    ScriptLibraries.Add(library);
            }

            if (settings.TryGetValue<float>(nameof(GlobalOffset), out var globalOffset)) GlobalOffset = globalOffset;
            if (settings.TryGetValue<bool>(nameof(ValuesContentVisible), out var valuesContentVisible)) ValuesContentVisible = valuesContentVisible;
            if (settings.TryGetValue<bool>(nameof(VideoContentVisible), out var videoContentVisible)) VideoContentVisible = videoContentVisible;
            if (settings.TryGetValue<bool>(nameof(AxisContentVisible), out var axisContentVisible)) AxisContentVisible = axisContentVisible;
            if (settings.TryGetValue<int>(nameof(HeatmapBucketCount), out var heatmapBucketCount)) HeatmapBucketCount = heatmapBucketCount;
            if (settings.TryGetValue<bool>(nameof(HeatmapInvertY), out var heatmapInvertY)) HeatmapInvertY = heatmapInvertY;
            if (settings.TryGetValue<bool>(nameof(HeatmapShowStrokeLength), out var heatmapShowStrokeLength)) HeatmapShowStrokeLength = heatmapShowStrokeLength;

            if (settings.TryGetValue(nameof(SyncSettings), out var syncSettingsToken)) syncSettingsToken.Populate(SyncSettings);
        }
    }

    public void Handle(SyncRequestMessage message) => ResetSync(true, message.Axes);

    public void Handle(ScriptLoadMessage message)
    {
        if (message.Scripts == null)
            return;

        Logger.Info("Received ScriptLoadMessage [Axes: {list}]", message.Scripts.Keys);
        ResetSync(true, message.Scripts.Keys);

        foreach (var (axis, script) in message.Scripts)
            SetScript(axis, script);
    }
    #endregion

    #region Common

    private void InvalidateState(params DeviceAxis[] axes) => InvalidateState(axes?.AsEnumerable());
    private void InvalidateState(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;
        if (!axes.Any())
            return;

        Logger.Debug("Invalidating axes [Axes: {list}]", axes);
        foreach (var axis in axes)
        {
            var state = AxisStates[axis];
            lock (state)
                state.Invalidate();
        }
    }

    private float GetAxisPosition(DeviceAxis axis) => GetAxisPosition(axis, CurrentPosition);
    private float GetAxisPosition(DeviceAxis axis, float position) => position - GlobalOffset - AxisSettings[axis].Offset;
    public float GetValue(DeviceAxis axis) => MathUtils.Clamp01(AxisStates[axis].Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetSyncProgress(float time, float duration) => MathF.Pow(2, 10 * (time / duration - 1));

    private void ResetSync(bool isSyncing = true, params DeviceAxis[] axes) => ResetSync(isSyncing, axes?.AsEnumerable());
    private void ResetSync(bool isSyncing = true, IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All; 
        if (!axes.Any())
            return;

        Logger.Debug("Resetting sync [Axes: {list}]", axes);

        foreach (var axis in axes)
        {
            var state = AxisStates[axis];
            lock (state)
            {
                state.SyncTime = isSyncing ? 0 : SyncSettings.Duration;
            }
        }

        NotifyOfPropertyChange(nameof(IsSyncing));
        NotifyOfPropertyChange(nameof(SyncProgress));
    }
    #endregion

    #region UI Common
    [SuppressPropertyChangedWarnings]
    public void OnOffsetSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.DataContext is KeyValuePair<DeviceAxis, AxisModel> pair)
        {
            var (axis, _) = pair;
            ResetSync(true, axis);
        }
        else
        {
            ResetSync();
        }

        foreach (var axis in DeviceAxis.All)
            SearchForValidIndex(axis, AxisStates[axis]);
    }

    public void OnSliderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
            slider.Value = 0;
    }
    #endregion

    #region Script
    private List<DeviceAxis> TryMatchFiles(bool overwrite, params DeviceAxis[] axes) => TryMatchFiles(overwrite, axes?.AsEnumerable());
    private List<DeviceAxis> TryMatchFiles(bool overwrite, IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        var updated = new List<DeviceAxis>();
        if (!axes.Any() || VideoFile == null)
            return updated;

        Logger.Debug("Maching files to axes [Axes: {list}]", axes);
        bool TryMatchFile(string fileName, Func<IScriptFile> generator)
        {
            var videoWithoutExtension = Path.GetFileNameWithoutExtension(VideoFile.Name);
            var funscriptWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            if (DeviceAxis.TryParse("L0", out var strokeAxis))
            {
                if (axes.Contains(strokeAxis))
                {
                    if (string.Equals(funscriptWithoutExtension, videoWithoutExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        if (AxisModels[strokeAxis].Script == null || overwrite)
                        {
                            SetScript(strokeAxis, generator());
                            updated.Add(strokeAxis);

                            Logger.Debug("Matched {0} script to \"{1}\"", strokeAxis.Name, fileName);
                        }

                        return true;
                    }
                }
            }

            foreach (var axis in axes)
            {
                if (axis.FunscriptNames.Any(n => funscriptWithoutExtension.EndsWith(n, StringComparison.OrdinalIgnoreCase)))
                {
                    if (AxisModels[axis].Script == null || overwrite)
                    {
                        SetScript(axis, generator());
                        updated.Add(axis);

                        Logger.Debug("Matched {0} script to \"{1}\"", axis, fileName);
                    }

                    return true;
                }
            }

            return false;
        }

        bool TryMatchArchive(string path)
        {
            if (File.Exists(path))
            {
                Logger.Info("Matching zip file \"{0}\"", path);
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries.Where(e => string.Equals(Path.GetExtension(e.FullName), ".funscript", StringComparison.OrdinalIgnoreCase)))
                    TryMatchFile(entry.Name, () => ScriptFile.FromZipArchiveEntry(path, entry));

                return true;
            }

            return false;
        }

        var videoWithoutExtension = Path.GetFileNameWithoutExtension(VideoFile.Name);
        foreach (var library in ScriptLibraries)
        {
            Logger.Info("Searching library \"{0}\"", library.Directory);
            foreach (var zipFile in library.EnumerateFiles($"{videoWithoutExtension}.zip"))
                TryMatchArchive(zipFile.FullName);

            foreach (var funscriptFile in library.EnumerateFiles($"{videoWithoutExtension}*.funscript"))
                TryMatchFile(funscriptFile.Name, () => ScriptFile.FromFileInfo(funscriptFile));
        }

        if (Directory.Exists(VideoFile.Source))
        {
            Logger.Info("Searching video location \"{0}\"", VideoFile.Source);
            var sourceDirectory = new DirectoryInfo(VideoFile.Source);
            TryMatchArchive(Path.Join(sourceDirectory.FullName, $"{videoWithoutExtension}.zip"));

            foreach (var funscriptFile in sourceDirectory.EnumerateFiles($"{videoWithoutExtension}*.funscript"))
                TryMatchFile(funscriptFile.Name, () => ScriptFile.FromFileInfo(funscriptFile));
        }

        foreach (var axis in axes.Except(updated))
        {
            if (overwrite && AxisModels[axis].Script != null)
            {
                if (AxisModels[axis].Script.Origin != ScriptFileOrigin.User)
                {
                    ResetScript(axis);
                    updated.Add(axis);
                }
            }
        }

        return updated;
    }

    private void SearchForValidIndex(DeviceAxis axis, AxisState state) => SearchForValidIndex(axis, state, CurrentPosition);
    private void SearchForValidIndex(DeviceAxis axis, AxisState state, float position)
    {
        if (!AxisKeyframes.TryGetValue(axis, out var keyframes) || keyframes == null || keyframes.Count == 0)
            return;

        Logger.Debug("Searching for valid index [Axis: {0}]", axis);
        lock (state)
            state.Index = keyframes.BinarySearch(GetAxisPosition(axis, position));
    }

    private void UpdateLinkedScriptsTo(DeviceAxis axis) => UpdateLinkScriptFor(DeviceAxis.All.Where(a => a != axis && AxisSettings[a].LinkAxis == axis));
    private void UpdateLinkedScriptsTo(params DeviceAxis[] axes) => UpdateLinkedScriptsTo(axes?.AsEnumerable());
    private void UpdateLinkedScriptsTo(IEnumerable<DeviceAxis> axes)
    {
        axes ??= DeviceAxis.All;
        foreach (var axis in axes)
            UpdateLinkedScriptsTo(axis);
    }

    private void UpdateLinkScriptFor(params DeviceAxis[] axes) => UpdateLinkScriptFor(axes?.AsEnumerable());
    private void UpdateLinkScriptFor(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;
        if (!axes.Any())
            return;

        Logger.Debug("Trying to link axes [Axes: {list}]", axes);
        foreach (var axis in axes)
        {
            var model = AxisModels[axis];
            if (model.Settings.LinkAxis == null)
            {
                if (model.Settings.LinkAxisHasPriority)
                    ResetScript(axis);

                continue;
            }

            if (model.Script != null)
            {
                if (model.Settings.LinkAxisHasPriority && model.Script.Origin == ScriptFileOrigin.User)
                    continue;

                if (!model.Settings.LinkAxisHasPriority && model.Script.Origin != ScriptFileOrigin.Link)
                    continue;
            }

            Logger.Debug("Linked {0} to {1}", axis.Name, model.Settings.LinkAxis.Name);

            SetScript(axis, LinkedScriptFile.LinkTo(AxisModels[model.Settings.LinkAxis].Script));
        }
    }

    private void ResetScript(params DeviceAxis[] axes) => ResetScript(axes?.AsEnumerable());
    private void ResetScript(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;
        if (!axes.Any())
            return;

        Logger.Debug("Resetting axes [Axes: {list}]", axes);
        foreach (var axis in axes)
        {
            if (AxisModels[axis].Script != null)
                ResetSync(true, axis);

            SetScript(axis, null);
        }
    }

    private void SetScript(DeviceAxis axis, IScriptFile script)
    {
        var model = AxisModels[axis];
        var state = AxisStates[axis];
        lock (state)
        {
            state.Invalidate();
            model.Script = script;
        }

        if(script != null)
            Logger.Info("Set {0} script to \"{1}\" from \"{2}\"", axis, script.Name, script.Source);

        UpdateLinkedScriptsTo(axis);
    }

    private void ReloadScript(params DeviceAxis[] axes) => ReloadScript(axes?.AsEnumerable());
    private void ReloadScript(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;
        if (!axes.Any())
            return;

        ResetSync(true, axes);

        Logger.Debug("Reloading axes [Axes: {list}]", axes);
        foreach (var (enabled, items) in axes.GroupBy(a => AxisSettings[a].LinkAxisHasPriority))
        {
            var groupAxes = items.ToArray();
            if (enabled)
            {
                UpdateLinkScriptFor(groupAxes);
            }
            else
            {
                var updated = TryMatchFiles(true, groupAxes).Where(a => AxisModels[a].Script != null);
                UpdateLinkedScriptsTo(updated);
                UpdateLinkScriptFor(groupAxes.Except(updated));
            }
        }
    }

    private void SkipGap(float minimumSkip = 0, params DeviceAxis[] axes) => SkipGap(axes?.AsEnumerable(), minimumSkip);
    private void SkipGap(IEnumerable<DeviceAxis> axes = null, float minimumSkip = 0)
    {
        float? GetSkipPosition(DeviceAxis axis)
        {
            var keyframes = AxisKeyframes[axis];
            if (keyframes == null)
                return null;

            var state = AxisStates[axis];
            var startIndex = state.InsideScript ? state.Index : 0;
            var skipIndex = keyframes.SkipGap(startIndex);
            if (skipIndex == startIndex && state.InsideScript)
                return null;

            if (!keyframes.ValidateIndex(skipIndex))
                return null;

            return keyframes[skipIndex].Position;
        }

        axes ??= DeviceAxis.All;
        if (!axes.Any())
            return;

        var maybeSkipPosition = AxisKeyframes.Keys.Select(a => GetSkipPosition(a)).MinBy(x => x.GetValueOrDefault(float.PositiveInfinity));
        var currentPosition = CurrentPosition;
        if (maybeSkipPosition is not float skipPosition || currentPosition >= skipPosition || (skipPosition - currentPosition) <= minimumSkip)
            return;

        SeekVideoToTime(skipPosition);
    }
    #endregion 

    #region Video
    public void OnOpenVideoLocation()
    {
        if (VideoFile == null)
            return;

        var fullPath = VideoFile.IsModified ? VideoFile.ModifiedPath : VideoFile.OriginalPath;
        if (VideoFile.IsUrl)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        else
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (Directory.Exists(directory))
                Process.Start("explorer.exe", directory);
        }
    }

    public void OnPlayPauseClick()
    {
        _eventAggregator.Publish(new VideoPlayPauseMessage(!IsPlaying));
    }

    public void OnKeyframesHeatmapMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        if (e.ChangedButton != MouseButton.Left)
            return;

        SeekVideoToPercent((float)e.GetPosition(element).X / (float)element.ActualWidth);
    }

    private void SeekVideoToPercent(float percent)
    {
        if (!float.IsFinite(VideoDuration) || !float.IsFinite(percent))
            return;

        _eventAggregator.Publish(new VideoSeekMessage(TimeSpan.FromSeconds(VideoDuration * MathUtils.Clamp01(percent))));
    }

    private void SeekVideoToTime(float time)
    {
        if (!float.IsFinite(VideoDuration) || !float.IsFinite(time))
            return;

        _eventAggregator.Publish(new VideoSeekMessage(TimeSpan.FromSeconds(MathUtils.Clamp(time, 0, VideoDuration))));
    }

    private Task _autoSkipToScriptStartTask = Task.CompletedTask;
    private void ScheduleAutoSkipToScriptStart()
    {
        if (!AutoSkipToScriptStartEnabled)
            return;

        if (!_autoSkipToScriptStartTask.IsCompleted)
            return;

        _autoSkipToScriptStartTask = Task.Delay(1000, _cancellationSource.Token)
                                         .ContinueWith(_ => SeekVideoToScriptStart(AutoSkipToScriptStartOffset, onlyWhenBefore: true));
    }

    private void SeekVideoToScriptStart(float offset, bool onlyWhenBefore)
    {
        if (!float.IsFinite(VideoDuration) || !float.IsFinite(offset))
            return;

        var startPosition = AxisKeyframes.Select(x => x.Value)
                                         .Where(ks => ks != null)
                                         .Select(ks => ks.TryGet(ks.SkipGap(index: 0), out var k) ? k.Position : default(float?))
                                         .FirstOrDefault();
        if (startPosition == null)
            return;

        var targetVideoTime = MathF.Max(MathF.Min(startPosition.Value, VideoDuration) - offset, 0);
        if (onlyWhenBefore && targetVideoTime <= CurrentPosition)
            return;

        Logger.Info("Skipping to script start at {0}s", targetVideoTime);
        SeekVideoToTime(targetVideoTime);
    }
    #endregion

    #region AxisSettings
    [SuppressPropertyChangedWarnings]
    public void OnAxisSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (sender is not AxisSettings settings)
            return;

        switch (e.PropertyName)
        {
            case nameof(ViewModels.AxisSettings.UpdateMotionProviderWhenPaused):
            case nameof(ViewModels.AxisSettings.UpdateMotionProviderWithoutScript):
            case nameof(ViewModels.AxisSettings.Inverted):
            case nameof(ViewModels.AxisSettings.SmartLimitEnabled):
            case nameof(ViewModels.AxisSettings.Bypass):
                var (axis, _) = AxisSettings.FirstOrDefault(x => x.Value == settings);
                if(axis != null)
                    ResetSync(true, axis);
                break;
        }
    }

    public void OnAxisDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, model) = pair;
        var drop = e.Data.GetData(DataFormats.FileDrop);
        if (drop is IEnumerable<string> paths)
        {
            var path = paths.FirstOrDefault(p => Path.GetExtension(p) == ".funscript");
            if (path == null)
                return;

            ResetSync(true, axis);
            SetScript(axis, ScriptFile.FromPath(path, userLoaded: true));
        }
    }

    public void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Link;
    }

    public void OnAxisOpenFolder(DeviceAxis axis)
    {
        var model = AxisModels[axis];
        if (model.Script == null)
            return;

        var source = model.Script.Source;
        var path = Path.GetDirectoryName(source);
        if (!Directory.Exists(path))
            return;

        Process.Start("explorer.exe", path);
    }

    public void OnAxisLoad(DeviceAxis axis)
    {
        var dialog = new CommonOpenFileDialog()
        {
            InitialDirectory = Directory.Exists(VideoFile?.Source) ? VideoFile.Source : string.Empty,
            EnsureFileExists = true
        };
        dialog.Filters.Add(new CommonFileDialogFilter("Funscript files", "*.funscript"));

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        ResetSync(true, axis);
        SetScript(axis, ScriptFile.FromFileInfo(new FileInfo(dialog.FileName), userLoaded: true));
    }

    public void OnAxisClear(DeviceAxis axis) => ResetScript(axis);
    public void OnAxisReload(DeviceAxis axis) => ReloadScript(axis);

    public void SetAxisValue(DeviceAxis axis, float value, bool offset = false)
    {
        if (axis == null)
            return;

        var state = AxisStates[axis];
        lock (state)
        {
            var lastValue = state.Value;

            if (offset)
            {
                if (!float.IsFinite(state.Value))
                    state.Value = axis.DefaultValue;

                state.Value = MathUtils.Clamp01(state.Value + value);
            }
            else
            {
                state.Value = value;
            }

            state.IsDirty = state.Value != lastValue;
        }
    }

    private bool MoveScript(DeviceAxis axis, DirectoryInfo directory)
    {
        if (directory?.Exists == false || AxisModels[axis].Script == null)
            return false;

        try
        {
            var source = AxisModels[axis].Script.Source;
            if (!File.Exists(source))
                return false;

            File.Move(source, Path.Join(directory.FullName, Path.GetFileName(source)));
        }
        catch { return false; }

        return true;
    }

    public void OnAxisMoveToVideo(DeviceAxis axis)
    {
        if (VideoFile != null && MoveScript(axis, new DirectoryInfo(VideoFile.Source)))
            ReloadScript(axis);
    }

    public RelayCommand<DeviceAxis, ScriptLibrary> OnAxisMoveToLibraryCommand => new(OnAxisMoveToLibrary);
    public void OnAxisMoveToLibrary(DeviceAxis axis, ScriptLibrary library)
    {
        if (MoveScript(axis, library?.Directory.AsRefreshed()))
            ReloadScript(axis);
    }

    [SuppressPropertyChangedWarnings]
    public void OnLinkAxisPriorityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, _) = pair;
        ReloadScript(axis);
    }

    [SuppressPropertyChangedWarnings]
    public void OnSelectedLinkAxisChanged(object sender, SelectionChangedEventArgs e)
    {
        bool IsCircularLink(DeviceAxis from, DeviceAxis to)
        {
            var current = from;
            while(current != null)
            {
                if (current == to)
                {
                    Logger.Info("Found circular link [From: {0}, To: {1}]", from, to);
                    return true;
                }

                current = AxisSettings[current].LinkAxis;
            }

            return false;
        }

        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, model) = pair;
        if (e.AddedItems.TryGet<DeviceAxis>(0, out var added) && IsCircularLink(added, axis))
            model.Settings.LinkAxis = e.RemovedItems.TryGet<DeviceAxis>(0, out var removed) ? removed : null;

        ReloadScript(axis);
    }

    [SuppressPropertyChangedWarnings]
    public void OnPreviewSelectedMotionProviderChanged(SelectionChangedEventArgs e)
    {
        if (e.Source is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, _) = pair;
        ResetSync(true, axis);
    }
    #endregion

    #region SyncSettings
    [SuppressPropertyChangedWarnings]
    public void OnSyncDurationSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue <= e.OldValue)
            return;

        foreach(var (axis, state) in AxisStates)
        {
            lock (state)
            {
                if(state.SyncTime >= e.OldValue)
                    state.SyncTime = (float)e.NewValue;
            }
        }
    }
    #endregion 

    #region MediaResource
    public async void OnVideoPathModifierConfigure(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IMediaPathModifier modifier)
            return;

        _ = await DialogHost.Show(modifier, "MediaPathModifierDialog").ConfigureAwait(true);

        if (VideoFile != null)
            Handle(new VideoFileChangedMessage(VideoFile.OriginalPath));
    }

    public void OnVideoPathModifierAdd(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<string, Type> pair)
            return;

        var (_, type) = pair;
        var modifier = (IMediaPathModifier)Activator.CreateInstance(type);
        VideoPathModifiers.Add(modifier);

        if(VideoFile != null)
            Handle(new VideoFileChangedMessage(VideoFile.OriginalPath));
    }

    public void OnVideoPathModifierRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IMediaPathModifier modifier)
            return;

        VideoPathModifiers.Remove(modifier);

        if (VideoFile != null)
            Handle(new VideoFileChangedMessage(VideoFile.OriginalPath));
    }

    public void OnMapCurrentVideoPathToFile(object sender, RoutedEventArgs e)
    {
        if (VideoFile == null || !VideoFile.IsUrl)
            return;

        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = false,
            EnsureFileExists = true
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        VideoPathModifiers.Add(new FindReplaceMediaPathModifierViewModel()
        {
            Find = VideoFile.OriginalPath,
            Replace = dialog.FileName
        });

        Handle(new VideoFileChangedMessage(VideoFile.OriginalPath));
    }
    #endregion

    #region ScriptLibrary
    public void OnLibraryAdd(object sender, RoutedEventArgs e)
    {
        //TODO: remove dependency once /dotnet/wpf/issues/438 is resolved
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        var directory = new DirectoryInfo(dialog.FileName);
        ScriptLibraries.Add(new ScriptLibrary(directory));
        ReloadScript(null);
    }

    public void OnLibraryDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ScriptLibrary library)
            return;

        ScriptLibraries.Remove(library);
        ReloadScript(null);
    }

    public void OnLibraryOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ScriptLibrary library)
            return;

        Process.Start("explorer.exe", library.Directory.FullName);
    }
    #endregion

    #region Shortcuts
    public void RegisterShortcuts(IShortcutManager s)
    {
        void UpdateSettings(DeviceAxis axis, Action<AxisSettings> callback)
        {
            if (axis != null)
                callback(AxisSettings[axis]);
        }

        #region Video::PlayPause
        s.RegisterAction("Video::PlayPause::Set", 
            b => b.WithSetting<bool>(p => p.WithLabel("Play"))
                  .WithSetting<DeviceAxis>(p => p.WithLabel("Axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, play, axis) =>
                  {
                      if (play && !IsPlaying) OnPlayPauseClick();
                      else if (!play && IsPlaying) OnPlayPauseClick();
                  }));

        s.RegisterAction("Video::PlayPause::Toggle", b => b.WithCallback(_ => OnPlayPauseClick()));
        #endregion

        #region Video::ScriptOffset
        s.RegisterAction("Video::ScriptOffset::Offset", b => b.WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}s"))
                                                              .WithCallback((_, offset) => GlobalOffset = MathUtils.Clamp(GlobalOffset + offset, -5, 5)));
        s.RegisterAction("Video::ScriptOffset::Set", b => b.WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}s"))
                                                           .WithCallback((_, value) => GlobalOffset = MathUtils.Clamp(value, -5, 5)));
        #endregion

        #region Video::Position
        s.RegisterAction("Video::Position::Time::Offset", b => b.WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}s"))
                                                                .WithCallback((_, offset) => SeekVideoToTime(CurrentPosition + offset)));
        s.RegisterAction("Video::Position::Time::Set", b => b.WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}s"))
                                                             .WithCallback((_, value) => SeekVideoToTime(value)));

        s.RegisterAction("Video::Position::Percent::Offset", b => b.WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}%"))
                                                                   .WithCallback((_, offset) => SeekVideoToPercent(CurrentPosition / VideoDuration + offset / 100)));
        s.RegisterAction("Video::Position::Percent::Set", b => b.WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}%"))
                                                                .WithCallback((_, value) => SeekVideoToPercent(value / 100)));

        s.RegisterAction("Video::Position::SkipToScriptStart", b => b.WithSetting<float>(p => p.WithLabel("Offset").WithStringFormat("{}{0}s"))
                                                                     .WithCallback((_, offset) => SeekVideoToScriptStart(offset, onlyWhenBefore: false)));
        #endregion

        #region Video::AutoSkipToScriptStartEnabled
        s.RegisterAction("Video::AutoSkipToScriptStartEnabled::Set",
            b => b.WithSetting<bool>(p => p.WithLabel("Auto-skip to script start enabled"))
                  .WithCallback((_, enabled) => AutoSkipToScriptStartEnabled = enabled));

        s.RegisterAction("Video::AutoSkipToScriptStartEnabled::Toggle",
            b => b.WithCallback(_ => AutoSkipToScriptStartEnabled = !AutoSkipToScriptStartEnabled));
        #endregion

        #region Video::AutoSkipToScriptStartOffset
        s.RegisterAction("Video::AutoSkipToScriptStartOffset::Set",
            b => b.WithSetting<float>(p => p.WithLabel("Script start auto-skip offset").WithStringFormat("{}{0}s"))
                  .WithCallback((_, offset) => AutoSkipToScriptStartOffset = offset));
        #endregion

        #region Script::SkipGap
        s.RegisterAction("Script::SkipGap",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target").WithItemsSource(DeviceAxis.All).WithDescription("Target axis script to check for gaps\nEmpty to check all scripts"))
                  .WithSetting<float>(p => p.WithLabel("Minimum skip").WithDefaultValue(2).WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, minimumSkip) => SkipGap(minimumSkip, axis)));
        #endregion

        #region Axis::Value
        s.RegisterAction("Axis::Value::Offset", 
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset"))
                  .WithCallback((_, axis, offset) => SetAxisValue(axis, offset, offset: true)));

        s.RegisterAction("Axis::Value::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value"))
                  .WithCallback((_, axis, value) => SetAxisValue(axis, value)));

        s.RegisterAction("Axis::Value::Drive", 
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((gesture, axis) =>
                  {
                      if (gesture is IAxisInputGesture axisGesture) 
                          SetAxisValue(axis, axisGesture.Delta, offset: true);
                  }), ShortcutActionDescriptorFlags.AcceptsAxisGesture);
        #endregion

        #region Axis::Sync
        s.RegisterAction("Axis::Sync",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => { if (axis != null) ResetSync(true, axis); }));

        s.RegisterAction("Axis::SyncAll",
            b => b.WithCallback(_ => ResetSync(true, null)));
        #endregion

        #region Axis::Bypass
        s.RegisterAction("Axis::Bypass::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Bypass"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.Bypass = enabled)));

        s.RegisterAction("Axis::Bypass::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.Bypass = !s.Bypass)));
        #endregion

        #region Axis::ClearScript
        s.RegisterAction("Axis::ClearScript",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => { if (axis != null) OnAxisClear(axis); }));
        #endregion

        #region Axis::ReloadScript
        s.RegisterAction("Axis::ReloadScript",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => { if (axis != null) OnAxisReload(axis); }));
        #endregion

        #region Axis::Inverted
        s.RegisterAction("Axis::Inverted::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Invert"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.Inverted = enabled)));

        s.RegisterAction("Axis::Inverted::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.Inverted = !s.Inverted)));
        #endregion

        #region Axis::LinkPriority
        s.RegisterAction("Axis::LinkPriority::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Link has priority").WithDescription("When enabled the link has priority\nover automatically loaded scripts"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.LinkAxisHasPriority = enabled)));

        s.RegisterAction("Axis::LinkPriority::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.LinkAxisHasPriority = !s.LinkAxisHasPriority)));
        #endregion

        #region Axis::SmartLimitEnabled
        s.RegisterAction("Axis::SmartLimitEnabled::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.Parse("R1", "R2")))
                  .WithSetting<bool>(p => p.WithLabel("Smart limit enabled"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.SmartLimitEnabled = enabled)));

        s.RegisterAction("Axis::SmartLimitEnabled::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.Parse("R1", "R2")))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.SmartLimitEnabled = !s.SmartLimitEnabled)));
        #endregion

        #region Axis::LinkAxis
        s.RegisterAction("Axis::LinkAxis::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Source axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, source, target) =>
                  {
                      if (source == null || source == target)
                          return;

                      AxisSettings[source].LinkAxis = target;
                      ReloadScript(source);
                  }));
        #endregion

        #region Axis::SpeedLimitEnabled
        s.RegisterAction("Axis::SpeedLimitEnabled::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Speed limit enabled"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.SpeedLimitEnabled = enabled)));

        s.RegisterAction("Axis::SpeedLimitEnabled::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.SpeedLimitEnabled = !s.SpeedLimitEnabled)));
        #endregion

        #region Axis::SpeedLimitSecondsPerStroke
        s.RegisterAction("Axis::SpeedLimitSecondsPerStroke::Offset",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0:F3}s/stroke"))
                  .WithCallback((_, axis, offset) => UpdateSettings(axis, s => s.MaximumSecondsPerStroke = MathUtils.Clamp(s.MaximumSecondsPerStroke + offset, 0.001f, 2f))));

        s.RegisterAction("Axis::SpeedLimitSecondsPerStroke::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0:F3}s/stroke"))
                  .WithCallback((_, axis, value) => UpdateSettings(axis, s => s.MaximumSecondsPerStroke = MathUtils.Clamp(value, 0.001f, 2f))));
        #endregion

        #region Axis::AutoHomeEnabled
        s.RegisterAction("Axis::AutoHomeEnabled::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Auto home enabled"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.AutoHomeEnabled = enabled)));

        s.RegisterAction("Axis::AutoHomeEnabled::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.AutoHomeEnabled = !s.AutoHomeEnabled)));
        #endregion

        #region Axis::AutoHomeDelay
        s.RegisterAction("Axis::AutoHomeDelay::Offset",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, offset) => UpdateSettings(axis, s => s.AutoHomeDelay = MathF.Max(0, s.AutoHomeDelay + offset))));

        s.RegisterAction("Axis::AutoHomeDelay::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, value) => UpdateSettings(axis, s => s.AutoHomeDelay = MathF.Max(0, value))));
        #endregion

        #region Axis::AutoHomeDuration
        s.RegisterAction("Axis::AutoHomeDuration::Offset",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, offset) => UpdateSettings(axis, s => s.AutoHomeDuration = MathF.Max(0, s.AutoHomeDuration + offset))));

        s.RegisterAction("Axis::AutoHomeDuration::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, value) => UpdateSettings(axis, s => s.AutoHomeDuration = MathF.Max(0, value))));
        #endregion

        #region Axis::ScriptOffset
        s.RegisterAction("Axis::ScriptOffset::Offset",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, offset) => UpdateSettings(axis, s => s.Offset = MathUtils.Clamp(s.Offset + offset, -5, 5))));

        s.RegisterAction("Axis::ScriptOffset::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}s"))
                  .WithCallback((_, axis, value) => UpdateSettings(axis, s => s.Offset = MathUtils.Clamp(value, -5, 5))));
        #endregion

        #region Axis::MotionProvider
        var motionProviderNames = MotionProviderManager.MotionProviderNames;
        s.RegisterAction("Axis::MotionProvider::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<string>(p => p.WithLabel("Motion provider").WithItemsSource(motionProviderNames))
                  .WithCallback((_, axis, motionProviderName) => 
                        UpdateSettings(axis, s => s.SelectedMotionProvider = motionProviderNames.Contains(motionProviderName) ? motionProviderName : null)));

        MotionProviderManager.RegisterShortcuts(s);
        #endregion

        #region Axis::MotionProviderBlend
        s.RegisterAction("Axis::MotionProviderBlend::Offset",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value offset"))
                  .WithCallback((_, axis, offset) => 
                        UpdateSettings(axis, s => s.MotionProviderBlend = MathUtils.Clamp(s.MotionProviderBlend + offset, 0, 100))));

        s.RegisterAction("Axis::MotionProviderBlend::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<float>(p => p.WithLabel("Value").WithStringFormat("{}{0}%"))
                  .WithCallback((_, axis, value) =>
                        UpdateSettings(axis, s => s.MotionProviderBlend = MathUtils.Clamp(value, 0, 100))));

        s.RegisterAction("Axis::MotionProviderBlend::Drive",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((gesture, axis) =>
                  {
                      if (gesture is IAxisInputGesture axisGesture)
                          UpdateSettings(axis, s => s.MotionProviderBlend = MathUtils.Clamp(s.MotionProviderBlend + axisGesture.Delta * 100, 0, 100));
                  }), ShortcutActionDescriptorFlags.AcceptsAxisGesture);
        #endregion

        #region Axis::UpdateMotionProviderWhenPaused
        s.RegisterAction("Axis::UpdateMotionProviderWhenPaused::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Update motion providers when paused"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.UpdateMotionProviderWhenPaused = enabled)));

        s.RegisterAction("Axis::UpdateMotionProviderWhenPaused::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.UpdateMotionProviderWhenPaused = !s.UpdateMotionProviderWhenPaused)));
        #endregion

        #region Axis::UpdateMotionProviderWithoutScript
        s.RegisterAction("Axis::UpdateMotionProviderWithoutScript::Set",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithSetting<bool>(p => p.WithLabel("Update motion providers without script"))
                  .WithCallback((_, axis, enabled) => UpdateSettings(axis, s => s.UpdateMotionProviderWithoutScript = enabled)));

        s.RegisterAction("Axis::UpdateMotionProviderWithoutScript::Toggle",
            b => b.WithSetting<DeviceAxis>(p => p.WithLabel("Target axis").WithItemsSource(DeviceAxis.All))
                  .WithCallback((_, axis) => UpdateSettings(axis, s => s.UpdateMotionProviderWithoutScript = !s.UpdateMotionProviderWithoutScript)));
        #endregion
    }
    #endregion

    protected virtual void Dispose(bool disposing)
    {
        _cancellationSource?.Cancel();
        _updateThread?.Join();
        _cancellationSource?.Dispose();

        _cancellationSource = null;
        _updateThread = null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

public enum AxisFilesChangeType
{
    Clear,
    Update
}

public class AxisModel : PropertyChangedBase
{
    public AxisState State { get; } = new AxisState();
    public AxisSettings Settings { get; } = new AxisSettings();
    public IScriptFile Script { get; set; } = null;

    public AxisModel()
    {
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    [SuppressPropertyChangedWarnings]
    private void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AxisSettings.SelectedMotionProvider)
         || e.PropertyName == nameof(AxisSettings.MotionProviderBlend))
            NotifyOfPropertyChange(nameof(MotionProviderOverridesScript));

        if (e.PropertyName == nameof(AxisSettings.LinkAxis)
         || e.PropertyName == nameof(AxisSettings.LinkAxisHasPriority))
            NotifyOfPropertyChange(nameof(LinkOverridesScript));
    }

    public bool MotionProviderOverridesScript => Script != null && Settings.SelectedMotionProvider != null && Settings.MotionProviderBlend > 50;
    public bool LinkOverridesScript => Script != null && Script.Origin == ScriptFileOrigin.Link && Settings.LinkAxis != null && Settings.LinkAxisHasPriority;
}

[DoNotNotify]
public class AxisState : INotifyPropertyChanged
{
    public int Index { get; set; } = int.MinValue;
    public float Value { get; set; } = float.NaN;

    public bool Invalid => Index == int.MinValue;
    public bool BeforeScript => Index == -1;
    public bool AfterScript => Index == int.MaxValue;
    public bool InsideScript => Index >= 0 && Index != int.MaxValue;

    public float SyncTime { get; set; } = 0;
    public float AutoHomeTime { get; set; } = 0;

    public bool IsDirty { get; set; } = true;
    public bool IsSpeedLimited { get; set; } = false;

    public event PropertyChangedEventHandler PropertyChanged;

    public void Invalidate(bool end = false) => Index = end ? int.MaxValue : int.MinValue;

    public void NotifyIsSpeedLimitedChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeedLimited)));
    public void NotifyValueChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InsideScript)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }
}

[JsonObject(MemberSerialization.OptIn)]
public class AxisSettings : PropertyChangedBase
{
    [JsonProperty] public bool LinkAxisHasPriority { get; set; } = false;
    [JsonProperty] public DeviceAxis LinkAxis { get; set; } = null;
    [JsonProperty] public bool SmartLimitEnabled { get; set; } = false;
    [JsonProperty] public InterpolationType InterpolationType { get; set; } = InterpolationType.Pchip;
    [JsonProperty] public bool AutoHomeEnabled { get; set; } = false;
    [JsonProperty] public float AutoHomeDelay { get; set; } = 5;
    [JsonProperty] public float AutoHomeDuration { get; set; } = 3;
    [JsonProperty] public bool Inverted { get; set; } = false;
    [JsonProperty] public float Offset { get; set; } = 0;
    [JsonProperty] public bool Bypass { get; set; } = false;
    [JsonProperty] public float MotionProviderBlend { get; set; } = 100;
    [JsonProperty] public bool UpdateMotionProviderWhenPaused { get; set; } = false;
    [JsonProperty] public bool UpdateMotionProviderWithoutScript { get; set; } = true;
    [JsonProperty] public DeviceAxis UpdateMotionProviderWithAxis { get; set; } = null;
    [JsonProperty] public string SelectedMotionProvider { get; set; } = null;
    [JsonProperty] public bool SpeedLimitEnabled { get; set; } = false;
    [JsonProperty] public float MaximumSecondsPerStroke { get; set; } = 0.1f;
}

[JsonObject(MemberSerialization.OptIn)]
public class SyncSettings : PropertyChangedBase
{
    [JsonProperty] public float Duration { get; set; } = 4;
    [JsonProperty] public bool SyncOnVideoFileChanged { get; set; } = true;
    [JsonProperty] public bool SyncOnVideoPlayPause { get; set; } = true;
    [JsonProperty] public bool SyncOnSeek { get; set; } = true;
}

[JsonObject(MemberSerialization.OptIn)]
public class ScriptLibrary : PropertyChangedBase
{
    public ScriptLibrary(DirectoryInfo directory)
    {
        Directory = directory;
    }

    [JsonProperty] public DirectoryInfo Directory { get; }
    [JsonProperty] public bool Recursive { get; set; }

    public IEnumerable<FileInfo> EnumerateFiles(string searchPattern) => Directory.SafeEnumerateFiles(searchPattern);
}
