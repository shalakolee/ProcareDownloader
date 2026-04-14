using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using ProcareDownloader.Models;
using ProcareDownloader.Mobile.Services;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile.ViewModels;

public enum MobileAppState
{
    Login,
    LoadingStudents,
    SelectStudent,
    LoadingPhotos,
    Gallery,
    Downloading
}

public partial class MainPageViewModel : ObservableObject
{
    private readonly MobileProcareApiService _api;
    private readonly MobileDownloadService _downloader;
    private readonly MobileImageCacheService _imageCache;
    private readonly MobilePhotoMetadataCacheService _photoCache;
    private readonly DownloadHistoryService _history;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainPageViewModel(
        MobileProcareApiService api,
        MobileDownloadService downloader,
        MobileImageCacheService imageCache,
        MobilePhotoMetadataCacheService photoCache,
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

        DownloadLayoutOptions.Add(new DownloadLayoutOption(DownloadLayout.Flat, "Single Folder"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(DownloadLayout.YearMonth, "Year / Month"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(DownloadLayout.StudentYear, "Student / Year"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(DownloadLayout.StudentYearMonth, "Student / Year / Month"));

        SelectedDownloadLayout = DownloadLayoutOptions.FirstOrDefault(option => option.Layout == _settings.DownloadLayout)
                                 ?? DownloadLayoutOptions.First();
    }

    public event EventHandler? ReloadLoginRequested;

    [ObservableProperty] private MobileAppState _state = MobileAppState.Login;
    [ObservableProperty] private string _statusMessage = "Log in to Procare in the embedded browser.";
    [ObservableProperty] private string _subStatus = "After login, the app will continue automatically.";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private Student? _selectedStudent;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private DownloadLayoutOption? _selectedDownloadLayout;
    [ObservableProperty] private string _loginUrl = "https://schools.procareconnect.com/login";
    [ObservableProperty] private bool _isPhotoViewerOpen;
    [ObservableProperty] private bool _isPhotoViewerLoading;
    [ObservableProperty] private ImageSource? _photoViewerSource;
    [ObservableProperty] private string _photoViewerTitle = "";
    [ObservableProperty] private string _photoViewerSubtitle = "";
    [ObservableProperty] private MobilePhotoItemViewModel? _activeViewerPhoto;
    [ObservableProperty] private bool _isThumbnailPrefetching;

    public ObservableCollection<Student> Students { get; } = [];
    public ObservableCollection<MobilePhotoMonthGroupViewModel> MonthGroups { get; } = [];
    public ObservableCollection<DownloadLayoutOption> DownloadLayoutOptions { get; } = [];

    public bool IsLoginVisible => State == MobileAppState.Login;
    public bool IsLoadingVisible => State == MobileAppState.LoadingStudents || State == MobileAppState.LoadingPhotos;
    public bool IsStudentSelectionVisible => State == MobileAppState.SelectStudent;
    public bool IsGalleryVisible => State == MobileAppState.Gallery || State == MobileAppState.Downloading;
    public bool IsDownloading => State == MobileAppState.Downloading;
    public bool IsThumbnailPrefetchOverlayVisible => IsGalleryVisible && IsThumbnailPrefetching;

    public int TotalCount => MonthGroups.SelectMany(group => group.Photos).Count();
    public int SelectedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => photo.IsSelected);
    public int DownloadedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => photo.IsDownloaded);
    public int UnsavedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => !photo.IsDownloaded);
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;
    public string SelectAllButtonText => AllSelected ? "Deselect All" : "Select All";
    public string UnsavedDownloadButtonText => $"Download Unsaved ({UnsavedCount})";
    public string DownloadRootPath => _downloader.DownloadRootPath;
    public string DownloadLayoutPreview => MobileDownloadPathHelper.BuildLayoutPreviewPath(
        SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
        SelectedStudent?.FullName);

