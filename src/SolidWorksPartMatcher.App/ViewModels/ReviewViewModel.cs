using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using System.Collections.ObjectModel;

namespace SolidWorksPartMatcher.App.ViewModels;

public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly IPartRepository _repo;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private PartClusterItem? _selectedCluster;

    public ObservableCollection<PartClusterItem> Clusters { get; } = [];

    public ReviewViewModel(IPartRepository repo) => _repo = repo;

    public async Task LoadAsync(Guid scanRunId, CancellationToken ct)
    {
        Clusters.Clear();
        var clusters = await _repo.GetClustersAsync(scanRunId, ct);
        foreach (var c in clusters)
            Clusters.Add(new PartClusterItem(c));
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (SelectedCluster is null) return;
        await _repo.UpdateClusterReviewAsync(
            SelectedCluster.Id,
            ReviewStatus.Approved,
            null,
            Environment.UserName,
            DateTime.UtcNow,
            CancellationToken.None);
        SelectedCluster.ReviewStatus = ReviewStatus.Approved.ToString();
        StatusText = $"Approved: {SelectedCluster.CanonicalName}";
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (SelectedCluster is null) return;
        await _repo.UpdateClusterReviewAsync(
            SelectedCluster.Id,
            ReviewStatus.Rejected,
            "Rejected by reviewer",
            Environment.UserName,
            DateTime.UtcNow,
            CancellationToken.None);
        SelectedCluster.ReviewStatus = ReviewStatus.Rejected.ToString();
        StatusText = $"Rejected: {SelectedCluster.CanonicalName}";
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedCluster is null) return;
        var dlg = new Views.RenameDialog { Owner = System.Windows.Application.Current.MainWindow };
        dlg.NewName = SelectedCluster.CanonicalName;
        if (dlg.ShowDialog() != true) return;

        await _repo.UpdateClusterCanonicalNameAsync(SelectedCluster.Id, dlg.NewName, CancellationToken.None);
        SelectedCluster.CanonicalName = dlg.NewName;
        StatusText = $"Renamed to: {dlg.NewName}";
    }
}

public sealed partial class PartClusterItem : ObservableObject
{
    public Guid Id { get; }
    [ObservableProperty] private string _canonicalName;
    [ObservableProperty] private string _classification;
    [ObservableProperty] private string _reviewStatus;

    public PartClusterItem(PartCluster c)
    {
        Id = c.Id;
        _canonicalName = c.CanonicalName;
        _classification = c.Classification.ToString();
        _reviewStatus = c.ReviewStatus.ToString();
    }
}
