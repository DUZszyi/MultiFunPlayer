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
using System.Windows.Controls.Primitives;
using MultiFunPlayer.Input;
using MultiFunPlayer.MotionProvider;
using MultiFunPlayer.VideoSource.MediaResource;
using MaterialDesignThemes.Wpf;
using System.Reflection;
using MultiFunPlayer.VideoSource.MediaResource.Modifier.ViewModels;

namespace MultiFunPlayer.UI.Controls.ViewModels;

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
public class ScriptViewModel : Screen, IDeviceAxisValueProvider, IDisposable,
    IHandle<VideoPositionMessage>, IHandle<VideoPlayingMessage>, IHandle<VideoFileChangedMessage>, IHandle<VideoDurationMessage>, IHandle<VideoSpeedMessage>, IHandle<AppSettingsMessage>
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
    public float GlobalOffset { get; set; }

    public ObservableConcurrentDictionary<DeviceAxis, AxisModel> AxisModels { get; set; }
    public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, AxisState> AxisStates { get; }
    public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, KeyframeCollection> AxisKeyframes { get; }

    public Dictionary<string, Type> VideoPathModifierTypes { get; }

    public MediaResourceInfo VideoFile { get; set; }

    [JsonProperty] public bool ValuesContentVisible { get; set; }
    [JsonProperty] public bool VideoContentVisible { get; set; } = true;
    [JsonProperty] public bool AxisContentVisible { get; set; } = false;
    [JsonProperty] public ObservableConcurrentDictionaryView<DeviceAxis, AxisModel, AxisSettings> AxisSettings { get; }
    [JsonProperty(ItemTypeNameHandling = TypeNameHandling.Objects)] public BindableCollection<IMediaPathModifier> VideoPathModifiers => _mediaResourceFactory.PathModifiers;
    [JsonProperty] public BindableCollection<ScriptLibrary> ScriptLibraries { get; }
    [JsonProperty] public SyncSettings SyncSettings { get; set; }
    [JsonProperty] public bool HeatmapShowStrokeLength { get; set; }
    [JsonProperty] public int HeatmapBucketCount { get; set; } = 333;

    public bool IsSyncing => AxisStates.Values.Any(s => s.SyncTime < SyncSettings.Duration);
    public float SyncProgress => !IsSyncing ? 100 : GetSyncProgress(AxisStates.Values.Min(s => s.SyncTime), SyncSettings.Duration) * 100;

    public ScriptViewModel(IShortcutManager shortcutManager, IMediaResourceFactory mediaResourceFactory, IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.Subscribe(this);

        _mediaResourceFactory = mediaResourceFactory;

        AxisModels = new ObservableConcurrentDictionary<DeviceAxis, AxisModel>(DeviceAxis.All.ToDictionary(a => a, _ => new AxisModel()));
        VideoPathModifierTypes = Assembly.GetExecutingAssembly()
                                         .GetTypes()
                                         .Where(t => t.IsClass && !t.IsAbstract)
                                         .Where(t => typeof(IMediaPathModifier).IsAssignableFrom(t))
                                         .Select(t => (IMediaPathModifier)Activator.CreateInstance(t))
                                         .ToDictionary(i => i.Name, i => i.GetType());

        ScriptLibraries = new BindableCollection<ScriptLibrary>();
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
        var autoHomeTimes = DeviceAxis.All.ToDictionary(a => a, _ => 0f);

        stopwatch.Start();

        while (!token.IsCancellationRequested)
        {
            var dirty = UpdateValues();
            UpdateUi();
            UpdateSync();

            stopwatch.Restart();
            Thread.Sleep(IsPlaying && dirty ? 2 : 10);
        }

        bool UpdateValues()
        {
            if (IsPlaying)
                CurrentPosition += (float)stopwatch.Elapsed.TotalSeconds * PlaybackSpeed * _playbackSpeedCorrection;

            var dirty = false;
            foreach (var axis in DeviceAxis.All)
            {
                var state = AxisStates[axis];
                var settings = AxisSettings[axis];

                lock (state)
                {
                    var oldValue = state.Value;
                    if (!settings.Bypass)
                    {
                        state.Dirty |= IsPlaying && UpdateScript(axis, state, settings);
                        state.Dirty |= UpdateMotionProvider(axis, state, settings);
                    }

                    if (state.SyncTime < SyncSettings.Duration)
                        state.Value = MathUtils.Lerp(!float.IsFinite(oldValue) ? axis.DefaultValue : oldValue, state.Value, GetSyncProgress(state.SyncTime, SyncSettings.Duration));

                    state.Dirty |= UpdateAutoHome(axis, state, settings);
                    state.Dirty |= UpdateSmartLimit(axis, state, settings);
                    dirty |= state.Dirty;

                    state.Dirty = false;
                }
            }

            return dirty;

            bool UpdateSmartLimit(DeviceAxis axis, AxisState state, AxisSettings settings)
            {
                if (!settings.SmartLimitEnabled)
                    return false;

                if (!DeviceAxis.TryParse("L0", out var strokeAxis))
                    return false;

                var limitState = AxisStates[strokeAxis];
                if (!limitState.InsideScript)
                    return false;

                var value = state.Value;
                var limitValue = limitState.Value;

                var factor = MathUtils.Map(limitValue, 0.25f, 0.9f, 1f, 0f);
                var lastValue = state.Value;
                state.Value = MathUtils.Lerp(axis.DefaultValue, state.Value, factor);
                return lastValue != state.Value;
            }

            bool UpdateScript(DeviceAxis axis, AxisState state, AxisSettings settings)
            {
                if (state.AfterScript)
                    return false;

                var lastValue = state.Value;
                if (!AxisKeyframes.TryGetValue(axis, out var keyframes) || keyframes == null || keyframes.Count == 0)
                    return false;

                var axisPosition = GetAxisPosition(axis);
                var beforeIndex = state.Index;
                while (state.Index + 1 >= 0 && state.Index + 1 < keyframes.Count && keyframes[state.Index + 1].Position < axisPosition)
                    state.Index++;

                if (beforeIndex == -1 && state.Index >= 0)
                    state.SyncTime = 0;

                if (!keyframes.ValidateIndex(state.Index) || !keyframes.ValidateIndex(state.Index + 1))
                {
                    if (state.Index + 1 >= keyframes.Count)
                    {
                        state.Invalidate(true);
                        state.SyncTime = 0;
                    }

                    return false;
                }

                var newValue = default(float);
                if (keyframes.IsRawCollection || state.Index == 0 || state.Index + 2 == keyframes.Count || settings.InterpolationType == InterpolationType.Linear)
                {
                    var p0 = keyframes[state.Index];
                    var p1 = keyframes[state.Index + 1];

                    newValue = MathUtils.Interpolate(p0.Position, p0.Value, p1.Position, p1.Value, axisPosition, InterpolationType.Linear);
                }
                else
                {
                    var p0 = keyframes[state.Index - 1];
                    var p1 = keyframes[state.Index + 0];
                    var p2 = keyframes[state.Index + 1];
                    var p3 = keyframes[state.Index + 2];

                    newValue = MathUtils.Interpolate(p0.Position, p0.Value, p1.Position, p1.Value, p2.Position, p2.Value, p3.Position, p3.Value,
                                                            axisPosition, settings.InterpolationType);
                }

                if (settings.Inverted)
                    newValue = 1 - newValue;

                state.Value = newValue;
                return lastValue != newValue;
            }

            bool UpdateMotionProvider(DeviceAxis axis, AxisState state, AxisSettings settings)
            {
                if (state.InsideScript && !IsPlaying)
                    return false;

                var lastValue = state.Value;
                var newValue = default(float);

                var motionProvider = settings.SelectedMotionProviderInstance;
                if (motionProvider == null)
                    return false;

                motionProvider.Update();
                newValue = motionProvider.Value;
                if (state.InsideScript)
                    newValue = MathUtils.Lerp(state.Value, newValue, MathUtils.Clamp01(settings.MotionProviderBlend / 100));

                state.Value = newValue;
                return lastValue != newValue;
            }

            bool UpdateAutoHome(DeviceAxis axis, AxisState state, AxisSettings settings)
            {
                if (state.Dirty)
                {
                    autoHomeTimes[axis] = 0;
                    return false;
                }

                if (!float.IsFinite(state.Value))
                    return false;

                if (!settings.AutoHomeEnabled)
                    return false;

                if (settings.AutoHomeDuration < 0.0001f)
                {
                    var lastValue = state.Value;
                    state.Value = axis.DefaultValue;
                    return lastValue != state.Value;
                }

                autoHomeTimes[axis] += (float)stopwatch.Elapsed.TotalSeconds;

                var t = autoHomeTimes[axis] - settings.AutoHomeDelay;
                if (t >= 0 && t / settings.AutoHomeDuration <= 1)
                {
                    var lastValue = state.Value;
                    state.Value = MathUtils.Lerp(state.Value, axis.DefaultValue, MathF.Pow(2, 10 * (t / settings.AutoHomeDuration - 1)));
                    return lastValue != state.Value;
                }

                return false;
            }
        }

        void UpdateUi()
        {
            uiUpdateTime += (float)stopwatch.Elapsed.TotalSeconds;
            if (uiUpdateTime < uiUpdateInterval)
                return;

            uiUpdateTime = 0;
            if (ValuesContentVisible)
            {
                Execute.OnUIThread(() =>
                {
                    foreach (var axis in DeviceAxis.All)
                        AxisStates[axis].Notify();
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
                    if (state.InsideScript && !IsPlaying)
                        continue;

                    if (state.SyncTime >= SyncSettings.Duration)
                        continue;

                    state.SyncTime += (float)stopwatch.Elapsed.TotalSeconds;
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

        if (!IsPlaying && message.IsPlaying)
            if (SyncSettings.SyncOnVideoResume)
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
        var newPosition = (float)(message.Position?.TotalSeconds ?? float.NaN);
        Logger.Trace("Received VideoPositionMessage [Position: {0}]", message.Position?.ToString());

        var error = float.IsFinite(CurrentPosition) ? newPosition - CurrentPosition : 0;
        var wasSeek = MathF.Abs(error) > 1.0f;
        if (wasSeek)
        {
            Logger.Debug("Detected seek: {0}", error);
            if (SyncSettings.SyncOnSeek)
                ResetSync();

            _playbackSpeedCorrection = 1;
        }
        else
        {
            _playbackSpeedCorrection = MathUtils.Clamp(_playbackSpeedCorrection + error * 0.1f, 0.9f, 1.1f);
        }

        CurrentPosition = newPosition;
        if (!float.IsFinite(CurrentPosition))
            return;

        foreach (var axis in DeviceAxis.All)
        {
            var state = AxisStates[axis];
            if (wasSeek || state.Invalid)
                SearchForValidIndex(axis, state);
        }
    }

    public void Handle(AppSettingsMessage message)
    {
        if (message.Type == AppSettingsMessageType.Saving)
        {
            message.Settings["Script"] = JObject.FromObject(this);
        }
        else if (message.Type == AppSettingsMessageType.Loading)
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
                foreach (var modifier in videoPathModifiers)
                    VideoPathModifiers.Add(modifier);
            }

            if (settings.TryGetValue<List<ScriptLibrary>>(nameof(ScriptLibraries), out var scriptDirectories))
            {
                foreach (var library in scriptDirectories)
                    ScriptLibraries.Add(library);
            }

            if (settings.TryGetValue<bool>(nameof(ValuesContentVisible), out var valuesContentVisible)) ValuesContentVisible = valuesContentVisible;
            if (settings.TryGetValue<bool>(nameof(VideoContentVisible), out var videoContentVisible)) VideoContentVisible = videoContentVisible;
            if (settings.TryGetValue<bool>(nameof(AxisContentVisible), out var axisContentVisible)) AxisContentVisible = axisContentVisible;
            if (settings.TryGetValue<int>(nameof(HeatmapBucketCount), out var heatmapBucketCount)) HeatmapBucketCount = heatmapBucketCount;
            if (settings.TryGetValue<bool>(nameof(HeatmapShowStrokeLength), out var heatmapShowStrokeLength)) HeatmapShowStrokeLength = heatmapShowStrokeLength;

            if (settings.TryGetValue(nameof(SyncSettings), out var syncSettingsToken)) syncSettingsToken.Populate(SyncSettings);
        }
    }
    #endregion

    #region Common
    private void SearchForValidIndex(DeviceAxis axis, AxisState state)
    {
        if (!AxisKeyframes.TryGetValue(axis, out var keyframes) || keyframes == null || keyframes.Count == 0)
            return;

        Logger.Debug("Searching for valid index [Axis: {0}]", axis);
        lock (state)
            state.Index = keyframes.BinarySearch(GetAxisPosition(axis));
    }

    private List<DeviceAxis> UpdateLinkScript(params DeviceAxis[] axes) => UpdateLinkScript(axes?.AsEnumerable());
    private List<DeviceAxis> UpdateLinkScript(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        Logger.Debug("Trying to link axes [Axes: {list}]", axes);

        var updated = new List<DeviceAxis>();
        foreach (var axis in axes)
        {
            var model = AxisModels[axis];
            if (model.Settings.LinkAxis == null)
            {
                if (model.Settings.LinkAxisHasPriority)
                {
                    ResetScript(axis);
                    updated.Add(axis);
                }

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
            updated.Add(axis);
        }

        return updated;
    }

    private void ResetScript(params DeviceAxis[] axes) => ResetScript(axes?.AsEnumerable());
    private void ResetScript(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        Logger.Debug("Resetting axes [Axes: {list}]", axes);
        foreach (var axis in axes)
        {
            Logger.Debug("Reset {0} script", axis);
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
    }

    private void ReloadScript(params DeviceAxis[] axes) => ReloadScript(axes?.AsEnumerable());
    private void ReloadScript(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;
        ResetSync(true, axes);

        Logger.Debug("Reloading axes [Axes: {list}]", axes);
        foreach (var (enabled, items) in axes.GroupBy(a => AxisModels[a].Settings.LinkAxisHasPriority))
        {
            var groupAxes = items.ToArray();
            if (enabled)
            {
                UpdateLinkScript(groupAxes);
            }
            else
            {
                var updated = TryMatchFiles(true, groupAxes);
                UpdateLinkScript(groupAxes.Except(updated));
            }
        }
    }

    private void InvalidateState(params DeviceAxis[] axes) => InvalidateState(axes?.AsEnumerable());
    private void InvalidateState(IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        Logger.Debug("Invalidating axes [Axes: {list}]", axes);
        foreach (var axis in axes)
        {
            var state = AxisStates[axis];
            lock (state)
                state.Invalidate();
        }
    }

    private List<DeviceAxis> TryMatchFiles(bool overwrite, params DeviceAxis[] axes) => TryMatchFiles(overwrite, axes?.AsEnumerable());
    private List<DeviceAxis> TryMatchFiles(bool overwrite, IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        Logger.Debug("Maching files to axes [Axes: {list}]", axes);

        var updated = new List<DeviceAxis>();
        if (VideoFile == null)
            return updated;

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

    private float GetAxisPosition(DeviceAxis axis) => CurrentPosition - GlobalOffset - AxisSettings[axis].Offset;
    public float GetValue(DeviceAxis axis) => MathUtils.Clamp01(AxisStates[axis].Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetSyncProgress(float time, float duration) => MathF.Pow(2, 10 * (time / duration - 1));

    private void ResetSync(bool isSyncing = true, params DeviceAxis[] axes) => ResetSync(isSyncing, axes?.AsEnumerable());
    private void ResetSync(bool isSyncing = true, IEnumerable<DeviceAxis> axes = null)
    {
        axes ??= DeviceAxis.All;

        Logger.Debug("Resetting sync");

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
    #endregion

    #region AxisSettings
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

        Process.Start("explorer.exe", model.Script.Source.DirectoryName);
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

    public void SetAxisBypass(DeviceAxis axis, bool value)
    {
        var current = AxisSettings[axis].Bypass;

        AxisSettings[axis].Bypass = value;
        if (current && !value)
            ResetSync(true, axis);
    }

    public void SetAxisInverted(DeviceAxis axis, bool value)
    {
        var current = AxisSettings[axis].Inverted;

        AxisSettings[axis].Inverted = value;
        if (current && !value)
            ResetSync(true, axis);
    }

    public void SetAxisValue(DeviceAxis axis, float value, bool offset = false)
    {
        var state = AxisStates[axis];
        lock (state)
        {
            float lastValue = state.Value;

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

            state.Dirty = state.Value != lastValue;
        }
    }

    private bool MoveScript(DeviceAxis axis, DirectoryInfo directory)
    {
        if (directory?.Exists == false || AxisModels[axis].Script == null)
            return false;

        try
        {
            var sourceFile = AxisModels[axis].Script.Source;
            File.Move(sourceFile.FullName, Path.Join(directory.FullName, sourceFile.Name));
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
    public void OnInvertedCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, _) = pair;
        ResetSync(true, axis);
    }

    [SuppressPropertyChangedWarnings]
    public void OnBypassCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, _) = pair;
        SetAxisBypass(axis, button.IsChecked.GetValueOrDefault());
    }

    [SuppressPropertyChangedWarnings]
    public void OnSelectedLinkAxisChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, model) = pair;
        if (e.AddedItems.TryGet<DeviceAxis>(0, out var added) && added == axis)
            model.Settings.LinkAxis = e.RemovedItems.TryGet<DeviceAxis>(0, out var removed) ? removed : null;

        ReloadScript(axis);
    }

    [SuppressPropertyChangedWarnings]
    public void OnSmartLimitCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<DeviceAxis, AxisModel> pair)
            return;

        var (axis, _) = pair;
        ResetSync(true, axis);
    }
    #endregion

    #region MediaResource
    public async void OnVideoPathModifierConfigure(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IMediaPathModifier modifier)
            return;

        _ = await DialogHost.Show(modifier, "MediaPathModifierDialog").ConfigureAwait(true);
    }

    public void OnVideoPathModifierAdd(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not KeyValuePair<string, Type> pair)
            return;

        var (_, type) = pair;
        var modifier = (IMediaPathModifier)Activator.CreateInstance(type);
        VideoPathModifiers.Add(modifier);
    }

    public void OnVideoPathModifierRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IMediaPathModifier modifier)
            return;

        VideoPathModifiers.Remove(modifier);
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
        #region Video::PlayPause
        s.RegisterAction<bool>("Video::PlayPause::Set", "Play", (_, play) =>
        {
            if (play && !IsPlaying) OnPlayPauseClick();
            else if (!play && IsPlaying) OnPlayPauseClick();
        });

        s.RegisterAction("Video::PlayPause::Toggle", _ => OnPlayPauseClick());
        #endregion

        #region Video::ScriptOffset
        s.RegisterAction<float>("Video::ScriptOffset::Offset", "Value offset", (_, offset) => GlobalOffset = MathUtils.Clamp(GlobalOffset + offset, -5, 5));
        s.RegisterAction<float>("Video::ScriptOffset::Set", "Value", (_, value) => GlobalOffset = MathUtils.Clamp(value, -5, 5));
        #endregion

        #region Video::Position
        s.RegisterAction<float>("Video::Position::Time::Offset", "Value offset", (_, offset) => SeekVideoToTime(CurrentPosition + offset));
        s.RegisterAction<float>("Video::Position::Time::Set", "Value", (_, value) => SeekVideoToTime(value));

        s.RegisterAction<float>("Video::Position::Percent::Offset", "Value offset", (_, offset) => SeekVideoToPercent(CurrentPosition / VideoDuration + offset / 100));
        s.RegisterAction<float>("Video::Position::Percent::Set", "Value", (_, value) => SeekVideoToPercent(value / 100));
        #endregion

        #region Axis::Value
        s.RegisterAction<DeviceAxis, float>("Axis::Value::Offset", "Target axis", "Value offset", (_, axis, offset) =>
        {
            if (axis != null)
                SetAxisValue(axis, offset, offset: true);
        });

        s.RegisterAction<DeviceAxis, float>("Axis::Value::Set", "Target axis", "Value", (_, axis, value) =>
        {
            if (axis != null)
                SetAxisValue(axis, value);
        });

        s.RegisterAction<DeviceAxis>("Axis::Value::Drive", "Target axis", (gesture, axis) =>
        {
            if (gesture is not IAxisInputGesture axisGesture) return;
            if (axis != null)
                SetAxisValue(axis, axisGesture.Delta, offset: true);
        });
        #endregion

        #region Axis::Sync
        s.RegisterAction<DeviceAxis>("Axis::Sync", "Target axis", (_, axis) =>
        {
            if (axis != null)
                ResetSync(true, axis);
        });
        #endregion

        #region Axis::Bypass
        s.RegisterAction<DeviceAxis, bool>("Axis::Bypass::Set", "Target axis", "Bypass", (_, axis, enabled) =>
        {
            if (axis != null)
                SetAxisBypass(axis, enabled);
        });

        s.RegisterAction<DeviceAxis>("Axis::Bypass::Toggle", "Target axis", (_, axis) =>
        {
            if (axis != null)
                SetAxisBypass(axis, !AxisSettings[axis].Bypass);
        });
        #endregion

        #region Axis::ClearScript
        s.RegisterAction<DeviceAxis>("Axis::ClearScript", "Target axis", (_, axis) =>
        {
            if (axis != null)
                OnAxisClear(axis);
        });
        #endregion

        #region Axis::ReloadScript
        s.RegisterAction<DeviceAxis>("Axis::ReloadScript", "Target axis", (_, axis) =>
        {
            if (axis != null)
                OnAxisReload(axis);
        });
        #endregion

        #region Axis::Inverted
        s.RegisterAction<DeviceAxis, bool>("Axis::Inverted::Set", "Target axis", "Invert", (_, axis, enabled) =>
        {
            if (axis != null)
                SetAxisInverted(axis, enabled);
        });

        s.RegisterAction<DeviceAxis>("Axis::Inverted::Toggle", "Target axis", (_, axis) =>
        {
            if (axis != null)
                SetAxisInverted(axis, !AxisSettings[axis].Inverted);
        });
        #endregion

        #region Axis::LinkPriority
        s.RegisterAction<DeviceAxis, bool>("Axis::LinkPriority::Set", "Target axis", "Link has priority", (_, axis, enabled) =>
        {
            if (axis != null)
                AxisSettings[axis].LinkAxisHasPriority = enabled;
        });

        s.RegisterAction<DeviceAxis>("Axis::LinkPriority::Toggle", "Target axis", (_, axis) =>
        {
            if (axis != null)
                AxisSettings[axis].LinkAxisHasPriority = !AxisSettings[axis].LinkAxisHasPriority;
        });
        #endregion

        #region Axis::SmartLimitEnabled
        s.RegisterAction<DeviceAxis, bool>("Axis::SmartLimitEnabled::Set", "Target axis", "Link has priority", (_, axis, enabled) =>
        {
            if (axis == null || (axis.Name != "R1" && axis.Name != "R2"))
                return;

            AxisSettings[axis].SmartLimitEnabled = enabled;
        });

        s.RegisterAction<DeviceAxis>("Axis::SmartLimitEnabled::Toggle", "Target axis", (_, axis) =>
        {
            if (axis != null)
                AxisSettings[axis].SmartLimitEnabled = !AxisSettings[axis].SmartLimitEnabled;
        });
        #endregion

        #region Axis::LinkAxis
        s.RegisterAction<DeviceAxis, DeviceAxis>("Axis::LinkAxis::Set", "Source axis", "Target axis", (_, source, target) =>
        {
            if (source == null)
                return;

            AxisSettings[source].LinkAxis = target;
            ReloadScript(source);
        });
        #endregion

        #region Axis::AutoHomeEnabled
        s.RegisterAction<DeviceAxis, bool>("Axis::AutoHomeEnabled::Set", "Target axis", "Auto home enabled", (_, axis, enabled) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeEnabled = enabled;
        });

        s.RegisterAction<DeviceAxis>("Axis::AutoHomeEnabled::Toggle", "Target axis", (_, axis) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeEnabled = !AxisSettings[axis].AutoHomeEnabled;
        });
        #endregion

        #region Axis::AutoHomeDelay
        s.RegisterAction<DeviceAxis, float>("Axis::AutoHomeDelay::Offset", "Target axis", "Value offset", (_, axis, offset) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeDelay = MathF.Max(0, AxisSettings[axis].AutoHomeDelay + offset);
        });

        s.RegisterAction<DeviceAxis, float>("Axis::AutoHomeDelay::Set", "Target axis", "Value", (_, axis, value) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeDelay = MathF.Max(0, value);
        });
        #endregion

        #region Axis::AutoHomeDuration
        s.RegisterAction<DeviceAxis, float>("Axis::AutoHomeDuration::Offset", "Target axis", "Value offset", (_, axis, offset) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeDuration = MathF.Max(0, AxisSettings[axis].AutoHomeDuration + offset);
        });

        s.RegisterAction<DeviceAxis, float>("Axis::AutoHomeDuration::Set", "Target axis", "Value", (_, axis, value) =>
        {
            if (axis != null)
                AxisSettings[axis].AutoHomeDuration = MathF.Max(0, value);
        });
        #endregion

        #region Axis::ScriptOffset
        s.RegisterAction<DeviceAxis, float>("Axis::ScriptOffset::Offset", "Target axis", "Value offset", (_, axis, offset) =>
        {
            if (axis != null)
                AxisSettings[axis].Offset = MathUtils.Clamp(AxisSettings[axis].Offset + offset, -5, 5);
        });

        s.RegisterAction<DeviceAxis, float>("Axis::ScriptOffset::Set", "Target axis", "Value", (_, axis, value) =>
        {
            if (axis != null)
                AxisSettings[axis].Offset = MathUtils.Clamp(value, -5, 5);
        });
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
}

