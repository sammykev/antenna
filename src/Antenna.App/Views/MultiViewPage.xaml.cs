using System.Globalization;
using Antenna.App.Services;
using Antenna.Core;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Up to four streams playing at once, like UHF's multi-view. The selected tile is the main
/// screen: a little larger and the only one with sound. Every tile keeps playing.
/// Each tile has its own LibVLC instance (one per video surface), created off the UI thread.
/// </summary>
public sealed partial class MultiViewPage : Page
{
    private const int SlotCount = 4;

    /// <summary>How much larger the main screen's row and column are than the others.</summary>
    private const double MainWeight = 1.45;

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>What's in each tile; survives leaving and returning to the page.</summary>
    private static readonly PlayRequest?[] Chosen = new PlayRequest?[SlotCount];
    private static int _mainSlot;
    private static string _layout = "spot";

    private static readonly SolidColorBrush Selected = new(Colors.DodgerBlue);
    private static readonly SolidColorBrush Unselected = new(Colors.Transparent);

    private readonly Slot[] _slots = new Slot[SlotCount];
    private readonly DispatcherQueueTimer _audioGuard;
    private bool _leaving;

    private sealed class Slot
    {
        public required int Index;
        public required Grid Root;
        public required TextBlock Title;
        public required Border Frame;
        public required Button Pick;
        public required Button Change;
        public required Button Close;
        public required VideoView Video;
        public required ProgressRing Busy;
        public required TextBlock Status;
        public required Border SpeakerBadge;
        public LibVLC? Vlc;
        public MediaPlayer? Player;
        public int Retry;
        public int PlayVersion;

        /// <summary>Last mute state sent to libvlc, so routine updates only talk to it when something changed.</summary>
        public bool? SentMute;
    }

    public MultiViewPage()
    {
        InitializeComponent();
        for (var i = 0; i < SlotCount; i++)
            _slots[i] = CreateSlot(i);
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;
        ApplyLayout();

        // libvlc forgets a mute set before its audio output opens and can unmute on track changes,
        // so the "only the main screen has sound" rule is re-checked every second (off the UI thread).
        _audioGuard = DispatcherQueue.CreateTimer();
        _audioGuard.Interval = TimeSpan.FromSeconds(1);
        _audioGuard.IsRepeating = true;
        _audioGuard.Tick += (_, _) => ApplyAudio(force: true);
        _audioGuard.Start();

        // 1–4 choose the main screen.
        for (var i = 0; i < SlotCount; i++)
        {
            var index = i;
            foreach (var key in new[] { Windows.System.VirtualKey.Number1 + i, Windows.System.VirtualKey.NumberPad1 + i })
            {
                var accelerator = new KeyboardAccelerator { Key = key };
                accelerator.Invoked += (_, a) =>
                {
                    SelectMain(index);
                    a.Handled = true;
                };
                KeyboardAccelerators.Add(accelerator);
            }
        }
    }

    /// <summary>Puts a channel in the first free tile (or replaces the main one), makes it the main screen, and opens multi-view.</summary>
    public static void Add(PlayRequest request)
    {
        var free = Array.FindIndex(Chosen, c => c is null);
        var target = free >= 0 ? free : _mainSlot;
        Chosen[target] = ForTile(request);
        _mainSlot = target;
        App.Window.Frame.Navigate(typeof(MultiViewPage));
    }

    /// <summary>Replaces all tiles with these streams (up to four) and opens multi-view.</summary>
    public static void Show(IReadOnlyList<PlayRequest> requests)
    {
        for (var i = 0; i < SlotCount; i++)
            Chosen[i] = i < requests.Count ? ForTile(requests[i]) : null;
        _mainSlot = 0;
        App.Window.Frame.Navigate(typeof(MultiViewPage));
    }

