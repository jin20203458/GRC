using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GRC.Views;

public partial class EditWorldviewWindow : Window
{
    // 화면에 바인딩될 동적 스탯 리스트
    public ObservableCollection<StatInputItem> StatItems { get; set; } = new();
    public string? ChangedInitialScenario { get; private set; } = null;
    private string _currentInitialScenario;
    public class StatInputItem
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public string InputWorldview => WorldviewTextBox.Text;

    // 백엔드로 넘겨줄 문자열 형태 조립 (예: "체력: 100, 골드: 10")
    public string InputCustomStats
    {
        get
        {
            var validItems = StatItems.Where(x => !string.IsNullOrWhiteSpace(x.Name));
            return string.Join(", ", validItems.Select(x => $"{x.Name}: {x.Value}"));
        }
    }

    public EditWorldviewWindow(string currentWorldview, Dictionary<string, string>? currentStats, string currentScenario = "")
    {
        InitializeComponent();
        WorldviewTextBox.Text = currentWorldview;
        CustomStatsItemsControl.ItemsSource = StatItems;
        _currentInitialScenario = currentScenario; // 초기 시나리오 보관

        if (currentStats != null)
        {
            foreach (var kvp in currentStats)
                StatItems.Add(new StatInputItem { Name = kvp.Key, Value = kvp.Value });
        }

        if (Application.Current.MainWindow != this)
            this.Owner = Application.Current.MainWindow;
    }

    private void AddStatButton_Click(object sender, RoutedEventArgs e)
    {
        StatItems.Add(new StatInputItem { Name = "새 스탯", Value = "0" });
    }

    private void RemoveStatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StatInputItem item)
        {
            StatItems.Remove(item);
        }
    }
    private void EditScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditInitialScenarioWindow(ChangedInitialScenario ?? _currentInitialScenario);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            ChangedInitialScenario = dialog.InputScenario;
        }
    }
    private void SaveButton_Click(object sender, RoutedEventArgs e) => this.DialogResult = true;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;

}