[DoNotNotify]
public class AxisState : INotifyPropertyChanged
{
    public int Index { get; set; } = int.MinValue;
    public float Value { get; set; } = float.NaN;
    public bool Dirty { get; set; } = true;
    public float SyncTime { get; set; } = 0;

    public bool Invalid => Index == int.MinValue;
    public bool BeforeScript => Index == -1;
    public bool AfterScript => Index == int.MaxValue;
    public bool InsideScript => Index >= 0 && Index != int.MaxValue;

    public event PropertyChangedEventHandler PropertyChanged;

    public void Invalidate(bool end = false) => Index = end ? int.MaxValue : int.MinValue;

    public void Notify()
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

    [JsonProperty]
    [AlsoNotifyFor(nameof(SelectedMotionProviderInstance))]
    public string SelectedMotionProvider { get; set; } = null;

    [AlsoNotifyFor(nameof(SelectedMotionProviderInstance))]
    [JsonProperty(ItemTypeNameHandling = TypeNameHandling.Objects)]
    public MotionProviderCollection MotionProviders { get; set; } = new MotionProviderCollection();

    public IMotionProvider SelectedMotionProviderInstance => MotionProviders[SelectedMotionProvider];
}

[JsonObject(MemberSerialization.OptIn)]
public class SyncSettings : PropertyChangedBase
{
    [JsonProperty] public float Duration { get; set; } = 4;
    [JsonProperty] public bool SyncOnVideoFileChanged { get; set; } = true;
    [JsonProperty] public bool SyncOnVideoResume { get; set; } = true;
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