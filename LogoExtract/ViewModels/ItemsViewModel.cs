using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogoExtract.Core;
using LogoExtract.Services;

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

    // Resolved from Colors.xaml (named resources, not a duplicated hex literal here) -- these 4 are
    // deliberately single, non-AppThemeBinding literals (§2.4: a real pipeline-state distinction, not a brand
    // color), so a plain one-time dictionary lookup is correct; no RequestedThemeChanged subscription needed.
    public Color StateColor => Record.State switch
    {
        ItemState.Imported => ResourceColor("StateImported"),
        ItemState.Segmented => ResourceColor("StateSegmented"),
        ItemState.Flattened => ResourceColor("StateFlattened"),
        ItemState.Vectorized => ResourceColor("StateVectorized"),
        _ => Colors.Gray,
    };

    // The "Hoàn tất" badge uses the success-green literal, which is dark enough to need the same near-black
    // text as a Primary-filled button (see PrimaryDarkText's own remarks); the other 3 badge colors were picked
    // specifically to clear >=4.5:1 under plain White (see Colors.xaml's own remarks on this token group).
    public Color StateTextColor => Record.State == ItemState.Vectorized ? ResourceColor("PrimaryDarkText") : Colors.White;

    public string ThumbPath => Path.Combine(itemFolder, Record.ThumbFile ?? Record.OriginalFile);

    private static Color ResourceColor(string key) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color c ? c : Colors.Gray;
}

public partial class ItemsViewModel : ObservableObject
{
    private readonly ItemStore _store;
    private readonly AppDialog _dialog;

    public ItemsViewModel(ItemStore store, AppDialog dialog)
    {
        _store = store;
        _dialog = dialog;
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

    // Was an instant delete on tap -- the one destructive action in this app with no confirmation at all
    // (consistency pass, 2026-10-27). Same shape as every other app's delete-confirm: AppDialog.ConfirmAsync,
    // destructive: true turns the accept button the portfolio-wide #D32F2F, never deletes until accepted.
    [RelayCommand]
    private async Task DeleteAsync(ItemRow row)
    {
        bool ok = await _dialog.ConfirmAsync("Xoá mục này?", $"Xoá \"{row.Name}\"? Không thể hoàn tác.", "Xoá", "Huỷ", destructive: true);
        if (!ok) return;
        _store.Delete(row.Record.Id);
        Refresh();
    }
}