    private static PlayRequest ForTile(PlayRequest r) => r with { StartAtMs = 0 };

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _leaving = true;
        _audioGuard.Stop();
        if (App.Window.IsFullScreen)
            App.Window.SetFullScreen(false);
        foreach (var slot in _slots)
            Release(slot);
    }

    // ---- Tiles ----

    private Slot CreateSlot(int index)
    {
        var video = new VideoView();
        var title = new TextBlock { Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var number = new TextBlock
        {
            Text = (index + 1).ToString(CultureInfo.InvariantCulture),
            Foreground = new SolidColorBrush(Colors.White),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var speaker = new Border
        {
            Background = Selected,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
            Visibility = Visibility.Collapsed,
        };
        var header = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(10, 6, 88, 6),
            ColumnSpacing = 8,
            Background = new SolidColorBrush(ColorHelper.FromArgb(120, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(number);
        Grid.SetColumn(speaker, 1);
        header.Children.Add(speaker);
        Grid.SetColumn(title, 2);
        header.Children.Add(title);

        var frame = new Border { BorderThickness = new Thickness(3), BorderBrush = Unselected, IsHitTestVisible = false };
        var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var busy = new ProgressRing { Width = 40, Height = 40, IsActive = false, IsHitTestVisible = false };
        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        var pick = new Button { Content = "＋ Choose channel", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RequestedTheme = ElementTheme.Dark };
        var change = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 44, 4),
            RequestedTheme = ElementTheme.Dark,
        };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            RequestedTheme = ElementTheme.Dark,
        };
        ToolTipService.SetToolTip(change, "Change channel");
        ToolTipService.SetToolTip(close, "Remove");

        var root = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 16, 20)) };
        foreach (var child in new UIElement[] { video, hit, busy, status, header, pick, change, close, frame })
            root.Children.Add(child);
        Slots.Children.Add(root);

        var slot = new Slot
        {
            Index = index, Root = root, Title = title, Frame = frame, Pick = pick, Change = change, Close = close,
            Video = video, Busy = busy, Status = status, SpeakerBadge = speaker,
        };

        video.Initialized += async (_, e) =>
        {
            if (_leaving)
                return;
            // Creating a LibVLC instance loads its plugins and takes a moment: keep it off the UI thread.
            var options = e.SwapChainOptions;
            var (vlc, player) = await Task.Run(() =>
            {
                var v = new LibVLC(enableDebugLogs: false, options);
                return (v, new MediaPlayer(v) { Mute = true });
            });
            if (_leaving)
            {
                _ = Task.Run(() => { player.Dispose(); vlc.Dispose(); });
                return;
            }
            // Events arrive on libvlc threads: never call back into libvlc there, hop to the UI.
            player.Playing += (_, _) => OnUi(() => OnPlaying(slot));
            player.ESAdded += (_, _) => OnUi(() => ApplyAudio(force: true));
            player.Unmuted += (_, _) => OnUi(() => ApplyAudio(force: true));
            player.Buffering += (_, a) => OnUi(() => slot.Busy.IsActive = a.Cache < 100f && Chosen[slot.Index] is not null);
            player.EncounteredError += (_, _) => OnUi(() => OnFailed(slot));
            player.EndReached += (_, _) => OnUi(() => OnFailed(slot));
            slot.Vlc = vlc;
            slot.Player = player;
            video.MediaPlayer = player;
            Play(slot);
        };
        hit.Tapped += (_, _) => SelectMain(index);
        hit.DoubleTapped += (_, _) =>
        {
            SelectMain(index);
            if (_layout != "focus")
                SetLayout("focus");
        };
        pick.Click += (_, _) => ShowPicker(slot, pick);
        change.Click += (_, _) => ShowPicker(slot, change);
        close.Click += (_, _) =>
        {
            Chosen[index] = null;
            Play(slot);
            ApplyLayout();
        };
        return slot;
    }

    private void OnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_leaving)
            action();
    });

    private void UpdateChrome(Slot slot)
    {
        var request = Chosen[slot.Index];
        var main = IsMain(slot);
        Set(slot.Title, request?.Title ?? "");
        SetVisible(slot.Pick, request is null);
        SetVisible(slot.Close, request is not null);
        SetVisible(slot.Change, request is not null);
        SetVisible(slot.SpeakerBadge, main);
        var brush = main ? Selected : Unselected;
        if (!ReferenceEquals(slot.Frame.BorderBrush, brush))
            slot.Frame.BorderBrush = brush;
        if (request is null)
        {
            slot.Busy.IsActive = false;
            SetVisible(slot.Status, false);
        }
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
            block.Text = text;
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != v)
            element.Visibility = v;
    }

    private static bool IsMain(Slot slot) => slot.Index == _mainSlot && Chosen[slot.Index] is not null;

    // ---- Sound ----

    /// <summary>
    /// Exactly one tile, the main screen, has sound; the others keep playing silently.
    /// libvlc calls go to a worker thread so a busy stream can't stall the UI.
    /// </summary>
    private void ApplyAudio(bool force = false)
    {
        if (Chosen[_mainSlot] is null && _slots.FirstOrDefault(s => Chosen[s.Index] is not null) is { } fallback)
            _mainSlot = fallback.Index;

        foreach (var slot in _slots)
        {
            UpdateChrome(slot);
            var mute = !IsMain(slot);
            if (slot.Player is not { } player || (!force && slot.SentMute == mute))
                continue;
            slot.SentMute = mute;
            Worker.Run(() =>
            {
                if (player.Mute != mute)
                    player.Mute = mute;
            }, "multi-view mute");
        }
    }

    private void SelectMain(int index)
    {
        if (Chosen[index] is null || index == _mainSlot)
            return;
        _mainSlot = index;
        ApplyLayout();
    }

    // ---- Playback ----

    private void Play(Slot slot)
    {
        UpdateChrome(slot);
        var request = Chosen[slot.Index];
        var version = ++slot.PlayVersion;
        if (slot.Player is not { } player || slot.Vlc is not { } vlc)
            return;
        if (request is null)
        {
            slot.Busy.IsActive = false;
            Worker.Run(() => player.Stop(), "multi-view stop");
            return;
        }

        slot.Busy.IsActive = true;
        SetVisible(slot.Status, false);
        var mute = !IsMain(slot);
        slot.SentMute = mute;

        var options = new List<string>
        {
            $":network-caching={AppSettings.NetworkCachingMs}",
            ":http-reconnect",
            AppSettings.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none",
        };
        options.AddRange(request.Options);
        Worker.Run(() =>
        {
            if (version != slot.PlayVersion)
                return; // Superseded before it started (fast channel changes).
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Mute = mute;
            player.Play(media);
        }, "multi-view play");
    }

    private void OnPlaying(Slot slot)
    {
        slot.Retry = 0;
        slot.Busy.IsActive = false;
        SetVisible(slot.Status, false);
        ApplyAudio(force: true);
    }

    /// <summary>Live streams that drop or end are retried with backoff.</summary>
    private async void OnFailed(Slot slot)
    {
        if (Chosen[slot.Index] is null)
            return;
        slot.Busy.IsActive = false;
        if (slot.Retry >= RetryDelays.Length)
        {
            Set(slot.Status, "Stream unavailable");
            SetVisible(slot.Status, true);
            return;
        }

        var version = slot.PlayVersion;
        var delay = RetryDelays[slot.Retry++];
        Set(slot.Status, $"Reconnecting ({slot.Retry}/{RetryDelays.Length})…");
        SetVisible(slot.Status, true);
        await Task.Delay(delay);
        if (!_leaving && version == slot.PlayVersion)
            Play(slot);
    }

    /// <summary>Ch+/Ch− on the main screen, through the list it was picked from.</summary>
    private void Zap(int delta)
    {
        var slot = _slots[_mainSlot];
        if (Chosen[_mainSlot] is not { CanZap: true } request)
            return;
        Chosen[_mainSlot] = ForTile(request.WithChannel(request.ChannelIndex + delta));
        slot.Retry = 0;
        Play(slot);
    }

    // ---- Layout ----

    /// <summary>
    /// Places the tiles. Tiles keep their positions; choosing a main screen only resizes them, so
    /// nothing jumps around. Empty tiles take no space unless there's room for an "add" tile.
    /// </summary>
    private void ApplyLayout()
    {
        Slots.RowDefinitions.Clear();
        Slots.ColumnDefinitions.Clear();
        GridLength Star(double n = 1) => new(n, GridUnitType.Star);

        var filled = _slots.Where(s => Chosen[s.Index] is not null).ToList();
        var empty = _slots.Where(s => Chosen[s.Index] is null).ToList();
        if (Chosen[_mainSlot] is null && filled.Count > 0)
            _mainSlot = filled[0].Index;

        // Which tiles are on screen, in reading order: 3 streams get a 2×2 with one "add" tile.
        var shown = filled.Count switch
        {
            0 => _slots.ToList(),
            1 or 2 => filled,
            3 => filled.Concat(empty.Take(1)).ToList(),
            _ => filled,
        };
        var mainPos = Math.Max(0, shown.FindIndex(s => s.Index == _mainSlot));

        if (_layout == "focus" && filled.Count > 1)
        {
            // Main screen large on the left, the other streams stacked on the right.
            shown = filled;
            Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(3) });
            Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(1) });
            var others = filled.Where(s => s.Index != _mainSlot).ToList();
            for (var r = 0; r < others.Count; r++)
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
            Place(_slots[_mainSlot], 0, 0, rowSpan: others.Count);
            for (var r = 0; r < others.Count; r++)
                Place(others[r], r, 1);
        }
        else if (shown.Count <= 2)
        {
            // One stream fills the screen; two sit side by side, the main one a little wider.
            Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
            for (var c = 0; c < shown.Count; c++)
            {
                var weight = _layout == "spot" && shown.Count == 2 && c == mainPos ? MainWeight : 1;
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(weight) });
                Place(shown[c], 0, c);
            }
        }
        else
        {
            // 2×2. In "Main + others" the main screen's row and column are slightly larger.
            var spot = _layout == "spot";
            for (var r = 0; r < 2; r++)
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star(spot && r == mainPos / 2 ? MainWeight : 1) });
            for (var c = 0; c < 2; c++)
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(spot && c == mainPos % 2 ? MainWeight : 1) });
            for (var i = 0; i < shown.Count; i++)
                Place(shown[i], i / 2, i % 2);
        }

        // Tiles not on screen are always empty ones: park them behind the first tile instead of
        // collapsing them (a zero-size video surface can crash).
        foreach (var slot in _slots)
        {
            var visible = shown.Contains(slot);
            slot.Root.Opacity = visible ? 1 : 0;
            slot.Root.IsHitTestVisible = visible;
            Canvas.SetZIndex(slot.Root, visible ? 0 : -1);
            if (!visible)
                Place(slot, 0, 0);
        }

        SetVisible(AddButton, filled.Count < SlotCount);
        ApplyAudio();
    }

    private static void Place(Slot slot, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(slot.Root, row);
        Grid.SetColumn(slot.Root, column);
        Grid.SetRowSpan(slot.Root, Math.Max(1, rowSpan));
    }

    private void SetLayout(string layout)
    {
        _layout = layout;
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == layout;
        ApplyLayout();
    }

    private void Layout_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Slots is null || _slots[SlotCount - 1] is null)
            return; // Fires during InitializeComponent.
        var layout = sender.SelectedItem?.Tag as string ?? "spot";
        if (layout == _layout && Slots.ColumnDefinitions.Count > 0)
            return;
        _layout = layout;
        ApplyLayout();
    }

    // ---- Picking channels ----

    /// <summary>A row in the channel picker; ToString is what the list shows.</summary>
    private sealed class PickRow(LibraryEntry entry, string? now)
    {
        public LibraryEntry Entry { get; } = entry;

        public override string ToString() =>
            now is null ? $"{Entry.Item.Title}  ·  {Entry.SourceName}" : $"{Entry.Item.Title}  ·  Now: {now}";
    }

    /// <summary>
    /// Full channel picker: every live channel when the box is empty, every match while typing
    /// (channels showing a matching event first). The list scrolls through all of them.
    /// </summary>
    private void ShowPicker(Slot slot, FrameworkElement anchor)
    {
        var box = new TextBox { PlaceholderText = "Search channels or what's on (e.g. NBA, man city)" };
        var count = new TextBlock { Opacity = 0.7, FontSize = 12 };
        var list = new ListView { Height = 440, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        var panel = new StackPanel { Spacing = 8, Width = 460 };
        panel.Children.Add(box);
        panel.Children.Add(count);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel };

        List<PickRow> rows = [];
        var version = 0;

        async Task LoadAsync()
        {
            var text = box.Text.Trim();
            var mine = ++version;
            count.Text = "Searching…";
            List<PickRow> found;
            try
            {
                found = await Task.Run(() =>
                {
                    var nowPlaying = AppServices.Library.GetNowPlaying(DateTimeOffset.Now);
                    string? NowOn(MediaItem item) =>
                        item.EpgChannelId is { } ch && nowPlaying.TryGetValue((item.SourceId, ch), out var p) ? p.Title : null;

                    if (text.Length < 2)
                    {
                        return AppServices.Library.GetItems(MediaKind.Live, limit: 20000)
                            .Select(e => new PickRow(e, NowOn(e.Item))).ToList();
                    }
                    var at = DateTimeOffset.Now;
                    var byEvent = AppServices.Library.SearchEvents(text, at, at.AddSeconds(1), 500).SelectMany(h => h.Channels);
                    var byName = AppServices.Library.Search(text, MediaKind.Live, 5000);
                    return byEvent.Concat(byName)
                        .DistinctBy(e => (e.Item.SourceId, e.Item.ExternalId))
                        .Select(e => new PickRow(e, NowOn(e.Item)))
                        .ToList();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "multi-view picker");
                found = [];
            }
            if (mine != version)
                return; // A newer search replaced this one.
            rows = found;
            list.ItemsSource = rows;
            count.Text = rows.Count == 0 ? "No matching channels" : $"{rows.Count:N0} channel{(rows.Count == 1 ? "" : "s")} · click one, or press Enter for the first";
        }

        // Wait for a pause in typing; drop results from older keystrokes.
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(250);
        debounce.IsRepeating = false;
        debounce.Tick += async (_, _) => await LoadAsync();
        box.TextChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };

        void Choose(PickRow row)
        {
            // The whole list becomes the tile's channel list, so Page Up/Down steps through it.
            var channels = rows.Select(r => r.Entry.Item).ToList();
            Chosen[slot.Index] = ForTile(PlayRequest.ForItem(row.Entry.Item, channels));
            _mainSlot = slot.Index;
            slot.Retry = 0;
            flyout.Hide();
            Play(slot);
            ApplyLayout();
        }

        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is PickRow row)
                Choose(row);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && rows.Count > 0)
            {
                Choose(rows[0]);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Down && rows.Count > 0)
            {
                list.SelectedIndex = 0;
                (list.ContainerFromIndex(0) as Control)?.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
        };
        flyout.Closed += (_, _) => debounce.Stop();

        flyout.ShowAt(anchor);
        box.Focus(FocusState.Programmatic);
        _ = LoadAsync();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var free = _slots.FirstOrDefault(s => Chosen[s.Index] is null);
        if (free is not null)
            ShowPicker(free, AddButton);
    }

    private static void Release(Slot slot)
    {
        var player = slot.Player;
        var vlc = slot.Vlc;
        slot.Video.MediaPlayer = null;
        slot.Player = null;
        slot.Vlc = null;
        _ = Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                vlc?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "multi-view release");
            }
        });
    }

    // ---- Window ----

    private void ToggleFullScreen()
    {
        var full = !App.Window.IsFullScreen;
        App.Window.SetFullScreen(full);
        Toolbar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        FullScreenIcon.Glyph = full ? "" : "";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void FullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullScreen();
        args.Handled = true;
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (App.Window.IsFullScreen)
            ToggleFullScreen();
        args.Handled = true;
    }

    private void ChannelUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Zap(+1);
        args.Handled = true;
    }

    private void ChannelDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Zap(-1);
        args.Handled = true;
    }
}
