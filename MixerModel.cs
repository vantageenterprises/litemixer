using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using LiteMixer.Audio;

namespace LiteMixer;

/// <summary>
/// The mixer: channels are groups of apps. A channel's volume scales each member app's Windows session
/// volume, and its output device is applied per app through Windows' own per-app routing.
/// </summary>
public sealed class MixerModel : ObservableObject, IDisposable
{
    static readonly TimeSpan FullPollInterval = TimeSpan.FromSeconds(2);
    // After startup or a device plug/unplug, saved default devices win over whatever Windows picked.
    static readonly TimeSpan DeviceGrace = TimeSpan.FromSeconds(8);
    // Volume increases larger than this ramp up when fade-in is on.
    const double FadeThreshold = 8;
    // Chat louder than this (about -36 dBFS) counts as someone talking for auto-duck.
    const double TalkThreshold = 0.015;
    static readonly TimeSpan DuckHold = TimeSpan.FromMilliseconds(600);
    const string CustomGlyph = "";
    // New channels take the first color not already used, so they stay easy to tell apart.
    static readonly string[] CustomColors =
        ["#22D3EE", "#A3E635", "#FB7185", "#FACC15", "#E879F9", "#FB923C", "#C4B5FD", "#2DD4BF", "#F9A8D4", "#BEF264", "#7DD3FC", "#FDBA74"];

    readonly AppSettings _s;
    readonly AudioEngine _engine = new();
    readonly DispatcherTimer _tick;
    readonly DispatcherTimer _meterTimer;
    readonly DispatcherTimer _saveTimer;
    readonly DispatcherTimer _rampTimer;
    readonly DispatcherTimer _duckTimer;
    readonly List<ChannelVM> _outputs;
    readonly List<ChannelVM> _cappable;
    readonly Dictionary<string, ChannelVM> _byId;
    readonly HashSet<string> _knownSessions = new();
    // App key to the channel id sorting picked. Rebuilt each run, so better rules apply to apps the user hasn't placed.
    readonly Dictionary<string, string> _sorted = new();
    readonly HashSet<uint> _micFollowed = new();
    StreamMix? _stream;
    HashSet<uint>? _streamPids;
    DateTime _streamRetryAt;
    readonly Dictionary<uint, string> _routed = new();
    // The level actually applied to sessions; trails the fader while a fade-in ramp runs.
    readonly Dictionary<ChannelVM, double> _applied = new();

    List<SessionInfo> _sessions = new();
    List<SessionInfo> _micSessions = new();
    string _deviceSignature = "";
    DateTime _graceUntil;
    DateTime _lastPoll;
    DateTime _lastTalk;
    bool _micRestorePending;
    bool? _lastMicMuted;
    double _balance;
    double _duck = 1;
    double[] _peaks = [];

