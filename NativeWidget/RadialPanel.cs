using System.Windows;
using System.Windows.Controls;

namespace NativeWidget;

/// Arranges every child evenly around a circle. The radius is derived from the available
/// space and the largest child, so adding/removing launcher actions automatically closes up
/// or redistributes the remaining space without hand-tuned canvas coordinates.
public class RadialPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(availableSize);
        return new Size(availableSize.Width, availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;

        var largest = InternalChildren.Cast<UIElement>()
            .Select(child => Math.Max(child.DesiredSize.Width, child.DesiredSize.Height))
            .DefaultIfEmpty(0)
            .Max();
        var radius = Math.Max(0, Math.Min(finalSize.Width, finalSize.Height) / 2 - largest / 2 - 10);
        var centerX = finalSize.Width / 2;
        var centerY = finalSize.Height / 2;
        var step = 2 * Math.PI / InternalChildren.Count;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var angle = -Math.PI / 2 + step * i;
            var x = centerX + radius * Math.Cos(angle) - child.DesiredSize.Width / 2;
            var y = centerY + radius * Math.Sin(angle) - child.DesiredSize.Height / 2;
            child.Arrange(new Rect(new Point(x, y), child.DesiredSize));
        }
        return finalSize;
    }
}
