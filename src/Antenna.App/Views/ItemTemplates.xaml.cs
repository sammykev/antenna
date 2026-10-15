using Microsoft.UI.Xaml;

namespace Antenna.App.Views;

/// <summary>Code-behind is required for x:Bind inside a ResourceDictionary.</summary>
public sealed partial class ItemTemplates : ResourceDictionary
{
    private static ItemTemplates? _shared;

    public ItemTemplates()
    {
        InitializeComponent();
    }

    /// <summary>A template by key, for code that picks templates at run time.</summary>
    public static DataTemplate Get(string key) => (DataTemplate)(_shared ??= new ItemTemplates())[key];
}
