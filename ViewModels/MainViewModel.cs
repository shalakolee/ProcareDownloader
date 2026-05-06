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
    private readonly DesktopImageCacheService _imageCache;
    private readonly DesktopPhotoMetadataCacheService _photoCache;
    private readonly DownloadHistoryService _history;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainViewModel(
        ProcareApiService api,
        DownloadService downloader,
        DesktopImageCacheService imageCache,
        DesktopPhotoMetadataCacheService photoCache,
        DownloadHistoryService history,
        SettingsService settingsService)
    {
        _api = api;
        _downloader = downloader;
        _imageCache = imageCache;
        _photoCache = photoCache;
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
    [ObservableProperty] private string _lastSavedFolderPath = "";
    [ObservableProperty] private bool _isPhotoViewerOpen;
    [ObservableProperty] private bool _isPhotoViewerLoading;
    [ObservableProperty] private BitmapImage? _photoViewerImage;
    [ObservableProperty] private string _photoViewerTitle = "";
    [ObservableProperty] private string _photoViewerSubtitle = "";
    [ObservableProperty] private PhotoViewModel? _activeViewerPhoto;
    [ObservableProperty] private string _cacheUsageText = "Cache: checking...";

    public ObservableCollection<Student> Students { get; } = [];
    public ObservableCollection<PhotoViewModel> Photos { get; } = [];
    public ObservableCollection<PhotoMonthGroupViewModel> MonthGroups { get; } = [];
    public ObservableCollection<DesktopGalleryRowViewModel> GalleryRows { get; } = [];
    public ObservableCollection<DownloadLayoutOption> DownloadLayoutOptions { get; } = [];

    public int SelectedCount => Photos.Count(photo => photo.IsSelected);
    public int TotalCount => Photos.Count;
    public int DownloadedCount => Photos.Count(photo => photo.IsDownloaded);
    public int UnsavedCount => Photos.Count(photo => !photo.IsDownloaded);
    public int DownloadHistoryCount => _history.Count;
    public bool HasLastSavedFolder => !string.IsNullOrWhiteSpace(LastSavedFolderPath) && Directory.Exists(LastSavedFolderPath);
    public string SelectAllButtonText => AllSelected ? "Deselect All" : "Select All";
    public string UnsavedDownloadButtonText => $"⬇ Download Unsaved ({UnsavedCount})";
    public string DownloadLayoutPreview => DownloadService.BuildLayoutPreviewPath(
        "Chosen folder",
        SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
        SelectedStudent?.FullName);
    public bool CanShowPreviousPhoto => ActiveViewerPhoto != null && Photos.IndexOf(ActiveViewerPhoto) > 0;
    public bool CanShowNextPhoto => ActiveViewerPhoto != null && Photos.IndexOf(ActiveViewerPhoto) >= 0 && Photos.IndexOf(ActiveViewerPhoto) < Photos.Count - 1;

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

    partial void OnLastSavedFolderPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasLastSavedFolder));
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
    public async Task ClearCacheAsync()
    {
        await _imageCache.ClearAsync();
        foreach (var photo in Photos)
        {
            photo.Thumbnail = null;
            photo.IsLoaded = false;
            photo.FullImage = null;
        }

        PhotoViewerImage = ActiveViewerPhoto?.Thumbnail;
        RefreshCacheUsage();
        _ = LoadThumbnailsAsync(_cts?.Token ?? CancellationToken.None);
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
        var token = _cts.Token;
        var studentId = SelectedStudent.Id;
        var studentName = SelectedStudent.FullName;

        try
        {
            var cached = await _photoCache.TryLoadAsync(studentId, token);
            if (cached.HasCache)
            {
                ApplyPhotosToGallery(cached.Photos);

                var ageMinutes = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - cached.SavedAtUtc).TotalMinutes));
                State = AppState.Gallery;
                StatusMessage = $"{studentName} — {Photos.Count} photos";
                SubStatus = cached.IsFresh
                    ? $"{DownloadedCount} already downloaded • loaded from cache"
                    : $"{DownloadedCount} already downloaded • cache is {ageMinutes} minutes old, refreshing...";
                IsIndeterminate = false;
                AppLog.Info(
                    cached.IsFresh
                        ? $"Loaded cached photos for {studentName}. Count: {Photos.Count}."
                        : $"Loaded stale cached photos for {studentName}. Count: {Photos.Count}. Starting refresh.");
                _ = LoadThumbnailsAsync(token);

                if (cached.IsFresh)
                {
                    return;
                }
            }

            IsIndeterminate = true;
            var progress = new Progress<(int loaded, int total)>(item =>
            {
                var totalText = item.total > 0 ? item.total.ToString() : "?";
                StatusMessage = $"Loading photos... {item.loaded}/{totalText}";
            });

            var photos = await _api.GetPhotosAsync(studentId, progress, token);
            await _photoCache.SaveAsync(studentId, photos, token);

            if (!cached.HasCache || !DesktopPhotoMetadataCacheService.AreEquivalent(cached.Photos, photos))
            {
                ApplyPhotosToGallery(photos);
            }

            State = AppState.Gallery;
            StatusMessage = $"{studentName} — {Photos.Count} photos";
            SubStatus = $"{DownloadedCount} already downloaded";
            AppLog.Info($"Photo load completed for {studentName} with {Photos.Count} photos.");

            _ = LoadThumbnailsAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (MonthGroups.Count > 0)
            {
                State = AppState.Gallery;
                StatusMessage = $"{studentName} — {Photos.Count} photos";
                SubStatus = $"Using cached photos. Refresh failed: {ex.Message}";
                AppLog.Error($"Photo refresh failed for student {studentId}; cached data kept.", ex);
                return;
            }

            AppLog.Error($"Error loading photos for student {studentId}.", ex);
            State = AppState.SelectStudent;
            StatusMessage = $"Error loading photos: {ex.Message}";
            SubStatus = "Select the student again to retry.";
        }
        finally
        {
            IsIndeterminate = false;
        }
    }

    private void ApplyPhotosToGallery(IReadOnlyCollection<Photo> photos)
    {
        ClearGallery();

        foreach (var photo in photos.OrderByDescending(item => item.CreatedAt))
        {
            var viewModel = new PhotoViewModel(photo, _history.IsDownloaded(photo));
            var cachedThumbnail = _imageCache.GetThumbnail(photo);
            if (cachedThumbnail != null)
            {
                viewModel.Thumbnail = cachedThumbnail;
                viewModel.IsLoaded = true;
            }

            viewModel.FullImage = _imageCache.GetFullImage(photo);

            Photos.Add(viewModel);
        }

        RebuildMonthGroups();
    }

    private async Task LoadThumbnailsAsync(CancellationToken ct)
    {
        var tasks = Photos.Select(async photoViewModel =>
        {
            try
            {
                if (ct.IsCancellationRequested || photoViewModel.IsLoaded)
                {
                    return;
                }

                var bitmap = await _imageCache.EnsureThumbnailCachedAsync(photoViewModel.Photo, ct);
                if (bitmap == null)
                {
                    return;
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    photoViewModel.Thumbnail = bitmap;
                    photoViewModel.IsLoaded = true;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Thumbnail load failed for photo {photoViewModel.Photo.Id}: {ex.Message}");
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

    public async Task OpenPhotoViewerAsync(PhotoViewModel? photo)
    {
        if (photo == null)
        {
            return;
        }

        ActiveViewerPhoto = photo;
        PhotoViewerTitle = string.IsNullOrWhiteSpace(photo.Photo.Caption) ? "Photo" : photo.Photo.Caption!;
        PhotoViewerSubtitle = photo.DateLabel;
        PhotoViewerImage = photo.FullImage ?? photo.Thumbnail;
        IsPhotoViewerOpen = true;
        IsPhotoViewerLoading = photo.FullImage == null;
        OnPropertyChanged(nameof(CanShowPreviousPhoto));
        OnPropertyChanged(nameof(CanShowNextPhoto));

        if (photo.FullImage != null)
        {
            return;
        }

        try
        {
            var bitmap = await _imageCache.EnsureFullImageCachedAsync(photo.Photo, _cts?.Token ?? CancellationToken.None);
            if (bitmap == null)
            {
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                photo.FullImage = bitmap;
                if (ActiveViewerPhoto == photo)
                {
                    PhotoViewerImage = bitmap;
                }

                IsPhotoViewerLoading = false;
                RefreshCacheUsage();
            });
        }
        catch (OperationCanceledException)
        {
            IsPhotoViewerLoading = false;
        }
        catch (Exception ex)
        {
            IsPhotoViewerLoading = false;
            SubStatus = $"Could not open full-size photo: {ex.Message}";
            AppLog.Error($"Desktop full-size photo load failed for {photo.Photo.Id}.", ex);
        }
    }

    [RelayCommand]
    public void ClosePhotoViewer()
    {
        IsPhotoViewerOpen = false;
        IsPhotoViewerLoading = false;
        ActiveViewerPhoto = null;
        PhotoViewerImage = null;
        PhotoViewerTitle = "";
        PhotoViewerSubtitle = "";
        OnPropertyChanged(nameof(CanShowPreviousPhoto));
        OnPropertyChanged(nameof(CanShowNextPhoto));
    }

    [RelayCommand]
    public async Task ShowPreviousPhotoAsync()
    {
        await MovePhotoViewerAsync(-1);
    }

    [RelayCommand]
    public async Task ShowNextPhotoAsync()
    {
        await MovePhotoViewerAsync(1);
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
        LastSavedFolderPath = "";
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
        GalleryRows.Clear();
        ClosePhotoViewer();
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

        RebuildGalleryRows();
        RefreshCacheUsage();
        RefreshSelectionState();
    }

    private void RebuildGalleryRows()
    {
        GalleryRows.Clear();

        foreach (var group in MonthGroups)
        {
            GalleryRows.Add(DesktopGalleryRowViewModel.CreateHeader(group, RebuildGalleryRows, RefreshSelectionState));
            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (var rowPhotos in group.Photos.Chunk(5))
            {
                GalleryRows.Add(DesktopGalleryRowViewModel.CreatePhotoRow(group, rowPhotos));
            }
        }
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

        foreach (var row in GalleryRows)
        {
            row.RefreshCounts();
        }
    }

    private async Task MovePhotoViewerAsync(int delta)
    {
        if (ActiveViewerPhoto == null)
        {
            return;
        }

        var current = Photos.IndexOf(ActiveViewerPhoto);
        var next = current + delta;
        if (current < 0 || next < 0 || next >= Photos.Count)
        {
            return;
        }

        await OpenPhotoViewerAsync(Photos[next]);
    }

    private void RefreshCacheUsage()
    {
        CacheUsageText = $"Cache: {_imageCache.GetUsage().DisplayText}";
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
            LastSavedFolderPath = ResolveLastSavedFolderPath(outputFolder, result.OutputSummary);
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

    private static string ResolveLastSavedFolderPath(string rootFolder, string outputSummary)
    {
        if (!string.IsNullOrWhiteSpace(outputSummary))
        {
            var multiFolderMarker = outputSummary.IndexOf(" (+", StringComparison.Ordinal);
            var candidate = multiFolderMarker > 0
                ? outputSummary[..multiFolderMarker]
                : outputSummary;

            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.Exists(rootFolder) ? rootFolder : "";
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
    [ObservableProperty] private BitmapImage? _fullImage;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isDownloaded;

    public string DateLabel => Photo.CreatedAt != default
        ? Photo.CreatedAt.ToString("MMM d, yyyy")
        : "";
}

public sealed partial class DesktopGalleryRowViewModel : ObservableObject
{
    private readonly PhotoMonthGroupViewModel? _group;
    private readonly Action? _rebuildRows;
    private readonly Action? _refreshCounts;

    private DesktopGalleryRowViewModel(
        bool isHeader,
        PhotoMonthGroupViewModel group,
        IReadOnlyCollection<PhotoViewModel> photos,
        Action? rebuildRows,
        Action? refreshCounts)
    {
        IsHeader = isHeader;
        IsPhotoRow = !isHeader;
        _group = group;
        _rebuildRows = rebuildRows;
        _refreshCounts = refreshCounts;
        Label = group.Label;
        Photos = new ObservableCollection<PhotoViewModel>(photos);
    }

    public bool IsHeader { get; }
    public bool IsPhotoRow { get; }
    public string Label { get; }
    public ObservableCollection<PhotoViewModel> Photos { get; }
    public int SelectedCount => _group?.SelectedCount ?? 0;
    public int TotalCount => _group?.TotalCount ?? 0;
    public int DownloadedCount => _group?.DownloadedCount ?? 0;
    public string ToggleLabel => _group?.ToggleLabel ?? "";
    public string ChevronGlyph => _group?.IsExpanded == true ? "▾" : "▸";

    public static DesktopGalleryRowViewModel CreateHeader(
        PhotoMonthGroupViewModel group,
        Action rebuildRows,
        Action refreshCounts)
    {
        return new DesktopGalleryRowViewModel(true, group, [], rebuildRows, refreshCounts);
    }

    public static DesktopGalleryRowViewModel CreatePhotoRow(
        PhotoMonthGroupViewModel group,
        IReadOnlyCollection<PhotoViewModel> photos)
    {
        return new DesktopGalleryRowViewModel(false, group, photos, null, null);
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        if (_group == null)
        {
            return;
        }

        _group.IsExpanded = !_group.IsExpanded;
        _rebuildRows?.Invoke();
        _refreshCounts?.Invoke();
    }

    [RelayCommand]
    public void ToggleMonthSelection()
    {
        if (_group == null)
        {
            return;
        }

        var target = !_group.AllSelected;
        foreach (var photo in _group.Photos)
        {
            photo.IsSelected = target;
        }

        _group.RefreshSelectionState();
        _refreshCounts?.Invoke();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ChevronGlyph));
    }
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
