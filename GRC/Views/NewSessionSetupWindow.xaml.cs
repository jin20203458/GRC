using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GRC.Views;

public partial class NewSessionSetupWindow : Window
{
    // 💡 화면에 바인딩될 동적 스탯 리스트
    public ObservableCollection<StatInputItem> StatItems { get; set; } = new();

    // 입력 데이터 모델
    public class StatInputItem
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public string InputName => NameTextBox.Text;
    public string InputWorldview => WorldviewTextBox.Text;
    public string InputScenario => ScenarioTextBox.Text;

    // 💡 기존 텍스트박스 값을 읽어오던 것을 리스트 조립으로 변경 (다른 파일 수정 불필요)
    public string InputCustomStats
    {
        get
        {
            // 이름이 입력된 유효한 항목만 골라서 백엔드 파싱 형식에 맞게 문자열로 조립
            var validItems = StatItems.Where(x => !string.IsNullOrWhiteSpace(x.Name));
            return string.Join(", ", validItems.Select(x => $"{x.Name}: {x.Value}"));
        }
    }

    public NewSessionSetupWindow()
    {
        InitializeComponent();

        // ItemsControl에 컬렉션 바인딩
        CustomStatsItemsControl.ItemsSource = StatItems;

        if (Application.Current.MainWindow != this)
        {
            this.Owner = Application.Current.MainWindow;
        }
    }

    // [+ 스탯 추가] 버튼 클릭 시
    private void AddStatButton_Click(object sender, RoutedEventArgs e)
    {
        StatItems.Add(new StatInputItem { Name = "새 스탯", Value = "0" });
    }

    // [✕] 삭제 버튼 클릭 시
    private void RemoveStatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StatInputItem item)
        {
            StatItems.Remove(item);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }
}