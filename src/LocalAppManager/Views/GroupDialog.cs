using LocalAppManager.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocalAppManager.Views;

/// <summary>Create or edit a group: a name and a set of projects started/stopped together.</summary>
public sealed partial class GroupDialog : ContentDialog
{
    private readonly TextBox _name = new() { Header = "그룹 이름", PlaceholderText = "예: 프론트 + API" };
    private readonly List<(CheckBox Box, string Id)> _checks = [];

    public GroupDialog(ProjectManager manager, string? groupId)
    {
        var group = groupId is null ? null : manager.Groups.FirstOrDefault(g => g.Id == groupId);
        Title = group is null ? "그룹 만들기" : "그룹 편집";
        PrimaryButtonText = group is null ? "만들기" : "저장";
        CloseButtonText = "취소";
        DefaultButton = ContentDialogButton.Primary;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _name.Text = group?.Name ?? "";

        var list = new StackPanel { Spacing = 2 };
        foreach (var p in manager.Projects)
        {
            var box = new CheckBox { Content = p.Definition.Name, IsChecked = group?.ProjectIds.Contains(p.Id) ?? false };
            box.Checked += (_, _) => Validate();
            box.Unchecked += (_, _) => Validate();
            _checks.Add((box, p.Id));
            list.Children.Add(box);
        }
        if (_checks.Count == 0)
            list.Children.Add(new TextBlock { Text = "먼저 프로젝트를 추가하세요.", Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"] });

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children =
            {
                _name,
                new TextBlock { Text = "함께 시작/정지할 프로젝트", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                new ScrollViewer { Content = list, MaxHeight = 360 },
            },
        };
        _name.TextChanged += (_, _) => Validate();
        Validate();
    }

    public string GroupName => _name.Text.Trim();

    public IReadOnlyList<string> SelectedIds => _checks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList();

    private void Validate() => IsPrimaryButtonEnabled = GroupName.Length > 0 && SelectedIds.Count > 0;
}
