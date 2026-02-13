using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Screener.Golf.Models;
using Screener.Golf.Persistence;

namespace Screener.UI.ViewModels;

/// <summary>
/// ViewModel for the golfer management dialog (add/edit/delete golfer profiles).
/// </summary>
public partial class GolferManagementViewModel : ObservableObject
{
    private readonly GolferRepository _golferRepository;
    private readonly ILogger<GolferManagementViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<GolferProfile> _golfers = new();

    [ObservableProperty]
    private GolferProfile? _selectedGolfer;

    [ObservableProperty]
    private string _editFirstName = string.Empty;

    [ObservableProperty]
    private string _editLastName = string.Empty;

    [ObservableProperty]
    private string _editDisplayName = string.Empty;

    /// <summary>Fired to close the dialog window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public GolferManagementViewModel(
        GolferRepository golferRepository,
        ILogger<GolferManagementViewModel> logger)
    {
        _golferRepository = golferRepository;
        _logger = logger;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var golfers = await _golferRepository.GetAllAsync();
            Golfers = new ObservableCollection<GolferProfile>(golfers);
            if (Golfers.Count > 0)
                SelectedGolfer = Golfers[0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load golfers");
        }
    }

    partial void OnSelectedGolferChanged(GolferProfile? value)
    {
        if (value != null)
        {
            EditFirstName = value.FirstName;
            EditLastName = value.LastName;
            EditDisplayName = value.DisplayName ?? string.Empty;
        }
        else
        {
            EditFirstName = string.Empty;
            EditLastName = string.Empty;
            EditDisplayName = string.Empty;
        }
    }

    [RelayCommand]
    private void AddGolfer()
    {
        var golfer = new GolferProfile
        {
            FirstName = "New",
            LastName = "Golfer"
        };
        Golfers.Add(golfer);
        SelectedGolfer = golfer;
    }

    [RelayCommand]
    private async Task SaveGolferAsync()
    {
        if (SelectedGolfer == null) return;

        try
        {
            SelectedGolfer.FirstName = EditFirstName;
            SelectedGolfer.LastName = EditLastName;
            SelectedGolfer.DisplayName = string.IsNullOrWhiteSpace(EditDisplayName) ? null : EditDisplayName;

            // Check if this golfer exists in DB already
            var existing = await _golferRepository.GetByIdAsync(SelectedGolfer.Id);
            if (existing != null)
            {
                await _golferRepository.UpdateAsync(SelectedGolfer);
            }
            else
            {
                await _golferRepository.CreateAsync(SelectedGolfer);
            }

            _logger.LogInformation("Saved golfer: {Name}", SelectedGolfer.EffectiveDisplayName);

            // Refresh display
            OnPropertyChanged(nameof(SelectedGolfer));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save golfer");
        }
    }

    [RelayCommand]
    private async Task DeleteGolferAsync()
    {
        if (SelectedGolfer == null) return;

        try
        {
            await _golferRepository.DeleteAsync(SelectedGolfer.Id);
            Golfers.Remove(SelectedGolfer);
            SelectedGolfer = Golfers.FirstOrDefault();
            _logger.LogInformation("Deleted golfer");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete golfer");
        }
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, true);
    }
}
