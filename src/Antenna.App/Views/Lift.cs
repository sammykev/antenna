using System.Numerics;
using Microsoft.UI.Xaml;

namespace Antenna.App.Views;

/// <summary>
/// Cards grow slightly under the pointer, like artwork on Apple TV. Set <c>views:Lift.On="True"</c>
/// on a card's root. Off when Windows' "Animation effects" setting is off.
/// </summary>
public static class Lift
{
    private const float Raised = 1.04f;
    private static readonly bool AnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(Lift), new PropertyMetadata(false, OnChanged));

    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);

    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!AnimationsEnabled || e.NewValue is not true || d is not FrameworkElement card)
            return;
        card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
        card.SizeChanged += (_, a) => card.CenterPoint = new Vector3((float)a.NewSize.Width / 2, (float)a.NewSize.Height / 2, 0);
        card.PointerEntered += (_, _) => card.Scale = new Vector3(Raised, Raised, 1);
        card.PointerExited += (_, _) => card.Scale = Vector3.One;
        card.PointerCanceled += (_, _) => card.Scale = Vector3.One;
        card.PointerCaptureLost += (_, _) => card.Scale = Vector3.One;
    }
}
