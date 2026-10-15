using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Antenna.App.Services;

/// <summary>
/// A list that loads pages on demand as a GridView/ListView scrolls, so huge categories
/// open instantly and only fetch what's on screen.
/// </summary>
internal sealed class IncrementalList<T>(Func<int, int, Task<IReadOnlyList<T>>> loadPage, int pageSize = 200)
    : ObservableCollection<T>, ISupportIncrementalLoading
{
    private bool _loading;

    public bool HasMoreItems { get; private set; } = true;

    /// <summary>Raised after each page, with whether anything was loaded so far.</summary>
    public event Action<bool>? PageLoaded;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => LoadAsync().AsAsyncOperation();

    private async Task<LoadMoreItemsResult> LoadAsync()
    {
        if (_loading || !HasMoreItems)
            return new LoadMoreItemsResult { Count = 0 };
        _loading = true;
        try
        {
            var page = await loadPage(Count, pageSize);
            foreach (var item in page)
                Add(item);
            HasMoreItems = page.Count == pageSize;
            PageLoaded?.Invoke(Count > 0);
            return new LoadMoreItemsResult { Count = (uint)page.Count };
        }
        catch (Exception)
        {
            HasMoreItems = false;
            PageLoaded?.Invoke(Count > 0);
            return new LoadMoreItemsResult { Count = 0 };
        }
        finally
        {
            _loading = false;
        }
    }
}
