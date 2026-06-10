using GRC.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace GRC.Views;

public partial class EditLorebookWindow : Window, INotifyPropertyChanged
{
    // 화면 좌측 리스트에 바인딩될 컬렉션
    public ObservableCollection<EditableLorebookEntry> LorebookItems { get; set; } = new();

    // 현재 선택된 로어북 (우측 편집 영역에 바인딩됨)
    private EditableLorebookEntry _selectedLorebook;
    public EditableLorebookEntry SelectedLorebook
    {
        get => _selectedLorebook;
        set
        {
            _selectedLorebook = value;
            OnPropertyChanged(nameof(SelectedLorebook));
        }
    }

    // ChatViewModel로 돌려줄 최종 결과물
    public List<LorebookEntry> FinalLorebooks { get; private set; } = new();

    public EditLorebookWindow(List<LorebookEntry>? existingLorebooks)
    {
        InitializeComponent();
        DataContext = this; // XAML의 Binding이 이 클래스를 바라보도록 설정

        // 1. 기존 데이터 불러오기 (읽기 전용 Record를 편집 가능한 객체로 변환)
        if (existingLorebooks != null)
        {
            foreach (var lore in existingLorebooks)
            {
                LorebookItems.Add(new EditableLorebookEntry
                {
                    Name = lore.Name,
                    KeywordsString = string.Join(", ", lore.Keywords), // 리스트를 쉼표 문자열로 변환
                    Content = lore.Content,
                    Category = lore.Category,
                    Priority = lore.Priority.ToString()
                });
            }
        }

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(LorebookItems);

        // 1순위: 카테고리 기준 오름차순(가나다순) 정렬
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Category", System.ComponentModel.ListSortDirection.Ascending));
        // 2순위: 같은 카테고리 내에서는 항목 이름 기준 오름차순 정렬
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));


        // 데이터가 있다면 첫 번째 항목을 자동 선택 (오류 수정됨)
        if (LorebookItems.Count > 0)
        {
            SelectedLorebook = LorebookItems.FirstOrDefault(); // <--- 여기를 수정했습니다.
        }
    }

    // ==========================================
    // 버튼 클릭 이벤트 핸들러
    // ==========================================

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var newItem = new EditableLorebookEntry
        {
            Name = "새 로어북",
            Category = "인물",
            KeywordsString = "",
            Content = ""
        };
        LorebookItems.Add(newItem);
        SelectedLorebook = newItem; // 추가된 항목을 바로 편집할 수 있게 포커스
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLorebook != null)
        {
            LorebookItems.Remove(SelectedLorebook);
            SelectedLorebook = LorebookItems.FirstOrDefault(); // 삭제 후 다음 항목 선택
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 2. 편집된 데이터들을 다시 불변 객체(Record)인 LorebookEntry 리스트로 변환하여 저장
        FinalLorebooks = LorebookItems.Select(item => new LorebookEntry(
            Name: item.Name ?? "이름 없음",
            Keywords: string.IsNullOrWhiteSpace(item.KeywordsString)
                        ? new List<string>()
                        : item.KeywordsString.Split(',').Select(k => k.Trim()).Where(k => !string.IsNullOrEmpty(k)).ToList(),
            Content: item.Content ?? "",
            Category: item.Category ?? "기타",
            Priority: int.TryParse(item.Priority, out int p) ? p : 0
        )).ToList();

        DialogResult = true; // ChatViewModel에 true 반환
        Close();
    }

    // INotifyPropertyChanged 구현
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    private void BtnSortCategory_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // 1. UI 스타일 스왑 (카테고리 버튼을 활성화)
        BtnSortCategory.Style = (System.Windows.Style)FindResource("HeaderActionButton");
        BtnSortPriority.Style = (System.Windows.Style)FindResource("GhostButton");

        // 2. 정렬 로직 (카테고리순)
        if (LorebookItems == null) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(LorebookItems);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Category", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
    }

    private void BtnSortPriority_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // 1. UI 스타일 스왑 (우선순위 버튼을 활성화)
        BtnSortPriority.Style = (System.Windows.Style)FindResource("HeaderActionButton");
        BtnSortCategory.Style = (System.Windows.Style)FindResource("GhostButton");

        // 2. 정렬 로직 (우선순위순)
        if (LorebookItems == null) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(LorebookItems);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Priority", System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Name", System.ComponentModel.ListSortDirection.Ascending));
    }

}

/// <summary>
/// 모델(Record)은 변경이 불가능하므로, UI 텍스트박스에서 양방향 바인딩(Two-Way)으로 
/// 실시간 편집을 하기 위한 전용 클래스입니다.
/// </summary>
public class EditableLorebookEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); } }

    private string _keywordsString = string.Empty;
    public string KeywordsString { get => _keywordsString; set { _keywordsString = value; OnPropertyChanged(nameof(KeywordsString)); } }

    private string _content = string.Empty;
    public string Content { get => _content; set { _content = value; OnPropertyChanged(nameof(Content)); } }

    private string _category = string.Empty;
    public string Category { get => _category; set { _category = value; OnPropertyChanged(nameof(Category)); } }

    private string _priority = "0";
    public string Priority
    {
        get => _priority;
        set
        {
            _priority = value;
            OnPropertyChanged(nameof(Priority));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}