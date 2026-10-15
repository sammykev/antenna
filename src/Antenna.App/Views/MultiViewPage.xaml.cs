using Antenna.App.Services;
using Antenna.Core;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Antenna.App.Views;

/// <summary>
/// Four players in a 2×2 grid. Each tile has its own LibVLC instance (one per video surface).
/// The chosen channels are remembered while the app runs, so coming back restores them.
/// </summary>
public sealed partial class MultiViewPage : Page
{
    private const int SlotCount = 4;

    /// <summary>What's in each tile; survives leaving and returning to the page.</summary>
    private static readonly PlayRequest?[] Chosen = new PlayRequest?[SlotCount];
    private static int _audioSlot;

    private readonly Slot[] _slots = new Slot[SlotCount];

    private sealed class Slot
    {
        public required int Index;
        public required Grid Root;
        public required VideoView Video;
        public required TextBlock Title;
        public required Border Frame;
        public LibVLC? Vlc;
        public MediaPlayer? Player;
    }

    public MultiViewPage()
    {
        InitializeComponent();
        for (var i = 0; i < SlotCount; i++)
            _slots[i] = CreateSlot(i);
    }

    /// <summary>Puts a channel in the first free tile (or replaces the last one) and opens multi-view.</summary>
    public static void Add(PlayRequest request)
    {
        var free = Array.FindIndex(Chosen, c => c is null);
        Chosen[free >= 0 ? free : SlotCount - 1] = request with { Channels = [], ChannelIndex = -1 };
        App.Window.Frame.Navigate(typeof(MultiViewPage));
    }

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
            Margin = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var frame = new Border { BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false };
        var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

        var pick = new Button { Content = "＋ Choose channel", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RequestedTheme = ElementTheme.Dark };
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), RequestedTheme = ElementTheme.Dark };

        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 16, 16, 20)) };
        root.Children.Add(video);
        root.Children.Add(hit);
        root.Children.Add(title);
        root.Children.Add(pick);
        root.Children.Add(close);
        root.Children.Add(frame);
        Grid.SetRow(root, index / 2);
        Grid.SetColumn(root, index % 2);
        Slots.Children.Add(root);

        var slot = new Slot { Index = index, Root = root, Video = video, Title = title, Frame = frame };
        video.Initialized += (_, e) =>
        {
            slot.Vlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
            slot.Player = new MediaPlayer(slot.Vlc) { Mute = index != _audioSlot };
            video.MediaPlayer = slot.Player;
            Play(slot);
        };
        hit.Tapped += (_, _) => SetAudio(index);
        pick.Click += (_, _) => ShowPicker(slot, pick);
        close.Click += (_, _) =>
        {
            Chosen[index] = null;
            Play(slot);
        };

        void UpdateButtons()
        {
            pick.Visibility = Chosen[index] is null ? Visibility.Visible : Visibility.Collapsed;
            close.Visibility = Chosen[index] is null ? Visibility.Collapsed : Visibility.Visible;
        }
        slot.Root.Tag = (Action)UpdateButtons;
        UpdateButtons();
        return slot;
    }

    private void Play(Slot slot)
    {
        var request = Chosen[slot.Index];
        slot.Title.Text = request?.Title ?? "";
        (slot.Root.Tag as Action)?.Invoke();
        slot.Frame.BorderBrush = new SolidColorBrush(slot.Index == _audioSlot && request is not null ? Colors.DodgerBlue : Colors.Transparent);

        if (slot.Player is not { } player || slot.Vlc is not { } vlc)
            return;
        if (request is null)
        {
            ThreadPool.QueueUserWorkItem(_ => player.Stop());
            return;
        }

        var options = new List<string> { $":network-caching={AppSettings.NetworkCachingMs}", ":http-reconnect" };
        options.AddRange(request.Options);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            using var media = new Media(vlc, request.Url, FromType.FromLocation, [.. options]);
            player.Play(media);
        });
    }

    private void SetAudio(int index)
    {
        _audioSlot = index;
        foreach (var slot in _slots)
        {
            if (slot.Player is { } p)
                p.Mute = slot.Index != index;
            slot.Frame.BorderBrush = new SolidColorBrush(slot.Index == index && Chosen[slot.Index] is not null ? Colors.DodgerBlue : Colors.Transparent);
        }
    }

    private void ShowPicker(Slot slot, FrameworkElement anchor)
    {
        var box = new AutoSuggestBox { PlaceholderText = "Search channels", Width = 320, QueryIcon = new SymbolIcon(Symbol.Find) };
        var flyout = new Flyout { Content = box };
        List<LibraryEntry> hits = [];
        box.TextChanged += async (s, a) =>
        {
            if (a.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            var text = s.Text;
            hits = (await Task.Run(() => AppServices.Library.Search(text, MediaKind.Live, 12))).ToList();
            s.ItemsSource = hits.Select(h => $"{h.Item.Title}  ·  {h.SourceName}").ToList();
        };
        box.SuggestionChosen += (s, a) =>
        {
            var index = (s.ItemsSource as List<string>)?.IndexOf(a.SelectedItem as string ?? "") ?? -1;
            if (index < 0 || index >= hits.Count)
                return;
            Chosen[slot.Index] = PlayRequest.ForItem(hits[index].Item) with { Channels = [], ChannelIndex = -1 };
            flyout.Hide();
            Play(slot);
        };
        flyout.ShowAt(anchor);
        box.Focus(FocusState.Programmatic);
    }

    private static void Release(Slot slot)
    {
        var player = slot.Player;
        var vlc = slot.Vlc;
        slot.Player = null;
        slot.Vlc = null;
        _ = Task.Run(() =>
        {
            player?.Stop();
            player?.Dispose();
            vlc?.Dispose();
        });
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => App.Window.SetFullScreen(!App.Window.IsFullScreen);
}