    public MixerModel(AppSettings settings)
    {
        _s = settings;
        HotkeyActions.FillDefaults(_s);

        Master = new("master", "MASTER", "", "#5B6CF5", ChannelKind.Master);
        Game = new("game", "GAME", "", "#1FCB9C", ChannelKind.Output);
        Chat = new("chat", "CHAT", "", "#3BA4F0", ChannelKind.Output);
        // Video and Music keep the ids "media" and "aux", so saved levels, routing and assignments carry over.
        Media = new("media", "VIDEO","", "#F2468E", ChannelKind.Output);
        Aux = new("aux", "MUSIC","", "#9D5CF5", ChannelKind.Output);
        Mic = new("mic", "MIC", "", "#F5A623", ChannelKind.Mic);
        var customs = _s.CustomChannels.Select(c => new ChannelVM(c.Id, c.Name, CustomGlyph, c.Color, ChannelKind.Output, c.Name)).ToList();
        _outputs = InSavedOrder([Game, Chat, Media, .. customs, Aux]);
        _cappable = [Master, .. _outputs];
        Channels = new([.. _cappable, Mic]);
        ScrollChannels = new(_outputs);
        _byId = Channels.ToDictionary(c => c.Id);

        foreach (var channel in _cappable)
            if (_s.Caps.TryGetValue(channel.Id, out var cap)) channel.Cap = cap;
        foreach (var channel in Channels)
            if (_s.Looks.TryGetValue(channel.Id, out var look)) channel.SetLook(look.Color, look.Glyph);

        Master.SetSilently(_s.MasterVolume, _s.MasterMuted);
        foreach (var channel in _outputs)
        {
            var cs = SettingsFor(channel);
            channel.SetSilently(cs.Volume, cs.Muted);
        }
        Mic.SetSilently(_s.MicVolume >= 0 ? _s.MicVolume : 100, _s.MicMuted ?? false);
        foreach (var channel in CappableChannels) _applied[channel] = channel.Volume;
        _balance = Math.Clamp(_s.Balance, -100, 100);
        UpdateDefaultHint();

        foreach (var channel in Channels)
        {
            channel.VolumeChanged = OnVolumeChanged;
            channel.MutedChanged = OnMutedChanged;
        }
        foreach (var channel in _outputs) WireStream(channel);

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) =>
        {
            if (_engine.Dirty || DateTime.UtcNow - _lastPoll >= FullPollInterval) Poll();
        };
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _rampTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(25) };
        _rampTimer.Tick += (_, _) => RampTick();
        _duckTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        _duckTimer.Tick += (_, _) => DuckTick();
    }

    public AppSettings Settings => _s;
    public ChannelVM Master { get; }
    public ChannelVM Game { get; }
    public ChannelVM Chat { get; }
    public ChannelVM Media { get; }
    public ChannelVM Aux { get; }
    public ChannelVM Mic { get; }
    /// <summary>Every channel in display order: Master first, Mic last.</summary>
    public ObservableCollection<ChannelVM> Channels { get; }
    /// <summary>The channels in the scrolling area between the pinned Master and Mic.</summary>
    public ObservableCollection<ChannelVM> ScrollChannels { get; }
    public IReadOnlyList<ChannelVM> CappableChannels => _cappable;
    public IReadOnlyList<ChannelVM> OutputChannels => _outputs;
    /// <summary>Raised when a custom channel is added, renamed or deleted.</summary>
    public event Action? ChannelsChanged;
    public IReadOnlyList<DeviceInfo> RenderDevices { get; private set; } = [];
    public IReadOnlyList<DeviceInfo> CaptureDevices { get; private set; } = [];
    public string? DefaultOutputId { get; private set; }
    public string? DefaultInputId { get; private set; }

    /// <summary>Raised when the mic mute state changes; the flag says whether this app caused it.</summary>
    public event Action<bool, bool>? MicMuteChanged;
    public event Action? ProfilesChanged;

    /// <summary>-100 is full game, +100 is full chat, 0 is both at 100%.</summary>
    public double Balance
    {
        get => _balance;
        set
        {
            if (!Set(ref _balance, Math.Clamp(Math.Round(value), -100, 100))) return;
            _s.Balance = _balance;
            ApplyChannel(Game);
            ApplyChannel(Chat);
            QueueSave();
        }
    }

    public bool MetersEnabled
    {
        set
        {
            _meterTimer.IsEnabled = value;
            if (!value)
                foreach (var channel in Channels)
                {
                    channel.Peak = 0;
                    channel.StreamPeak = 0;
                }
        }
    }

    public bool DuckEnabled
    {
        get => _s.DuckEnabled;
        set
        {
            if (_s.DuckEnabled == value) return;
            _s.DuckEnabled = value;
            OnPropertyChanged();
            RefreshDuck();
            QueueSave();
        }
    }

    public void Start()
    {
        _graceUntil = DateTime.UtcNow + DeviceGrace;
        Poll();
        _tick.Start();
        RefreshDuck();
    }

    // ---- Public actions (UI) ----

    /// <summary>Moves an app to an output channel and remembers that choice. Master and Mic can't hold apps.</summary>
    public void Assign(string key, ChannelVM target)
    {
        if (target.Kind != ChannelKind.Output) return;
        _s.Assignments[key] = target.Id;

        foreach (var session in _sessions.Where(s => s.Key == key))
        {
            ApplyVolume(session);
            Route(session, force: true);
        }
        UpdateApps();
        QueueSave();
    }

    /// <summary>Moves every app in this channel to the default channel, and remembers that, so sorting doesn't send them back.</summary>
    public void UnassignChannel(ChannelVM channel)
    {
        var target = DefaultChannel;
        if (channel == target) return;
        var keys = _s.Assignments.Where(a => a.Value == channel.Id).Select(a => a.Key)
            .Concat(_sessions.Where(s => ChannelFor(s.Key) == channel).Select(s => s.Key))
            .Distinct()
            .ToList();
        foreach (var key in keys) Assign(key, target);
    }

    /// <summary>Always a real output channel. A missing or invalid saved default means Game.</summary>
    public ChannelVM DefaultChannel =>
        _byId.TryGetValue(_s.DefaultChannel ?? "", out var channel) && channel.Kind == ChannelKind.Output ? channel : Game;

    public void SetDefaultChannel(ChannelVM channel)
    {
        if (channel.Kind != ChannelKind.Output) return;
        _s.DefaultChannel = channel.Id;
        ReapplyUnplacedApps();
        QueueSave();
    }

    public bool AutoSortApps
    {
        get => _s.AutoSortApps;
        set
        {
            if (_s.AutoSortApps == value) return;
            _s.AutoSortApps = value;
            if (value)
            {
                foreach (var session in _sessions) SortApp(session.Key, session.Path);
                _micFollowed.Clear();
            }
            ReapplyUnplacedApps();
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---- Stream mix ----

    public event Action? StreamChanged;

    DateTime _nextStreamAppScan;
    string? _streamAppKey;
    string? _streamAppName;
    System.Windows.Media.ImageSource? _streamAppIcon;
    double[] _streamPeaks = [];

    public bool StreamMixEnabled
    {
        get => _s.StreamMixEnabled;
        set
        {
            if (_s.StreamMixEnabled == value) return;
            _s.StreamMixEnabled = value;
            // Turning it on with nothing chosen picks a virtual cable, never a real output.
            if (value && _s.StreamDeviceId == null)
                _s.StreamDeviceId = StreamDeviceChoices.Where(d => StreamMath.LooksLikeCable(d.Name)).OrderBy(d => StreamMath.CableRank(d.Name)).FirstOrDefault()?.Id;
            foreach (var channel in _outputs)
            {
                channel.ShowStream = value;
                channel.StreamPeak = 0;
            }
            _nextStreamAppScan = default;
            if (value) UpdateStreamApp();
            UpdateStream();
            QueueSave();
        }
    }

    /// <summary>Gives an output channel its stream fader, matching the settings.</summary>
    void WireStream(ChannelVM channel)
    {
        channel.SetStreamSilently(StreamLevel(channel), _s.StreamMuted.Contains(channel.Id));
        channel.StreamVolumeChanged = c => SetStreamLevel(c, c.StreamVolume);
        channel.StreamMutedChanged = c => SetStreamMuted(c, c.StreamMuted);
        channel.ShowStream = _s.StreamMixEnabled;
        channel.SetStreamApp(_streamAppName, _streamAppIcon);
    }

    /// <summary>Finds the open streaming app, for the icon above the stream faders. A process list every few seconds, while the mix is on.</summary>
    void UpdateStreamApp()
    {
        if (DateTime.UtcNow < _nextStreamAppScan) return;
        _nextStreamAppScan = DateTime.UtcNow.AddSeconds(3);
        var found = StreamMix.FindBroadcastApp();
        if (found?.Key == _streamAppKey) return;
        _streamAppKey = found?.Key;
        _streamAppName = found is { } app ? StreamMath.BroadcastName(app.Key) : null;
        _streamAppIcon = found is { } open ? Icons.ForExe(AudioEngine.QueryProcessPath(open.Pid)) : null;
        foreach (var channel in _outputs) channel.SetStreamApp(_streamAppName, _streamAppIcon);
    }

    /// <summary>Output devices the stream mix may use: everything the user isn't listening on.</summary>
    public IEnumerable<DeviceInfo> StreamDeviceChoices => RenderDevices.Where(d => !ListeningDeviceIds().Contains(d.Id));
    public string StreamDeviceName => RenderDevices.FirstOrDefault(d => d.Id == _s.StreamDeviceId)?.Name ?? "Choose a device";
    public bool HasVirtualCable => RenderDevices.Any(d => StreamMath.LooksLikeCable(d.Name));

    public string StreamStatus { get; private set; } = "";

    public void SetStreamDevice(DeviceInfo device)
    {
        if (_s.StreamDeviceId == device.Id) return;
        _s.StreamDeviceId = device.Id;
        StopStream();
        UpdateStream();
        QueueSave();
    }

    public double StreamLevel(ChannelVM channel) => _s.StreamLevels.GetValueOrDefault(channel.Id, 100);

    public void SetStreamLevel(ChannelVM channel, double percent)
    {
        percent = Math.Clamp(Math.Round(percent), 0, 100);
        if (percent >= 100) _s.StreamLevels.Remove(channel.Id);
        else _s.StreamLevels[channel.Id] = percent;
        channel.SetStreamSilently(percent, channel.StreamMuted);
        ApplyChannel(channel);
        QueueSave();
    }

    public void SetStreamMuted(ChannelVM channel, bool muted)
    {
        if (muted) _s.StreamMuted.Add(channel.Id);
        else _s.StreamMuted.Remove(channel.Id);
        channel.SetStreamSilently(channel.StreamVolume, muted);
        ApplyChannel(channel);
        QueueSave();
    }

    /// <summary>What the channel sends to the stream: its stream fader, or nothing while it's muted on the stream side.</summary>
    double StreamOutput(ChannelVM channel) => channel.StreamMuted ? 0 : StreamLevel(channel);

    HashSet<string> ListeningDeviceIds()
    {
        var ids = _outputs.Select(OutputDeviceFor).OfType<string>().ToHashSet();
        if (DefaultOutputId != null) ids.Add(DefaultOutputId);
        return ids;
    }

    /// <summary>Starts, stops or repoints the mix to match the settings. Called after every poll, so devices coming and going are handled.</summary>
    void UpdateStream()
    {
        var problem = _s.StreamMixEnabled
            ? StreamMath.DeviceProblem(_s.StreamDeviceId, RenderDevices.Select(d => d.Id), ListeningDeviceIds())
            : "";
        if (problem != null)
        {
            StopStream();
            SetStreamStatus(problem);
            return;
        }

        if (_stream is { IsAlive: false })
        {
            var failure = _stream.Failure;
            StopStream();
            _streamRetryAt = DateTime.UtcNow.AddSeconds(5);
            SetStreamStatus(failure ?? "The stream mix stopped.");
        }
        if (_stream == null)
        {
            if (DateTime.UtcNow < _streamRetryAt) return;
            var deviceId = _s.StreamDeviceId!;
            _stream = new StreamMix(() => new DeviceOutput(deviceId));
            foreach (var session in _sessions) ApplyVolume(session);
            _streamPids = null;
            _stream.Start();
        }

        _engine.HoldOwnSessionsAtFull(RenderDevices);
        var pids = _sessions.Where(s => !s.IsSystem && !StreamMath.IsBroadcastApp(s.Key)).Select(s => s.Pid).ToHashSet();
        if (_streamPids == null || !pids.SetEquals(_streamPids))
        {
            _streamPids = pids;
            _stream.SetSources(StreamMath.CaptureTargets(pids, StreamMix.ReadParents()));
        }
        SetStreamStatus($"Sending to {StreamDeviceName}.");
    }

    void StopStream()
    {
        if (_stream == null) return;
        _stream.Dispose();
        _stream = null;
        _streamPids = null;
        // Back to plain personal levels, so a channel at zero isn't left at the stream mix's floor.
        foreach (var session in _sessions) ApplyVolume(session);
    }

    void SetStreamStatus(string status)
    {
        if (StreamStatus == status) return;
        StreamStatus = status;
        StreamChanged?.Invoke();
    }

    /// <summary>Remembers where a new app belongs. Apps the user placed are never looked at, and the answer isn't saved, so it stays a suggestion.</summary>
    internal void SortApp(string key, string? path)
    {
        if (!_s.AutoSortApps || key.StartsWith('#') || _s.Assignments.ContainsKey(key) || _sorted.ContainsKey(key)) return;
        if (AppSorter.Suggest(key, path) is { } channelId) _sorted[key] = channelId;
    }

    /// <summary>
    /// Whether to send an app that's using a microphone to the Mic channel's device, by clearing any input device
    /// Windows has pinned for it. Once per run of each app, so a choice made afterwards isn't fought over.
    /// </summary>
    internal bool ShouldFollowMic(uint pid) => _s.AutoSortApps && pid != 0 && _micFollowed.Add(pid);

    void ReapplyUnplacedApps()
    {
        foreach (var session in _sessions.Where(s => !_s.Assignments.ContainsKey(s.Key)))
        {
            ApplyVolume(session);
            Route(session, force: true);
        }
        UpdateApps();
        UpdateDefaultHint();
    }

    public void ResetAssignments()
    {
        AppRouting.ClearAll();
        _s.Assignments.Clear();
        _routed.Clear();
        foreach (var session in _sessions)
        {
            ApplyVolume(session);
            Route(session, force: true);
        }
        UpdateApps();
        QueueSave();
    }

    void UpdateDefaultHint() =>
        Master.EmptyHint = (_s.AutoSortApps
            ? $"New apps sort themselves. Anything else opens in {DefaultChannel.Title}."
            : $"New apps and games open in {DefaultChannel.Title}.") + "\nDrag an app to another channel to move it.";

    public ChannelVM? ChannelOfApp(string key) => ChannelFor(key);

    public string? SavedChannelDevice(ChannelVM channel) => SettingsFor(channel).DeviceId;
    public string? SavedChannelDeviceName(ChannelVM channel) => SettingsFor(channel).DeviceName;

    public void SetChannelDevice(ChannelVM channel, DeviceInfo? device)
    {
        var cs = SettingsFor(channel);
        cs.DeviceId = device?.Id;
        cs.DeviceName = device?.Name;
        foreach (var session in _sessions.Where(s => ChannelFor(s.Key) == channel)) Route(session, force: true);
        UpdateDeviceRows();
        QueueSave();
    }

    public void SetDefaultOutput(DeviceInfo device)
    {
        _engine.SetDefault(device.Id);
        _s.DefaultOutputId = device.Id;
        DefaultOutputId = device.Id;
        UpdateDeviceRows();
        QueueSave();
    }

    public void SetDefaultInput(DeviceInfo device)
    {
        _engine.SetDefault(device.Id);
        _s.DefaultInputId = device.Id;
        DefaultInputId = device.Id;
        UpdateDeviceRows();
        QueueSave();
    }

    public void AdjustDeviceVolume(ChannelVM channel, int deltaPercent)
    {
        var id = EffectiveDevice(channel);
        var current = _engine.GetEndpointVolume(id);
        if (current == null) return;
        _engine.SetEndpointVolume(id, current.Value + deltaPercent / 100f);
        UpdateDeviceRows();
    }

    public void Nudge(ChannelVM channel, double delta) => channel.Volume += delta;

    public void ToggleMicMute() => Mic.Muted = !Mic.Muted;

    public void SetCap(ChannelVM channel, double percent)
    {
        channel.Cap = percent;
        if (channel.Cap >= 100) _s.Caps.Remove(channel.Id);
        else _s.Caps[channel.Id] = channel.Cap;
        QueueSave();
    }

    public double AppVolume(string key) => _s.AppVolumes.TryGetValue(key, out var v) ? v : 100;

    public void SetAppVolume(string key, double percent, bool refreshChips = true)
    {
        percent = Math.Clamp(Math.Round(percent), 0, 100);
        if (percent >= 100) _s.AppVolumes.Remove(key);
        else _s.AppVolumes[key] = percent;
        foreach (var session in _sessions.Where(s => s.Key == key)) ApplyVolume(session);
        if (refreshChips) UpdateApps();
        QueueSave();
    }

    public void SetHidden(string key, bool hidden)
    {
        _s.HiddenApps.Remove(key);
        if (hidden) _s.HiddenApps.Add(key);
        UpdateApps();
        QueueSave();
    }

    /// <summary>Friendly name for a hidden app key, when it is running.</summary>
    public string NameOfApp(string key) =>
        _sessions.Concat(_micSessions).FirstOrDefault(s => s.Key == key)?.Name ?? key;

    public void RefreshDuck()
    {
        _duckTimer.IsEnabled = _s.DuckEnabled;
        if (!_s.DuckEnabled && _duck != 1)
        {
            _duck = 1;
            foreach (var channel in _outputs) ApplyChannel(channel);
        }
        else
        {
            foreach (var channel in _outputs) ApplyChannel(channel);
        }
    }

    // ---- Custom channels ----

    /// <summary>Why a channel name cannot be used, or null when it is fine.</summary>
    public string? ChannelNameProblem(string name, ChannelVM? renaming = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give the channel a name.";
        bool taken = Channels.Any(c => c != renaming && c.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return taken ? $"You already have a channel called {name}." : null;
    }

    public ChannelVM AddChannel(string name)
    {
        name = name.Trim();
        var used = _s.CustomChannels.Select(c => c.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = CustomColors.FirstOrDefault(c => !used.Contains(c)) ?? CustomColors[_s.CustomChannels.Count % CustomColors.Length];
        var custom = new CustomChannel { Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Name = name, Color = color };
        _s.CustomChannels.Add(custom);

        var channel = new ChannelVM(custom.Id, name, CustomGlyph, color, ChannelKind.Output, name);
        // Starts at the default channel's level, so dragging an app in cannot make it jump to full volume.
        double level = DefaultChannel.Volume;
        SettingsFor(channel).Volume = level;
        channel.SetSilently(level, false);
        channel.VolumeChanged = OnVolumeChanged;
        channel.MutedChanged = OnMutedChanged;
        WireStream(channel);
        _applied[channel] = level;

        _byId[channel.Id] = channel;
        // New channels open at the far right, next to Mic.
        _outputs.Add(channel);
        _cappable.Add(channel);
        Channels.Insert(Channels.IndexOf(Mic), channel);
        ScrollChannels.Add(channel);

        SaveChannelOrder();
        return channel;
    }

    /// <summary>The default layout, rearranged by the saved order. Anything the order doesn't list goes at the end.</summary>
    List<ChannelVM> InSavedOrder(List<ChannelVM> channels)
    {
        var ordered = _s.ChannelOrder.Distinct()
            .Select(id => channels.FirstOrDefault(c => c.Id == id))
            .OfType<ChannelVM>()
            .ToList();
        ordered.AddRange(channels.Except(ordered));
        return ordered;
    }

    /// <summary>Moves a channel between Master and Mic. Only its place changes, never any audio.</summary>
    public void MoveChannel(ChannelVM channel, int index)
    {
        int from = ScrollChannels.IndexOf(channel);
        if (from < 0) return;
        index = Math.Clamp(index, 0, ScrollChannels.Count - 1);
        if (index == from) return;

        _outputs.RemoveAt(from);
        _outputs.Insert(index, channel);
        _cappable.RemoveAt(from + 1);
        _cappable.Insert(index + 1, channel);
        Channels.Move(from + 1, index + 1);
        ScrollChannels.Move(from, index);
    }

    /// <summary>Saves a channel's color and icon. Values matching its defaults aren't stored, so it keeps following them.</summary>
    public void SaveLook(ChannelVM channel, System.Windows.Media.Color color, string glyph)
    {
        var look = new ChannelLook
        {
            Color = color == channel.DefaultColor ? null : ChannelVM.ToHex(color),
            Glyph = glyph == channel.DefaultGlyph ? null : glyph,
        };
        if (look.Color == null && look.Glyph == null) _s.Looks.Remove(channel.Id);
        else _s.Looks[channel.Id] = look;
        channel.SetLook(look.Color, look.Glyph);
        SaveNow();
        ChannelsChanged?.Invoke();
    }

    public void SaveChannelOrder()
    {
        _s.ChannelOrder = _outputs.Select(c => c.Id).ToList();
        SaveNow();
        ChannelsChanged?.Invoke();
    }

    public void RenameChannel(ChannelVM channel, string name)
    {
        var custom = _s.CustomChannels.FirstOrDefault(c => c.Id == channel.Id);
        if (custom == null) return;
        name = name.Trim();
        custom.Name = name;
        channel.Rename(name);
        UpdateDefaultHint();
        SaveNow();
        ChannelsChanged?.Invoke();
    }

    /// <summary>Removes a custom channel. Its apps go back to the default channel.</summary>
    public void DeleteChannel(ChannelVM channel)
    {
        if (!channel.IsCustom) return;
        if (_s.DefaultChannel == channel.Id) SetDefaultChannel(Game);
        UnassignChannel(channel);

        _s.CustomChannels.RemoveAll(c => c.Id == channel.Id);
        _s.Channels.Remove(channel.Id);
        _s.Caps.Remove(channel.Id);
        _s.Looks.Remove(channel.Id);
        _s.StreamLevels.Remove(channel.Id);
        _s.StreamMuted.Remove(channel.Id);
        _s.DuckChannels.Remove(channel.Id);
        _byId.Remove(channel.Id);
        _applied.Remove(channel);
        _outputs.Remove(channel);
        _cappable.Remove(channel);
        Channels.Remove(channel);
        ScrollChannels.Remove(channel);

        UpdateApps();
        SaveChannelOrder();
    }

    // ---- Profiles ----

    public void SaveProfile(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var profile = new MixProfile
        {
            Name = name,
            Master = Master.Volume,
            Balance = _balance,
            Volumes = _outputs.ToDictionary(c => c.Id, c => c.Volume),
            Mutes = CappableChannels.ToDictionary(c => c.Id, c => c.Muted),
        };
        _s.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _s.Profiles.Add(profile);
        SaveNow();
        ProfilesChanged?.Invoke();
    }

    public void ApplyProfile(MixProfile profile)
    {
        // Volumes go through the faders, so caps and fade-in apply to profiles too.
        Master.Volume = profile.Master;
        foreach (var channel in _outputs)
            if (profile.Volumes.TryGetValue(channel.Id, out var volume)) channel.Volume = volume;
        foreach (var channel in CappableChannels)
            if (profile.Mutes.TryGetValue(channel.Id, out var muted)) channel.Muted = muted;
        Balance = profile.Balance;
    }

    public void DeleteProfile(MixProfile profile)
    {
        _s.Profiles.Remove(profile);
        SaveNow();
        ProfilesChanged?.Invoke();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        SettingsStore.Save(_s);
    }

    public void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---- Polling ----

    void Poll()
    {
        _lastPoll = DateTime.UtcNow;
        _engine.ClearDirty();

        var render = _engine.GetDevices(EDataFlow.Render);
        var capture = _engine.GetDevices(EDataFlow.Capture);
        var signature = string.Join("|", render.Select(d => d.Id)) + "#" + string.Join("|", capture.Select(d => d.Id));
        if (signature != _deviceSignature)
        {
            _deviceSignature = signature;
            RenderDevices = render;
            CaptureDevices = capture;
            _engine.ResetDeviceCaches();
            _routed.Clear();
            _graceUntil = DateTime.UtcNow + DeviceGrace;
            _micRestorePending = true;
        }
        bool grace = DateTime.UtcNow < _graceUntil;

        DefaultOutputId = SyncDefault(EDataFlow.Render, render, _s.DefaultOutputId, grace, id => _s.DefaultOutputId = id);
        DefaultInputId = SyncDefault(EDataFlow.Capture, capture, _s.DefaultInputId, grace, id => _s.DefaultInputId = id);
        RestoreMicLevel();

        var sessions = _engine.GetSessions(render, includeSystemSounds: true);
        var seen = new HashSet<string>();
        foreach (var session in sessions)
        {
            seen.Add(session.InstanceId);
            if (_knownSessions.Add(session.InstanceId))
            {
                SortApp(session.Key, session.Path);
                ApplyVolume(session);
            }
            Route(session, force: false);
        }
        _knownSessions.IntersectWith(seen);

        var mics = _engine.GetSessions(capture, includeSystemSounds: false);
        foreach (var mic in mics)
            if (ShouldFollowMic(mic.Pid) && AppRouting.GetAppDevice(mic.Pid, EDataFlow.Capture) != null)
                AppRouting.SetAppDevice(mic.Pid, null, EDataFlow.Capture);
        var livePids = sessions.Select(s => s.Pid).Concat(mics.Select(s => s.Pid)).ToHashSet();
        _micFollowed.IntersectWith(livePids);
        foreach (var pid in _routed.Keys.Where(p => !livePids.Contains(p)).ToList()) _routed.Remove(pid);
        _engine.PruneProcessCache(livePids);

        var oldSessions = _sessions;
        var oldMics = _micSessions;
        _sessions = sessions;
        _micSessions = mics;
        AudioEngine.Release(oldSessions);
        AudioEngine.Release(oldMics);

        UpdateApps();
        UpdateDeviceRows();
        if (_s.StreamMixEnabled || _stream != null) UpdateStream();
        if (_s.StreamMixEnabled) UpdateStreamApp();
    }

    string? SyncDefault(EDataFlow flow, List<DeviceInfo> devices, string? saved, bool grace, Action<string> save)
    {
        var current = _engine.GetDefaultId(flow);
        if (saved == null)
        {
            // First run: remember whatever Windows is using now.
            if (current != null)
            {
                save(current);
                QueueSave();
            }
            return current;
        }

        if (saved != current && devices.Any(d => d.Id == saved))
        {
            if (grace)
            {
                _engine.SetDefault(saved);
                return saved;
            }
            // Changed in Windows on purpose (no device just arrived), so adopt it as the new saved choice.
            if (current != null)
            {
                save(current);
                QueueSave();
            }
        }
        return current;
    }

    void RestoreMicLevel()
    {
        if (!_micRestorePending || DefaultInputId == null) return;
        if (_s.DefaultInputId != null && _s.DefaultInputId != DefaultInputId) return;
        _micRestorePending = false;
        if (_s.MicVolume >= 0) _engine.SetEndpointVolume(DefaultInputId, (float)(_s.MicVolume / 100));
        if (_s.MicMuted is { } muted) _engine.SetEndpointMute(DefaultInputId, muted);
    }

    // ---- Volume and routing ----

    void OnVolumeChanged(ChannelVM channel)
    {
        switch (channel.Kind)
        {
            case ChannelKind.Master:
                _s.MasterVolume = channel.Volume;
                StartLevel(channel);
                break;
            case ChannelKind.Output:
                SettingsFor(channel).Volume = channel.Volume;
                StartLevel(channel);
                break;
            case ChannelKind.Mic:
                _s.MicVolume = channel.Volume;
                _engine.SetEndpointVolume(DefaultInputId, (float)(channel.Volume / 100));
                break;
        }
        QueueSave();
    }

    void OnMutedChanged(ChannelVM channel)
    {
        switch (channel.Kind)
        {
            case ChannelKind.Master:
                _s.MasterMuted = channel.Muted;
                foreach (var session in _sessions) ApplyVolume(session);
                break;
            case ChannelKind.Output:
                SettingsFor(channel).Muted = channel.Muted;
                ApplyChannel(channel);
                break;
            case ChannelKind.Mic:
                _s.MicMuted = channel.Muted;
                _engine.SetEndpointMute(DefaultInputId, channel.Muted);
                _lastMicMuted = channel.Muted;
                MicMuteChanged?.Invoke(channel.Muted, true);
                break;
        }
        QueueSave();
    }

    /// <summary>Applies a fader move: decreases and small steps land at once, big increases ramp up.</summary>
    void StartLevel(ChannelVM channel)
    {
        var applied = _applied.GetValueOrDefault(channel, channel.Volume);
        if (_s.FadeIn && channel.Volume > applied + FadeThreshold)
        {
            _rampTimer.Start();
            return;
        }
        _applied[channel] = channel.Volume;
        ApplyLevel(channel);
    }

    void RampTick()
    {
        bool pending = false;
        foreach (var channel in CappableChannels)
        {
            var applied = _applied[channel];
            var target = channel.Volume;
            if (applied >= target) continue;
            // Roughly half a second from silence to full, easing in toward the target.
            applied = Math.Min(target, applied + Math.Max(1.5, (target - applied) * 0.15));
            _applied[channel] = applied;
            ApplyLevel(channel);
            if (applied < target) pending = true;
        }
        if (!pending) _rampTimer.Stop();
    }

    void ApplyLevel(ChannelVM channel)
    {
        if (channel.Kind == ChannelKind.Master)
            foreach (var session in _sessions) ApplyVolume(session);
        else
            ApplyChannel(channel);
    }

    void DuckTick()
    {
        double peak = 0;
        if (!Chat.Muted)
            foreach (var session in _sessions)
                if (ChannelFor(session.Key) == Chat) peak = Math.Max(peak, AudioEngine.GetSessionPeak(session));

        var now = DateTime.UtcNow;
        if (peak > TalkThreshold) _lastTalk = now;
        double target = now - _lastTalk < DuckHold ? Math.Clamp(_s.DuckLevel, 0, 100) / 100 : 1;
        // Fast attack so the first word is clear, slow release so it does not pump.
        double next = _duck + (target - _duck) * (target < _duck ? 0.45 : 0.08);
        if (Math.Abs(next - target) < 0.005) next = target;
        if (next == _duck) return;
        _duck = next;
        foreach (var channel in _outputs)
            if (_s.DuckChannels.Contains(channel.Id)) ApplyChannel(channel);
    }

    void ApplyChannel(ChannelVM channel)
    {
        foreach (var session in _sessions)
            if (ChannelFor(session.Key) == channel) ApplyVolume(session);
    }

    void ApplyVolume(SessionInfo session)
    {
        var channel = ChannelFor(session.Key);
        double level = _applied[Master] / 100.0 * _applied[channel] / 100.0 * BalanceFactor(channel) * DuckFactor(channel);
        bool mute = Master.Muted || channel.Muted;
        level *= AppVolume(session.Key) / 100.0;
        if (_stream != null)
        {
            (level, mute) = StreamMath.PersonalLevel(level, mute);
            _stream.SetGain(session.Pid, StreamMath.Gain(StreamOutput(channel) / 100.0 * AppVolume(session.Key) / 100.0, level));
        }
        AudioEngine.SetSessionVolume(session, (float)level, mute);
    }

    double BalanceFactor(ChannelVM channel)
    {
        if (channel == Game && _balance > 0) return 1 - _balance / 100.0;
        if (channel == Chat && _balance < 0) return 1 + _balance / 100.0;
        return 1;
    }

    double DuckFactor(ChannelVM channel) => _s.DuckChannels.Contains(channel.Id) ? _duck : 1;

    void Route(SessionInfo session, bool force)
    {
        if (session.IsSystem || session.Pid == 0) return;
        var channel = ChannelFor(session.Key);
        var want = OutputDeviceFor(channel) ?? "";
        if (!force && _routed.TryGetValue(session.Pid, out var current) && current == want) return;
        _routed[session.Pid] = want;
        AppRouting.SetAppDevice(session.Pid, want.Length == 0 ? null : want, EDataFlow.Render);
    }

    /// <summary>Where an app plays: the user's choice, then sorting, then the default. Always an output channel, so never Master or Mic.</summary>
    ChannelVM ChannelFor(string key)
    {
        if (_s.Assignments.TryGetValue(key, out var chosen) && IsOutput(chosen, out var channel)) return channel;
        if (_s.AutoSortApps && _sorted.TryGetValue(key, out var sorted) && IsOutput(sorted, out channel)) return channel;
        return DefaultChannel;
    }

    bool IsOutput(string id, out ChannelVM channel)
    {
        if (_byId.TryGetValue(id, out var found) && found.Kind == ChannelKind.Output)
        {
            channel = found;
            return true;
        }
        channel = DefaultChannel;
        return false;
    }

    ChannelSettings SettingsFor(ChannelVM channel)
    {
        if (!_s.Channels.TryGetValue(channel.Id, out var cs)) _s.Channels[channel.Id] = cs = new ChannelSettings();
        return cs;
    }

    /// <summary>The channel's pinned device when it is connected, otherwise null (follow Windows default).</summary>
    string? OutputDeviceFor(ChannelVM channel)
    {
        var id = SettingsFor(channel).DeviceId;
        return id != null && RenderDevices.Any(d => d.Id == id) ? id : null;
    }

    string? EffectiveDevice(ChannelVM channel) => channel.Kind switch
    {
        ChannelKind.Master => DefaultOutputId,
        ChannelKind.Mic => DefaultInputId,
        _ => OutputDeviceFor(channel) ?? DefaultOutputId,
    };

    // ---- UI sync ----

    /// <summary>Holds the chip lists still while an app's menu is open, so its chip is not rebuilt underneath it.</summary>
    public bool FreezeApps { get; set; }

    public void RefreshApps() => UpdateApps();

    void UpdateApps()
    {
        if (FreezeApps) return;
        var hidden = new HashSet<string>(_s.HiddenApps);
        var buckets = Channels.ToDictionary(c => c, _ => new List<AppChip>());
        var seen = new HashSet<string>();
        foreach (var session in _sessions)
            if (!hidden.Contains(session.Key) && seen.Add(session.Key))
                buckets[ChannelFor(session.Key)].Add(
                    new AppChip(session.Key, session.Name, true, AppVolume(session.Key), Icons.ForExe(session.Path)));

        seen.Clear();
        foreach (var session in _micSessions)
            if (!hidden.Contains(session.Key) && seen.Add(session.Key))
                buckets[Mic].Add(new AppChip(session.Key, session.Name, false, 100, Icons.ForExe(session.Path)));

        foreach (var (channel, apps) in buckets)
        {
            apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            channel.SyncApps(apps);
        }
    }

    void UpdateDeviceRows()
    {
        foreach (var channel in Channels)
        {
            var id = EffectiveDevice(channel);
            var devices = channel.Kind == ChannelKind.Mic ? CaptureDevices : RenderDevices;
            var name = devices.FirstOrDefault(d => d.Id == id)?.Name ?? "No device";
            var volume = _engine.GetEndpointVolume(id);

            // The device's own volume is separate from the channel fader, so it stays available on hover.
            var deviceLevel = volume is { } v ? $"\nDevice volume {Math.Round(v * 100):0}% (scroll to change)" : "";
            channel.DeviceName = name;

            switch (channel.Kind)
            {
                case ChannelKind.Master:
                    channel.DeviceTooltip = $"Windows default output: {name}{deviceLevel}";
                    break;
                case ChannelKind.Mic:
                    channel.DeviceTooltip = $"Windows default input: {name}{deviceLevel}";
                    if (volume is { } mic) channel.SetSilently(mic * 100, _engine.GetEndpointMute(id) ?? channel.Muted);
                    if (_lastMicMuted != channel.Muted)
                    {
                        // Changed outside this app (Windows, the headset) or first read: update the tray, no beep.
                        _lastMicMuted = channel.Muted;
                        MicMuteChanged?.Invoke(channel.Muted, false);
                    }
                    break;
                default:
                    bool follows = OutputDeviceFor(channel) == null;
                    channel.DeviceGlyph = follows ? "" : "";
                    channel.DeviceTooltip = follows
                        ? $"Follows Windows default: {name}{deviceLevel}"
                        : $"Routed to: {name}{deviceLevel}";
                    break;
            }
        }
    }

    void UpdateMeters()
    {
        if (_peaks.Length != Channels.Count) _peaks = new double[Channels.Count];
        var peaks = _peaks;
        Array.Clear(peaks);
        if (_streamPeaks.Length != Channels.Count) _streamPeaks = new double[Channels.Count];
        var streamPeaks = _streamPeaks;
        Array.Clear(streamPeaks);
        foreach (var session in _sessions)
        {
            if (_stream != null)
            {
                int target = IndexOf(ChannelFor(session.Key));
                streamPeaks[target] = Math.Max(streamPeaks[target], _stream.TakePeak(session.Pid));
            }
            double peak = AudioEngine.GetSessionPeak(session);
            if (peak <= 0) continue;
            int index = IndexOf(ChannelFor(session.Key));
            peaks[index] = Math.Max(peaks[index], peak);
            peaks[0] = Math.Max(peaks[0], peak);
        }
        peaks[Channels.Count - 1] = _engine.GetEndpointPeak(DefaultInputId);

        for (int i = 0; i < Channels.Count; i++)
        {
            var channel = Channels[i];
            // Post-fader: the level never rises past the fader, and a muted channel shows nothing.
            double level = channel.Muted ? 0 : ToMeterScale(peaks[i]) * channel.Volume / 100.0;
            channel.Peak = Math.Max(level, channel.Peak * 0.82);
            // Read from the mix itself, so it's already at the stream level, and muting for yourself doesn't touch it.
            channel.StreamPeak = Math.Max(ToMeterScale(streamPeaks[i]), channel.StreamPeak * 0.82);
        }
    }

    /// <summary>Maps a linear peak to a -60 dB..0 dB meter, so normal speech and quiet audio are visible.</summary>
    static double ToMeterScale(double peak)
    {
        if (peak <= 0.001) return 0;
        return Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
    }

    int IndexOf(ChannelVM channel)
    {
        for (int i = 0; i < Channels.Count; i++)
            if (Channels[i] == channel) return i;
        return 0;
    }

    public string Diagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Default output: {DefaultOutputId}");
        sb.AppendLine($"Default input: {DefaultInputId}");
        sb.AppendLine("Render devices:");
        foreach (var d in RenderDevices) sb.AppendLine($"  {d.Name} | {d.Id} | vol {_engine.GetEndpointVolume(d.Id)}");
        sb.AppendLine("Capture devices:");
        foreach (var d in CaptureDevices) sb.AppendLine($"  {d.Name} | {d.Id}");
        sb.AppendLine("Render sessions:");
        foreach (var s in _sessions) sb.AppendLine($"  pid {s.Pid} | {s.Key} | {s.Name} | dev {s.DeviceId} | vol {s.Volume?.GetMasterVolume()} | pinned {AppRouting.GetAppDevice(s.Pid, EDataFlow.Render)}");
        sb.AppendLine("Capture sessions:");
        foreach (var s in _micSessions) sb.AppendLine($"  pid {s.Pid} | {s.Key} | {s.Name}");
        foreach (var c in Channels) sb.AppendLine($"Channel {c.Id}: {c.Volume} muted={c.Muted} device='{c.DeviceName}' apps=[{string.Join(", ", c.Apps.Select(a => a.Name))}]");
        return sb.ToString();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _tick.Stop();
        _meterTimer.Stop();
        _rampTimer.Stop();
        _duckTimer.Stop();
        SaveNow();
        AudioEngine.Release(_sessions);
        AudioEngine.Release(_micSessions);
        _sessions.Clear();
        _micSessions.Clear();
        _engine.Dispose();
    }
}
