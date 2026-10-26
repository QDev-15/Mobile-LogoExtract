using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;

namespace LogoExtract.ViewModels;

/// <summary>One row of the items-view list (PLAN.md 2.6). Tap/delete are wired from the page's XAML to the
/// parent ItemsViewModel's own commands with this row as the CommandParameter, not owned by the row itself.</summary>
public sealed class ItemRow(LogoItem record, string itemFolder)
{
    public LogoItem Record { get; } = record;
    public string Name => Record.Name;
    public string StateText => Record.State switch
    {
        ItemState.Imported => "Đang tách nền",
        ItemState.Segmented => "Chờ làm phẳng",
        ItemState.Flattened => "Đã làm phẳng",
        ItemState.Vectorized => "Hoàn tất",
        _ => "",
    };
    public Color StateColor => Record.State switch
    {
        ItemState.Imported => Color.FromArgb("#2E7FD6"),
        ItemState.Segmented => Color.FromArgb("#D98A1F"),
        ItemState.Flattened => Color.FromArgb("#8A5CF0"),
        ItemState.Vectorized => Color.FromArgb("#1FAE82"),
        _ => Colors.Gray,
    };
    public string ThumbPath => Path.Combine(itemFolder, Record.ThumbFile ?? Record.OriginalFile);
}

public partial class ItemsViewModel : ObservableObject
{
    private readonly ItemStore _store;

    public ItemsViewModel(ItemStore store)
    {
        _store = store;
        Refresh();
    }

    public ObservableCollection<ItemRow> Items { get; } = [];

    [ObservableProperty]
    private bool isEmpty;

    public void Refresh()
    {
        Items.Clear();
        foreach (LogoItem record in _store.List())
            Items.Add(new ItemRow(record, _store.ItemFolder(record.Id)));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task ImportAsync() => await Shell.Current.GoToAsync(nameof(Views.ImportPage));

    [RelayCommand]
    private async Task OpenAsync(ItemRow row) => await Shell.Current.GoToAsync($"{nameof(Views.SegmentPage)}?itemId={row.Record.Id}");

    [RelayCommand]
    private void Delete(ItemRow row)
    {
        _store.Delete(row.Record.Id);
        Refresh();
    }
}