    partial void OnStateChanged(MobileAppState value)
    {
        OnPropertyChanged(nameof(IsLoginVisible));
        OnPropertyChanged(nameof(IsLoadingVisible));
        OnPropertyChanged(nameof(IsStudentSelectionVisible));
        OnPropertyChanged(nameof(IsGalleryVisible));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsThumbnailPrefetchOverlayVisible));
    }

    partial void OnSelectedStudentChanged(Student? value)
    {
        OnPropertyChanged(nameof(DownloadLayoutPreview));
    }

    partial void OnIsThumbnailPrefetchingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThumbnailPrefetchOverlayVisible));
    }

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
    public async Task OpenPhotoViewerAsync(MobilePhotoItemViewModel? photo)
    {
        if (photo == null)
        {
            return;
        }

        ActiveViewerPhoto = photo;
        PhotoViewerTitle = photo.Title;
        PhotoViewerSubtitle = photo.DateLabel;
        PhotoViewerSource = photo.FullImageSource ?? photo.ThumbnailSource;
        IsPhotoViewerOpen = true;
        IsPhotoViewerLoading = photo.FullImageSource == null;

        if (photo.FullImageSource != null)
        {
            return;
        }

        try
        {
            var path = await _imageCache.EnsureFullImageCachedAsync(photo.Photo, _cts?.Token ?? CancellationToken.None);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var source = ImageSource.FromFile(path);
                photo.FullImageSource = source;
                if (ActiveViewerPhoto == photo)
                {
                    PhotoViewerSource = source;
                }

                IsPhotoViewerLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            if (ActiveViewerPhoto == photo)
            {
                IsPhotoViewerLoading = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to load full-size image for photo {photo.Photo.Id}.", ex);
            if (ActiveViewerPhoto == photo)
            {
                IsPhotoViewerLoading = false;
                SubStatus = $"Could not open full-size photo: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void ClosePhotoViewer()
    {
        ActiveViewerPhoto = null;
        IsPhotoViewerOpen = false;
        IsPhotoViewerLoading = false;
        PhotoViewerSource = null;
        PhotoViewerTitle = "";
        PhotoViewerSubtitle = "";
    }

    [RelayCommand]
    public void ToggleSelectAll()
    {
        var target = !AllSelected;
        foreach (var photo in MonthGroups.SelectMany(group => group.Photos))
        {
            photo.IsSelected = target;
        }

        RefreshCounts();
    }

    [RelayCommand]
    public void BackToStudents()
    {
        MonthGroups.Clear();
        SelectedStudent = null;
        ClosePhotoViewer();
        IsThumbnailPrefetching = false;
        State = MobileAppState.SelectStudent;
        StatusMessage = "Select a student.";
        SubStatus = "Your session is still active.";
        ProgressText = "";
        ProgressValue = 0;
        RefreshCounts();
    }

    [RelayCommand]
    public async Task SelectStudentAsync(Student student)
    {
        SelectedStudent = student;
        await LoadPhotosAsync();
    }

    [RelayCommand]
    public async Task DownloadSelectedAsync()
    {
        var selected = MonthGroups.SelectMany(group => group.Photos).Where(photo => photo.IsSelected).ToList();
        await DownloadPhotosAsync(selected, "No photos selected.");
    }

    [RelayCommand]
    public async Task DownloadUnsavedAsync()
    {
        var unsaved = MonthGroups.SelectMany(group => group.Photos).Where(photo => !photo.IsDownloaded).ToList();
        await DownloadPhotosAsync(unsaved, "No unsaved photos left to download.");
    }

    [RelayCommand]
    public void SignOut()
    {
        _cts?.Cancel();
        _api.ClearCredentials();
        Students.Clear();
        MonthGroups.Clear();
        IsThumbnailPrefetching = false;
        SelectedStudent = null;
        ClosePhotoViewer();
        IsSettingsOpen = false;
        State = MobileAppState.Login;
        StatusMessage = "Session cleared in the app. Log in again to continue.";
        SubStatus = "If Procare auto-signs you back in, sign out inside the web page and then log in with the other account.";
        ProgressText = "";
        ProgressValue = 0;
        LoginUrl = $"https://schools.procareconnect.com/login?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        RefreshCounts();
        ReloadLoginRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task OnTokenCapturedAsync(TokenInfo token)
    {
        _api.SetCredentials(token);
        IsSettingsOpen = false;
        State = MobileAppState.LoadingStudents;
        StatusMessage = "Loading students...";
        SubStatus = "";

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
                return;
            }

            State = MobileAppState.SelectStudent;
            StatusMessage = "Select a student.";
            SubStatus = "Choose whose photos to browse.";
        }
        catch (Exception ex)
        {
            State = MobileAppState.Login;
            StatusMessage = $"Could not load students: {ex.Message}";
            SubStatus = "Stay on the Procare page. The app will retry automatically.";
            AppLog.Error("Mobile student load failed.", ex);
        }
    }

    public async Task<bool> TryContinueFromBrowserSessionAsync()
    {
        if (State != MobileAppState.Login)
        {
            return false;
        }

        try
        {
            AppLog.Info("Attempting to continue mobile app from browser session.");
            var students = await _api.GetStudentsAsync();
            if (students.Count == 0)
            {
                AppLog.Info("Browser-session continuation found no students yet.");
                return false;
            }

            IsSettingsOpen = false;
            State = MobileAppState.LoadingStudents;
            StatusMessage = "Loading students...";
            SubStatus = "";

            Students.Clear();
            foreach (var student in students)
            {
                Students.Add(student);
            }

            if (students.Count == 1)
            {
                SelectedStudent = students[0];
                await LoadPhotosAsync();
                return true;
            }

            State = MobileAppState.SelectStudent;
            StatusMessage = "Select a student.";
            SubStatus = "Choose whose photos to browse.";
            AppLog.Info($"Browser-session continuation succeeded with {students.Count} students.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Mobile browser-session student load failed.", ex);
            return false;
        }
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(UnsavedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        OnPropertyChanged(nameof(UnsavedDownloadButtonText));
        OnPropertyChanged(nameof(DownloadLayoutPreview));

        foreach (var group in MonthGroups)
        {
            group.RefreshCounts();
        }
    }

    private async Task LoadPhotosAsync()
    {
        if (SelectedStudent == null)
        {
            return;
        }

        State = MobileAppState.LoadingPhotos;
        StatusMessage = $"Loading photos for {SelectedStudent.FullName}...";
        SubStatus = "";
        ProgressValue = 0;
        ProgressText = "";
        IsThumbnailPrefetching = false;
        MonthGroups.Clear();

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
                State = MobileAppState.Gallery;
                StatusMessage = $"{studentName} — {TotalCount} photos";
                SubStatus = cached.IsFresh
                    ? $"{DownloadedCount} already downloaded • loaded from cache"
                    : $"{DownloadedCount} already downloaded • cache is {ageMinutes} minutes old, refreshing...";
                RefreshCounts();

                if (cached.IsFresh)
                {
                    await PrefetchAllMonthsThenCollapseAsync(token);
                    return;
                }
            }

            var progress = new Progress<(int loaded, int total)>(item =>
            {
                var totalText = item.total > 0 ? item.total.ToString() : "?";
                StatusMessage = $"Loading photos... {item.loaded}/{totalText}";
            });

            var photos = await _api.GetPhotosAsync(studentId, progress, token);
            await _photoCache.SaveAsync(studentId, photos, token);

            if (!cached.HasCache || !MobilePhotoMetadataCacheService.AreEquivalent(cached.Photos, photos))
            {
                ApplyPhotosToGallery(photos);
            }

            State = MobileAppState.Gallery;
            StatusMessage = $"{studentName} — {TotalCount} photos";
            SubStatus = $"{DownloadedCount} already downloaded";
            RefreshCounts();
            await PrefetchAllMonthsThenCollapseAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (MonthGroups.Count > 0)
            {
                State = MobileAppState.Gallery;
                StatusMessage = $"{studentName} — {TotalCount} photos";
                SubStatus = $"Using cached photos. Refresh failed: {ex.Message}";
                AppLog.Error($"Mobile photo refresh failed for student {studentId}; cache kept.", ex);
                await PrefetchAllMonthsThenCollapseAsync(token);
                return;
            }

            State = MobileAppState.SelectStudent;
            StatusMessage = $"Could not load photos: {ex.Message}";
            SubStatus = "Select the student again to retry.";
            AppLog.Error($"Mobile photo load failed for student {studentId}.", ex);
        }
        finally
        {
            IsThumbnailPrefetching = false;
        }
    }

    private void ApplyPhotosToGallery(IReadOnlyCollection<Photo> photos)
    {
        MonthGroups.Clear();

        var groups = photos
            .OrderByDescending(photo => photo.CreatedAt)
            .GroupBy(photo => GetMonthKey(photo.CreatedAt))
            .OrderByDescending(group => group.Key.SortDate)
            .Select(group => new MobilePhotoMonthGroupViewModel(
                group.Key.Label,
                group.Select(photo => new MobilePhotoItemViewModel(
                    photo,
                    _history.IsDownloaded(photo),
                    _imageCache.GetThumbnailSource(photo),
                    _imageCache.GetFullImageSource(photo),
                    RefreshCounts)).ToList(),
                RefreshCounts,
                isExpanded: true,
                WarmVisibleThumbnailsAsync));

        foreach (var group in groups)
        {
            MonthGroups.Add(group);
        }
    }

    private async Task DownloadPhotosAsync(List<MobilePhotoItemViewModel> photos, string emptyMessage)
    {
        if (photos.Count == 0)
        {
            StatusMessage = emptyMessage;
            SubStatus = "";
            return;
        }

        State = MobileAppState.Downloading;
        ProgressValue = 0;
        ProgressText = "Preparing download...";
        StatusMessage = ProgressText;
        SubStatus = "";
        _cts = new CancellationTokenSource();

        var progress = new Progress<(int done, int total, string file)>(item =>
        {
            ProgressValue = item.total == 0 ? 0 : (double)item.done / item.total;
            ProgressText = $"Downloading {item.done}/{item.total}";
            StatusMessage = ProgressText;
            SubStatus = item.file;
        });

        try
        {
            var result = await _downloader.DownloadAsync(
                photos.Select(photo => photo.Photo),
                SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
                SelectedStudent?.FullName,
                progress,
                _cts.Token);

            foreach (var photo in photos)
            {
                if (_history.IsDownloaded(photo.Photo))
                {
                    photo.IsDownloaded = true;
                }
            }

            State = MobileAppState.Gallery;
            StatusMessage = $"Done! {result.Succeeded} downloaded, {result.Skipped} skipped, {result.Failed} failed.";
            SubStatus = result.Failed > 0
                ? string.Join("; ", result.Errors.Take(3))
                : $"Saved to: {result.OutputSummary}";
            ProgressText = "";
            RefreshCounts();
        }
        catch (OperationCanceledException)
        {
            State = MobileAppState.Gallery;
            StatusMessage = "Download cancelled.";
            SubStatus = "";
            ProgressText = "";
        }
        catch (Exception ex)
        {
            State = MobileAppState.Gallery;
            StatusMessage = $"Download failed: {ex.Message}";
            ProgressText = "";
            AppLog.Error("Mobile download failed.", ex);
        }
    }

    private async Task WarmVisibleThumbnailsAsync(IReadOnlyCollection<MobilePhotoItemViewModel> photos)
    {
        if (photos.Count == 0)
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        using var semaphore = new SemaphoreSlim(4);
        var tasks = photos.Select(async photo =>
        {
            if (token.IsCancellationRequested || photo.ThumbnailSource != null)
            {
                return;
            }

            await semaphore.WaitAsync(token);
            try
            {
                var path = await _imageCache.EnsureThumbnailCachedAsync(photo.Photo, token);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    photo.ThumbnailSource = ImageSource.FromFile(path);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to warm thumbnail cache for photo {photo.Photo.Id}. {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PrefetchAllMonthsThenCollapseAsync(CancellationToken token)
    {
        if (MonthGroups.Count == 0 || token.IsCancellationRequested)
        {
            return;
        }

        IsThumbnailPrefetching = true;
        var originalSubStatus = SubStatus;
        SubStatus = "Rendering all thumbnails...";

        try
        {
            foreach (var group in MonthGroups)
            {
                group.IsExpanded = true;
            }

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await Task.Yield();
            });

            foreach (var group in MonthGroups)
            {
                token.ThrowIfCancellationRequested();
                await group.EnsureMaterializedAsync();
            }

            var allPhotos = MonthGroups
                .SelectMany(group => group.VisiblePhotos)
                .ToList();

            await WarmVisibleThumbnailsAsync(allPhotos);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var group in MonthGroups)
            {
                group.IsExpanded = false;
            }

            SubStatus = originalSubStatus;
            IsThumbnailPrefetching = false;
        }
    }

    private static (DateTime SortDate, string Label) GetMonthKey(DateTime createdAt)
    {
        if (createdAt == default)
        {
            return (DateTime.MinValue, "Unknown Date");
        }

        var bucket = new DateTime(createdAt.Year, createdAt.Month, 1);
        return (bucket, bucket.ToString("MMMM yyyy"));
    }
}

public partial class MobilePhotoItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public MobilePhotoItemViewModel(
        Photo photo,
        bool isDownloaded,
        ImageSource? thumbnailSource,
        ImageSource? fullImageSource,
        Action onChanged)
    {
        Photo = photo;
        _isDownloaded = isDownloaded;
        _thumbnailSource = thumbnailSource;
        _fullImageSource = fullImageSource;
        _onChanged = onChanged;
    }

    public Photo Photo { get; }
    public string Title => string.IsNullOrWhiteSpace(Photo.Caption) ? "Photo" : Photo.Caption!;
    public string DateLabel => Photo.CreatedAt == default ? "Unknown date" : Photo.CreatedAt.ToString("MMM d, yyyy");

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private ImageSource? _thumbnailSource;
    [ObservableProperty] private ImageSource? _fullImageSource;

    partial void OnIsSelectedChanged(bool value)
    {
        _onChanged();
    }

    partial void OnIsDownloadedChanged(bool value)
    {
        _onChanged();
    }

    [RelayCommand]
    public void ToggleSelection()
    {
        IsSelected = !IsSelected;
    }
}

public partial class MobilePhotoMonthGroupViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyCollection<MobilePhotoItemViewModel>, Task>? _onExpandedAsync;
    private const int InitialMaterializeCount = 24;
    private const int MaterializeBatchSize = 12;
    private CancellationTokenSource? _materializeCts;
    private Task? _materializeTask;
    private bool _isMaterialized;
    private bool _isMaterializing;

    public MobilePhotoMonthGroupViewModel(
        string label,
        List<MobilePhotoItemViewModel> photos,
        Action onChanged,
        bool isExpanded,
        Func<IReadOnlyCollection<MobilePhotoItemViewModel>, Task>? onExpandedAsync)
    {
        Label = label;
        Photos = new ObservableCollection<MobilePhotoItemViewModel>(photos);
        VisiblePhotos = [];
        _onChanged = onChanged;
        _onExpandedAsync = onExpandedAsync;
        _isExpanded = isExpanded;
        if (_isExpanded)
        {
            EnsureVisiblePhotosMaterialized();
        }
    }

    public string Label { get; }
    public ObservableCollection<MobilePhotoItemViewModel> Photos { get; }
    public ObservableCollection<MobilePhotoItemViewModel> VisiblePhotos { get; }

    [ObservableProperty] private bool _isExpanded = true;

    public int TotalCount => Photos.Count;
    public int SelectedCount => Photos.Count(photo => photo.IsSelected);
    public int DownloadedCount => Photos.Count(photo => photo.IsDownloaded);
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;
    public string ToggleExpandedText => IsExpanded ? "Collapse" : "Expand";
    public string ChevronGlyph => IsExpanded ? "▾" : "▸";
    public string SelectMonthText => AllSelected ? "Clear Month" : "Select Month";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleExpandedText));
        OnPropertyChanged(nameof(ChevronGlyph));

        if (value)
        {
            EnsureVisiblePhotosMaterialized();
            if (_isMaterialized)
            {
                _ = NotifyExpandedAsync();
            }
            return;
        }

        _materializeCts?.Cancel();
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    public void ToggleMonthSelection()
    {
        var target = !AllSelected;
        foreach (var photo in Photos)
        {
            photo.IsSelected = target;
        }

        RefreshCounts();
        _onChanged();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectMonthText));
        OnPropertyChanged(nameof(ToggleExpandedText));
        OnPropertyChanged(nameof(ChevronGlyph));
    }

    private void EnsureVisiblePhotosMaterialized()
    {
        if (_isMaterialized || _isMaterializing)
        {
            return;
        }

        _materializeTask = MaterializeVisiblePhotosAsync();
    }

    public Task EnsureMaterializedAsync()
    {
        EnsureVisiblePhotosMaterialized();
        return _materializeTask ?? Task.CompletedTask;
    }

    private async Task MaterializeVisiblePhotosAsync()
    {
        _materializeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _materializeCts = cts;
        var token = cts.Token;
        _isMaterializing = true;

        try
        {
            var added = 0;
            for (var index = VisiblePhotos.Count; index < Photos.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                VisiblePhotos.Add(Photos[index]);
                added++;

                if (VisiblePhotos.Count == InitialMaterializeCount && IsExpanded)
                {
                    await NotifyExpandedAsync();
                }

                if (added % MaterializeBatchSize == 0)
                {
                    await Task.Delay(1, token);
                }
            }

            _isMaterialized = VisiblePhotos.Count == Photos.Count;
            if (_isMaterialized && IsExpanded)
            {
                await NotifyExpandedAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isMaterializing = false;
        }
    }

    private async Task NotifyExpandedAsync()
    {
        if (_onExpandedAsync == null || VisiblePhotos.Count == 0)
        {
            return;
        }

        try
        {
            await _onExpandedAsync(VisiblePhotos.ToList());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to process expanded month group '{Label}'. {ex.Message}");
        }
    }
}

public sealed record DownloadLayoutOption(DownloadLayout Layout, string Label)
{
    public override string ToString() => Label;
}
