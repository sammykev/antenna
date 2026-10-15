using Antenna.App.Services;
using Antenna.Core;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Up to four players at once, like UHF's multi-view. Each tile has its own LibVLC instance
/// (one per video surface). Tiles keep their channels while the app runs.
/// </summary>
public sealed partial class MultiViewPage : Page
{
    private const int SlotCount = 4;

    /// <summary>What's in each tile; survives leaving and returning to the page.</summary>
    private static readonly PlayRequest?[] Chosen = new PlayRequest?[SlotCount];
    private static int _audioSlot;
    private static int _bigSlot;
    private static string _layout = "grid";

    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly Slot[] _slots = new Slot[SlotCount];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _audioGuard;
    private bool _leaving;

    private sealed class Slot
    {
        public required int Index;
        public required Grid Root;
        public required TextBlock Title;
        public required Border Frame;
        public required Button Pick;
        public required Button Close;
        public required VideoView Video;
        public required ProgressRing Busy;
        public required TextBlock Status;
        public required Border SpeakerBadge;
        public LibVLC? Vlc;
        public MediaPlayer? Player;

        /// <summary>Off-screen in the side-by-side layout: paused, silent, not resized to zero.</summary>
        public bool Hidden;
        public int Retry;
        public int PlayVersion;
    }

    public MultiViewPage()
    {
        InitializeComponent();
        for (var i = 0; i < SlotCount; i++)
            _slots[i] = CreateSlot(i);
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;
        ApplyLayout();

        // LibVLC forgets a mute set before its audio output opens and can unmute on track changes,
        // so the "only the selected tile has sound" rule is re-applied on events and every second.
        _audioGuard = DispatcherQueue.CreateTimer();
        _audioGuard.Interval = TimeSpan.FromSeconds(1);
        _audioGuard.IsRepeating = true;
        _audioGuard.Tick += (_, _) => ApplyAudio();
        _audioGuard.Start();

        // 1–4 choose which tile you hear.
        for (var i = 0; i < SlotCount; i++)
        {
            var index = i;
            foreach (var key in new[] { Windows.System.VirtualKey.Number1 + i, Windows.System.VirtualKey.NumberPad1 + i })
            {
                var accelerator = new KeyboardAccelerator { Key = key };
                accelerator.Invoked += (_, a) =>
                {
                    SelectAudio(index);
                    a.Handled = true;
                };
                KeyboardAccelerators.Add(accelerator);
            }
        }
    }

    /// <summary>Puts a channel in the first free tile (or replaces the selected one) and opens multi-view.</summary>
    public static void Add(PlayRequest request)
    {
        var free = Array.FindIndex(Chosen, c => c is null);
        var target = free >= 0 ? free : _audioSlot;
        Chosen[target] = ForTile(request);
        _audioSlot = target; // You hear what you just added.
        App.Window.Frame.Navigate(typeof(MultiViewPage));
    }

    /// <summary>Replaces all tiles with these streams (up to four) and opens multi-view.</summary>
    public static void Show(IReadOnlyList<PlayRequest> requests)
    {
        for (var i = 0; i < SlotCount; i++)
            Chosen[i] = i < requests.Count ? ForTile(requests[i]) : null;
        _audioSlot = 0;
        _bigSlot = 0;
        _layout = requests.Count switch { 2 => "pair", _ => "grid" };
        App.Window.Frame.Navigate(typeof(MultiViewPage));
    }

    private static PlayRequest ForTile(PlayRequest r) => r with { Channels = [], ChannelIndex = -1, StartAtMs = 0 };

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

