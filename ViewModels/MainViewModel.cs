using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.ViewModels;

public enum AppState { Login, LoadingStudents, SelectStudent, LoadingPhotos, Gallery, Downloading, Done }

public partial class MainViewModel : ObservableObject
{
    private readonly ProcareApiService _api;
    private readonly DownloadService _downloader;
    private readonly DownloadHistoryService _history;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainViewModel(
        ProcareApiService api,
        DownloadService downloader,
        DownloadHistoryService history,
        SettingsService settingsService)
    {
        _api = api;
        _downloader = downloader;
        _history = history;
        _settingsService = settingsService;
        _settings = _settingsService.Load();

        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.Flat,
            "Single Folder",
            "Save every image directly in the chosen folder."));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.YearMonth,
            "Year / Month",
            "Save as YYYY/MM/image.ext."));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.StudentYear,
            "Student / Year",
            "Save as Student/YYYY/image.ext."));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.StudentYearMonth,
            "Student / Year / Month",
            "Save as Student/YYYY/MM/image.ext."));

        SelectedDownloadLayout = DownloadLayoutOptions.FirstOrDefault(
            option => option.Layout == _settings.DownloadLayout) ?? DownloadLayoutOptions.FirstOrDefault();

        Photos.CollectionChanged += OnPhotosCollectionChanged;
    }

    [ObservableProperty] private AppState _state = AppState.Login;
    [ObservableProperty] private string _statusMessage = "Please log in to Procare.";
    [ObservableProperty] private string _subStatus = "";
    [ObservableProperty] private double _progressValue = 0;
    [ObservableProperty] private bool _isIndeterminate = false;
    [ObservableProperty] private Student? _selectedStudent;
    [ObservableProperty] private bool _allSelected;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private DownloadLayoutOption? _selectedDownloadLayout;

    public ObservableCollection<Student> Students { get; } = [];
    public ObservableCollection<PhotoViewModel> Photos { get; } = [];
    public ObservableCollection<PhotoMonthGroupViewModel> MonthGroups { get; } = [];
    public ObservableCollection<DownloadLayoutOption> DownloadLayoutOptions { get; } = [];

    public int SelectedCount => Photos.Count(photo => photo.IsSelected);
    public int TotalCount => Photos.Count;
    public int DownloadedCount => Photos.Count(photo => photo.IsDownloaded);
    public int UnsavedCount => Photos.Count(photo => !photo.IsDownloaded);
    public int DownloadHistoryCount => _history.Count;
    public string SelectAllButtonText => AllSelected ? "Deselect All" : "Select All";
    public string UnsavedDownloadButtonText => $"⬇ Download Unsaved ({UnsavedCount})";
    public string DownloadLayoutPreview => DownloadService.BuildLayoutPreviewPath(
        "Chosen folder",
        SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
        SelectedStudent?.FullName);

    partial void OnSelectedDownloadLayoutChanged(DownloadLayoutOption? value)
    {
        if (value == null)
        {
            return;
        }

        _settings.DownloadLayout = value.Layout;
        _settingsService.Save(_settings);
        OnPropertyChanged(nameof(DownloadLayoutPreview));
    }

    partial void OnSelectedStudentChanged(Student? value)
    {
        OnPropertyChanged(nameof(DownloadLayoutPreview));
    }

    [RelayCommand]
    public void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    [RelayCommand]
    public void SelectDownloadLayout(DownloadLayoutOption? option)
    {
        if (option != null)
        {
            SelectedDownloadLayout = option;
        }
    }

    public async Task OnTokenCapturedAsync(TokenInfo token)
    {
        _api.SetCredentials(token);
        State = AppState.LoadingStudents;
        StatusMessage = "Loading students...";
        IsIndeterminate = true;
        IsSettingsOpen = false;

        try
        {
            var students = await _api.GetStudentsAsync();
            Students.Clear();
            foreach (var student in students)
            {
                Students.Add(student);
            }

            if (students.Count == 1)
            {
                SelectedStudent = students[0];
                await LoadPhotosAsync();
            }
            else
            {
                State = AppState.SelectStudent;
                StatusMessage = "Select a student to view their photos.";
                SubStatus = "Open Settings if you need to change accounts or save options.";
                AppLog.Info($"Student load completed with {students.Count} students.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Error loading students.", ex);
            StatusMessage = $"Error loading students: {ex.Message}";
            State = AppState.Login;
        }
        finally
        {
            IsIndeterminate = false;
        }
    }

    [RelayCommand]
    public async Task SelectStudentAsync(Student student)
    {
        SelectedStudent = student;
        await LoadPhotosAsync();
    }

    private async Task LoadPhotosAsync()
    {
        if (SelectedStudent == null)
        {
            return;
        }

        State = AppState.LoadingPhotos;
        StatusMessage = $"Loading photos for {SelectedStudent.FullName}...";
        SubStatus = "";
        IsIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "";
        ClearGallery();

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int loaded, int total)>(item =>
            {
                var totalText = item.total > 0 ? item.total.ToString() : "?";
                StatusMessage = $"Loading photos... {item.loaded}/{totalText}";
            });

            var photos = await _api.GetPhotosAsync(SelectedStudent.Id, progress, _cts.Token);

            foreach (var photo in photos.OrderByDescending(item => item.CreatedAt))
            {
                Photos.Add(new PhotoViewModel(photo, _history.IsDownloaded(photo)));
            }

            RebuildMonthGroups();

            State = AppState.Gallery;
            StatusMessage = $"{SelectedStudent.FullName} — {Photos.Count} photos";
            SubStatus = $"{DownloadedCount} already downloaded";
            AppLog.Info($"Photo load completed for {SelectedStudent.FullName} with {Photos.Count} photos.");

            _ = LoadThumbnailsAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Error loading photos for student {SelectedStudent.Id}.", ex);
            StatusMessage = $"Error loading photos: {ex.Message}";
        }
        finally
        {
            IsIndeterminate = false;
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(6);
        var tasks = Photos.Select(async photoViewModel =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                if (string.IsNullOrEmpty(photoViewModel.Photo.ThumbnailUrl))
                {
                    return;
                }

                var bytes = await _api.GetThumbnailBytesAsync(photoViewModel.Photo.ThumbnailUrl, ct);
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    photoViewModel.Thumbnail = bitmap;
                    photoViewModel.IsLoaded = true;
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Thumbnail load failed for photo {photoViewModel.Photo.Id}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    public void ToggleSelectAll()
    {
        var shouldSelect = !AllSelected;
        foreach (var photo in Photos)
        {
            photo.IsSelected = shouldSelect;
        }

        RefreshSelectionState();
    }

    [RelayCommand]
    public void ToggleMonthSelection(PhotoMonthGroupViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        var shouldSelect = !group.AllSelected;
        foreach (var photo in group.Photos)
        {
            photo.IsSelected = shouldSelect;
        }

        RefreshSelectionState();
    }

    public void NotifySelectionChanged()
    {
        RefreshSelectionState();
    }

    public void PrepareForLogin(string message)
    {
        _cts?.Cancel();
        Students.Clear();
        ClearGallery();
        SelectedStudent = null;
        State = AppState.Login;
        IsIndeterminate = false;
        IsSettingsOpen = false;
        StatusMessage = message;
        SubStatus = "";
        ProgressValue = 0;
        ProgressText = "";
    }

    [RelayCommand]
    public async Task DownloadSelectedAsync(string outputFolder)
    {
        var selected = Photos.Where(photo => photo.IsSelected).ToList();
        await DownloadPhotosAsync(selected, outputFolder, "No photos selected.", "selected photos");
    }

    [RelayCommand]
    public async Task DownloadUnsavedAsync(string outputFolder)
    {
        var unsaved = Photos.Where(photo => !photo.IsDownloaded).ToList();
        await DownloadPhotosAsync(unsaved, outputFolder, "No unsaved photos left to download.", "unsaved photos");
    }

    [RelayCommand]
    public void CancelOperation()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    public void BackToStudents()
    {
        ClearGallery();
        SelectedStudent = null;
        State = AppState.SelectStudent;
        StatusMessage = "Select a student.";
        SubStatus = "Open Settings if you need to change accounts or save options.";
        ProgressText = "";
        OnPropertyChanged(nameof(DownloadLayoutPreview));
    }

    public string ImportExistingDownloads(string folder)
    {
        if (Photos.Count == 0)
        {
            return "Load a student's gallery before importing existing downloads.";
        }

        var result = _history.ImportFromFolder(folder, Photos.Select(photo => photo.Photo).ToList());
        foreach (var photo in Photos)
        {
            photo.IsDownloaded = _history.IsDownloaded(photo.Photo);
        }

        RefreshSelectionState();

        if (result.ScannedFiles == 0)
        {
            return "No files were found in the selected folder.";
        }

        return $"Imported {result.Imported} existing downloads from {result.MatchedFiles} matched files.";
    }

    private void ClearGallery()
    {
        Photos.Clear();
        foreach (var group in MonthGroups)
        {
            group.Photos.CollectionChanged -= OnMonthGroupPhotosChanged;
        }

        MonthGroups.Clear();
        RefreshSelectionState();
    }

    private void RebuildMonthGroups()
    {
        foreach (var group in MonthGroups)
        {
            group.Photos.CollectionChanged -= OnMonthGroupPhotosChanged;
        }

        MonthGroups.Clear();

        var groups = Photos
            .GroupBy(photo => GetMonthGroupKey(photo.Photo.CreatedAt))
            .OrderByDescending(group => group.Key.SortDate)
            .ThenByDescending(group => group.Key.Label)
            .Select((group, index) => new PhotoMonthGroupViewModel(
                group.Key.Label,
                group.Select(photo => photo).ToList(),
                isExpanded: index == 0));

        foreach (var group in groups)
        {
            group.Photos.CollectionChanged += OnMonthGroupPhotosChanged;
            MonthGroups.Add(group);
        }

        RefreshSelectionState();
    }

    private void OnPhotosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectionState();
    }

    private void OnMonthGroupPhotosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        var shouldSelectAll = Photos.Count > 0 && Photos.All(photo => photo.IsSelected);
        if (AllSelected != shouldSelectAll)
        {
            AllSelected = shouldSelectAll;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(UnsavedCount));
        OnPropertyChanged(nameof(DownloadHistoryCount));
        OnPropertyChanged(nameof(SelectAllButtonText));
        OnPropertyChanged(nameof(UnsavedDownloadButtonText));

        foreach (var group in MonthGroups)
        {
            group.RefreshSelectionState();
        }
    }

    private static (DateTime SortDate, string Label) GetMonthGroupKey(DateTime createdAt)
    {
        if (createdAt == default)
        {
            return (DateTime.MinValue, "Unknown Date");
        }

        var bucket = new DateTime(createdAt.Year, createdAt.Month, 1);
        return (bucket, bucket.ToString("MMMM yyyy"));
    }

    private async Task DownloadPhotosAsync(
        List<PhotoViewModel> photosToDownload,
        string outputFolder,
        string emptyMessage,
        string description)
    {
        if (photosToDownload.Count == 0)
        {
            StatusMessage = emptyMessage;
            SubStatus = "";
            return;
        }

        State = AppState.Downloading;
        ProgressValue = 0;
        ProgressText = "Preparing download...";
        StatusMessage = $"Preparing {description}...";
        SubStatus = "";
        _cts = new CancellationTokenSource();

        var progress = new Progress<(int done, int total, string file)>(item =>
        {
            ProgressValue = item.total == 0 ? 0 : (double)item.done / item.total * 100;
            ProgressText = $"Downloading {item.done}/{item.total} ({ProgressValue:0}%)";
            StatusMessage = ProgressText;
            SubStatus = item.file;
        });

        try
        {
            var result = await _downloader.DownloadAsync(
                photosToDownload.Select(photo => photo.Photo).ToList(),
                outputFolder,
                SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
                SelectedStudent?.FullName,
                progress,
                _cts.Token);

            foreach (var photo in photosToDownload)
            {
                if (_history.IsDownloaded(photo.Photo))
                {
                    photo.IsDownloaded = true;
                }
            }

            RefreshSelectionState();
            State = AppState.Gallery;
            StatusMessage = $"Done! {result.Succeeded} downloaded, {result.Skipped} skipped, {result.Failed} failed.";
            SubStatus = result.Failed > 0
                ? string.Join("; ", result.Errors.Take(3))
                : $"Saved to: {result.OutputSummary}";
            ProgressText = "";
            AppLog.Info(
                $"Download completed for {description}. Success: {result.Succeeded}, Skipped: {result.Skipped}, Failed: {result.Failed}, Output: {result.OutputSummary}");
        }
        catch (OperationCanceledException)
        {
            State = AppState.Gallery;
            StatusMessage = "Download cancelled.";
            SubStatus = "";
            ProgressText = "";
            AppLog.Warn($"Download cancelled while processing {description}.");
        }
    }
}

