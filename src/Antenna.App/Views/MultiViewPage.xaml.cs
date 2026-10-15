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

    private readonly Slot[] _slots = new Slot[SlotCount];

    private sealed class Slot
    {
        public required int Index;
        public required Grid Root;
        public required TextBlock Title;
        public required Border Frame;
        public required Button Pick;
        public required Button Close;
        public required VideoView Video;
        public LibVLC? Vlc;
        public MediaPlayer? Player;
    }

    public MultiViewPage()
    {
        InitializeComponent();
        for (var i = 0; i < SlotCount; i++)
            _slots[i] = CreateSlot(i);
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;
        ApplyLayout();
    }

    /// <summary>Puts a channel in the first free tile (or replaces the last one) and opens multi-view.</summary>
    public static void Add(PlayRequest request)
    {
        var free = Array.FindIndex(Chosen, c => c is null);
        Chosen[free >= 0 ? free : SlotCount - 1] = ForTile(request);
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
            Margin = new Thickness(10, 6, 44, 6),
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var frame = new Border { BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false };
        var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var pick = new Button { Content = "＋ Choose channel", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RequestedTheme = ElementTheme.Dark };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            RequestedTheme = ElementTheme.Dark,
        };
        ToolTipService.SetToolTip(close, "Remove");

        var root = new Grid { Background = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 16, 20)) };
        root.Children.Add(video);
        root.Children.Add(hit);
        root.Children.Add(title);
        root.Children.Add(pick);
        root.Children.Add(close);
        root.Children.Add(frame);
        Slots.Children.Add(root);

        var slot = new Slot { Index = index, Root = root, Title = title, Frame = frame, Pick = pick, Close = close, Video = video };
        video.Initialized += (_, e) =>
        {
            slot.Vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
            slot.Player = new MediaPlayer(slot.Vlc) { Mute = index != _audioSlot };
            video.MediaPlayer = slot.Player;
            Play(slot);
        };
        hit.Tapped += (_, _) => SetAudio(index);
        hit.DoubleTapped += (_, _) => MakeBig(index);
        pick.Click += (_, _) => ShowPicker(slot, pick);
        close.Click += (_, _) =>
        {
            Chosen[index] = null;
            Play(slot);
        };
        UpdateChrome(slot);
        return slot;
    }

    private void UpdateChrome(Slot slot)
    {
        var request = Chosen[slot.Index];
        slot.Title.Text = request?.Title ?? "";
        slot.Pick.Visibility = request is null ? Visibility.Visible : Visibility.Collapsed;
        slot.Close.Visibility = request is null ? Visibility.Collapsed : Visibility.Visible;
        slot.Frame.BorderBrush = new SolidColorBrush(slot.Index == _audioSlot && request is not null ? Colors.DodgerBlue : Colors.Transparent);
    }

    private void Play(Slot slot)
    {
        UpdateChrome(slot);
        var request = Chosen[slot.Index];
        if (slot.Player is not { } player || slot.Vlc is not { } vlc)
            return;
        if (request is null)
        {
            Worker.Run(() => player.Stop());
            return;
        }

        var options = new List<string>
        {
            $":network-caching={AppSettings.NetworkCachingMs}",
            ":http-reconnect",
            AppSettings.HardwareDecoding ? ":avcodec-hw=any" : ":avcodec-hw=none",
        };
        options.AddRange(request.Options);
        Worker.Run(() =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        });
    }

    // ---- Layout ----

    private void ApplyLayout()
    {
        Slots.RowDefinitions.Clear();
        Slots.ColumnDefinitions.Clear();
        GridLength Star(double n = 1) => new(n, GridUnitType.Star);

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
                    slot.Root.Visibility = Visibility.Visible;
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
                // The big/heard tile first, then the next filled one.
                var order = _slots.OrderByDescending(s => s.Index == _bigSlot).ThenByDescending(s => Chosen[s.Index] is not null).ToList();
                for (var i = 0; i < order.Count; i++)
                {
                    order[i].Root.Visibility = i < 2 ? Visibility.Visible : Visibility.Collapsed;
                    if (i < 2)
                        Place(order[i], 0, i);
                }
                break;

            default:
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                Slots.RowDefinitions.Add(new RowDefinition { Height = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                Slots.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
                foreach (var slot in _slots)
                {
                    slot.Root.Visibility = Visibility.Visible;
                    Place(slot, slot.Index / 2, slot.Index % 2);
                }
                break;
        }

        // Hidden tiles keep playing silently only if they're the heard one; otherwise mute.
        foreach (var slot in _slots)
        {
            if (slot.Player is { } p)
                p.Mute = slot.Index != _audioSlot || slot.Root.Visibility != Visibility.Visible;
        }
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
        _audioSlot = index;
        _layout = "focus";
        foreach (var item in LayoutChoice.Items)
            item.IsSelected = (item.Tag as string) == _layout;
        ApplyLayout();
        foreach (var slot in _slots)
            UpdateChrome(slot);
    }

    private void SetAudio(int index)
    {
        _audioSlot = index;
        foreach (var slot in _slots)
        {
            if (slot.Player is { } p)
                p.Mute = slot.Index != index;
            UpdateChrome(slot);
        }
    }

    private void Layout_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Slots is null || _slots[SlotCount - 1] is null)
            return; // Fires during InitializeComponent.
        _layout = sender.SelectedItem?.Tag as string ?? "grid";
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
            flyout.Hide();
            Play(slot);
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