    private Slot CreateSlot(int index)
    {
        var video = new VideoView();
        var title = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var number = new TextBlock
        {
            Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = new SolidColorBrush(Colors.White),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var speaker = new Border
        {
            Background = new SolidColorBrush(Colors.DodgerBlue),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new FontIcon { Glyph = "\uE767", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
            Visibility = Visibility.Collapsed,
        };
        var header = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(10, 6, 48, 6),
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

        var frame = new Border { BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false };
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
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            RequestedTheme = ElementTheme.Dark,
        };
        ToolTipService.SetToolTip(close, "Remove");
        ToolTipService.SetToolTip(hit, "Click to hear this tile · double-click to make it big");

        var root = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 16, 20)) };
        root.Children.Add(video);
        root.Children.Add(hit);
        root.Children.Add(busy);
        root.Children.Add(status);
        root.Children.Add(header);
        root.Children.Add(pick);
        root.Children.Add(close);
        root.Children.Add(frame);
        Slots.Children.Add(root);

        var slot = new Slot
        {
            Index = index, Root = root, Title = title, Frame = frame, Pick = pick, Close = close, Video = video,
            Busy = busy, Status = status, SpeakerBadge = speaker,
        };
        video.Initialized += (_, e) =>
        {
            if (_leaving)
                return;
            slot.Vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
            var player = new MediaPlayer(slot.Vlc) { Mute = true };
            // Events arrive on libvlc threads: never call back into libvlc there, hop to the UI.
            player.Playing += (_, _) => OnUi(() => OnPlaying(slot));
            player.ESAdded += (_, _) => OnUi(ApplyAudio);
            player.Unmuted += (_, _) => OnUi(ApplyAudio);
            player.Buffering += (_, a) => OnUi(() => slot.Busy.IsActive = a.Cache < 100f && Chosen[slot.Index] is not null);
            player.EncounteredError += (_, _) => OnUi(() => OnFailed(slot));
            player.EndReached += (_, _) => OnUi(() => OnFailed(slot));
            slot.Player = player;
            video.MediaPlayer = player;
            Play(slot);
        };
        hit.Tapped += (_, _) => SelectAudio(index);
        hit.DoubleTapped += (_, _) => MakeBig(index);
        pick.Click += (_, _) => ShowPicker(slot, pick);
        close.Click += (_, _) =>
        {
            Chosen[index] = null;
            Play(slot);
            ApplyAudio();
        };
        UpdateChrome(slot);
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
        var heard = IsHeard(slot);
        slot.Title.Text = request?.Title ?? "";
        slot.Pick.Visibility = request is null ? Visibility.Visible : Visibility.Collapsed;
        slot.Close.Visibility = request is null ? Visibility.Collapsed : Visibility.Visible;
        slot.SpeakerBadge.Visibility = heard ? Visibility.Visible : Visibility.Collapsed;
        slot.Frame.BorderBrush = new SolidColorBrush(heard ? Colors.DodgerBlue : Colors.Transparent);
        if (request is null)
        {
            slot.Busy.IsActive = false;
            slot.Status.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsHeard(Slot slot) => slot.Index == _audioSlot && Chosen[slot.Index] is not null && !slot.Hidden;

    /// <summary>Exactly one tile, the selected one, has sound.</summary>
    private void ApplyAudio()
    {
        var selected = _slots[_audioSlot];
        if (Chosen[_audioSlot] is null || selected.Hidden)
        {
            // The selected tile is empty or off-screen: hand the sound to the first visible stream.
            var fallback = _slots.FirstOrDefault(s => Chosen[s.Index] is not null && !s.Hidden);
            if (fallback is not null)
                _audioSlot = fallback.Index;
        }

        foreach (var slot in _slots)
        {
            var mute = !IsHeard(slot);
            if (slot.Player is { } p && p.Mute != mute)
                p.Mute = mute;
            UpdateChrome(slot);
        }
    }

    private void SelectAudio(int index)
    {
        if (Chosen[index] is null)
            return;
        _audioSlot = index;
        ApplyAudio();
    }

    private void Play(Slot slot)
    {
        UpdateChrome(slot);
        var request = Chosen[slot.Index];
        var version = ++slot.PlayVersion;
        if (slot.Player is not { } player || slot.Vlc is not { } vlc)
            return;
        if (request is null || slot.Hidden)
        {
            slot.Busy.IsActive = false;
            Worker.Run(() => player.Stop(), "multi-view stop");
            return;
        }

        slot.Busy.IsActive = true;
        slot.Status.Visibility = Visibility.Collapsed;
        player.Mute = !IsHeard(slot);

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
                return; // Superseded before it started.
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        }, "multi-view play");
    }

    private void OnPlaying(Slot slot)
    {
        slot.Retry = 0;
        slot.Busy.IsActive = false;
        slot.Status.Visibility = Visibility.Collapsed;
        ApplyAudio();
    }

    /// <summary>Live streams that drop or end are retried with backoff.</summary>
    private async void OnFailed(Slot slot)
    {
        if (Chosen[slot.Index] is null || slot.Hidden)
            return;
        slot.Busy.IsActive = false;
        if (slot.Retry >= RetryDelays.Length)
        {
            slot.Status.Text = "Stream unavailable";
            slot.Status.Visibility = Visibility.Visible;
            return;
        }

        var version = slot.PlayVersion;
        var delay = RetryDelays[slot.Retry++];
        slot.Status.Text = $"Reconnecting ({slot.Retry}/{RetryDelays.Length})…";
        slot.Status.Visibility = Visibility.Visible;
        await Task.Delay(delay);
        if (!_leaving && version == slot.PlayVersion)
            Play(slot);
    }

    // ---- Layout ----

    private void ApplyLayout()
    {
        Slots.RowDefinitions.Clear();
        Slots.ColumnDefinitions.Clear();
        GridLength Star(double n = 1) => new(n, GridUnitType.Star);
        var visible = new HashSet<int>();

        switch (_layout)
        {
            case "focus":
                // One big tile on the left, the other three stacked on the right.
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(3) });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(1) });
                for (var r = 0; r < 3; r++)
                    Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                var small = 0;
                foreach (var slot in _slots)
                {
                    visible.Add(slot.Index);
                    if (slot.Index == _bigSlot)
                        Place(slot, 0, 0, rowSpan: 3);
                    else
                        Place(slot, small++, 1);
                }
                break;

            case "pair":
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                // The selected tile first, then the next tile with a stream.
                var order = _slots
                    .OrderByDescending(s => s.Index == _audioSlot && Chosen[s.Index] is not null)
                    .ThenByDescending(s => Chosen[s.Index] is not null)
                    .ThenBy(s => s.Index)
                    .ToList();
                for (var i = 0; i < order.Count; i++)
                {
                    if (i < 2)
                    {
                        visible.Add(order[i].Index);
                        Place(order[i], 0, i);
                    }
                    else
                    {
                        // Parked behind the first tile: never resized to zero (that can crash the video surface).
                        Place(order[i], 0, 0);
                    }
                }
                break;

            default:
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                foreach (var slot in _slots)
                {
                    visible.Add(slot.Index);
                    Place(slot, slot.Index / 2, slot.Index % 2);
                }
                break;
        }

        foreach (var slot in _slots)
        {
            var hidden = !visible.Contains(slot.Index);
            var changed = hidden != slot.Hidden;
            slot.Hidden = hidden;
            slot.Root.Opacity = hidden ? 0 : 1;
            slot.Root.IsHitTestVisible = !hidden;
            Canvas.SetZIndex(slot.Root, hidden ? -1 : 0);
            if (changed)
                Play(slot); // Pauses tiles going off-screen, restarts ones coming back.
        }
        ApplyAudio();
    }

    private static void Place(Slot slot, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(slot.Root, row);
        Grid.SetColumn(slot.Root, column);
        Grid.SetRowSpan(slot.Root, rowSpan);
    }

    private void MakeBig(int index)
    {
        _bigSlot = index;
        if (Chosen[index] is not null)
            _audioSlot = index;
        _layout = "focus";
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;
        ApplyLayout();
    }

    private void Layout_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Slots is null || _slots[SlotCount - 1] is null)
            return; // Fires during InitializeComponent.
        var layout = sender.SelectedItem?.Tag as string ?? "grid";
        if (layout == _layout && Slots.ColumnDefinitions.Count > 0)
            return;
        _layout = layout;
        ApplyLayout();
    }

    // ---- Picking channels ----

    private void ShowPicker(Slot slot, FrameworkElement anchor)
    {
        var box = new AutoSuggestBox { PlaceholderText = "Search channels or events (e.g. NBA, man city)", Width = 360, QueryIcon = new SymbolIcon(Symbol.Find) };
        var flyout = new Flyout { Content = box };
        List<LibraryEntry> hits = [];
        var version = 0;

        // Wait for a pause in typing, then search off the UI thread; drop results from older keystrokes.
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(300);
        debounce.IsRepeating = false;
        debounce.Tick += async (_, _) =>
        {
            var text = box.Text.Trim();
            var mine = ++version;
            if (text.Length < 2)
            {
                box.ItemsSource = null;
                return;
            }
            box.ItemsSource = new List<string> { "Searching…" };
            List<LibraryEntry> found;
            try
            {
                found = await Task.Run(() =>
                {
                    var now = DateTimeOffset.Now;
                    // Channels showing a matching event right now first ("man utd", "nba"), then by name.
                    var byEvent = AppServices.Library.SearchEvents(text, now, now.AddSeconds(1), 8).SelectMany(h => h.Channels.Take(1));
                    var byName = AppServices.Library.Search(text, MediaKind.Live, 12);
                    return byEvent.Concat(byName).DistinctBy(e => (e.Item.SourceId, e.Item.ExternalId)).Take(16).ToList();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "multi-view search");
                found = [];
            }
            if (mine != version)
                return;
            hits = found;
            box.ItemsSource = hits.Count == 0
                ? new List<string> { "No matching channels" }
                : hits.Select(h => $"{h.Item.Title}  ·  {h.SourceName}").ToList();
        };

        box.TextChanged += (s, a) =>
        {
            if (a.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            debounce.Stop();
            debounce.Start();
        };

        void Choose(int index)
        {
            if (index < 0 || index >= hits.Count)
                return;
            Chosen[slot.Index] = ForTile(PlayRequest.ForItem(hits[index].Item));
            slot.Retry = 0;
            flyout.Hide();
            Play(slot);
            ApplyAudio();
        }

        box.SuggestionChosen += (s, a) =>
            Choose((s.ItemsSource as List<string>)?.IndexOf(a.SelectedItem as string ?? "") ?? -1);
        // Enter picks the top result.
        box.QuerySubmitted += (_, a) =>
        {
            if (a.ChosenSuggestion is null)
                Choose(0);
        };
        flyout.Closed += (_, _) => debounce.Stop();

        flyout.ShowAt(anchor);
        box.Focus(FocusState.Programmatic);
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
            player?.Stop();
            player?.Dispose();
            vlc?.Dispose();
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
}