public partial class PhotoViewModel : ObservableObject
{
    public PhotoViewModel(Photo photo, bool isDownloaded)
    {
        Photo = photo;
        _isDownloaded = isDownloaded;
    }

    public Photo Photo { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private BitmapImage? _thumbnail;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isDownloaded;

    public string DateLabel => Photo.CreatedAt != default
        ? Photo.CreatedAt.ToString("MMM d, yyyy")
        : "";
}

public partial class PhotoMonthGroupViewModel : ObservableObject
{
    public PhotoMonthGroupViewModel(string label, List<PhotoViewModel> photos, bool isExpanded)
    {
        Label = label;
        Photos = new ObservableCollection<PhotoViewModel>(photos);
        _isExpanded = isExpanded;
    }

    public string Label { get; }
    public ObservableCollection<PhotoViewModel> Photos { get; }

    [ObservableProperty] private bool _isExpanded;

    public int SelectedCount => Photos.Count(photo => photo.IsSelected);
    public int TotalCount => Photos.Count;
    public int DownloadedCount => Photos.Count(photo => photo.IsDownloaded);
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;
    public string ToggleLabel => AllSelected ? "Clear Month" : "Select Month";

    public void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ToggleLabel));
    }
}

public sealed record DownloadLayoutOption(DownloadLayout Layout, string Label, string Description)
{
    public override string ToString() => Label;
}
