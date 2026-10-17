using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Size = System.Windows.Size;

namespace RinkoCamera.Tests;

public sealed class ThemeTests
{
    // A missing ContentTemplateSelector in a custom ComboBox template exposes
    // record.ToString() (including handles/device IDs) instead of the display label.
    [Theory]
    [InlineData("Name", "播放设备")]
    [InlineData("Label", "系统声音")]
    [InlineData("DisplayName", "窗口 · 示例")]
    public async Task Selector_RendersDisplayMemberInsteadOfInternalRecord(string member, string label)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var theme = (ResourceDictionary)System.Windows.Application.LoadComponent(
                    new Uri("/RinkoCamera;component/Themes/Tsuki.xaml", UriKind.Relative));
                var value = new Choice(label, label, label, "internal-device-id");
                var combo = new ComboBox
                {
                    Style = (Style)theme[typeof(ComboBox)],
                    DisplayMemberPath = member,
                    ItemsSource = new[] { value }, SelectedIndex = 0, Width = 300, Height = 38
                };
                combo.Measure(new Size(300, 38));
                combo.Arrange(new Rect(0, 0, 300, 38));
                combo.UpdateLayout();
                var displayed = Descendants(combo).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Assert.Contains(label, displayed);
                Assert.DoesNotContain(displayed, text => text.Contains("internal-device-id"));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public sealed record Choice(string Name, string Label, string DisplayName, string Id);
}
