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

public enum MobileReviewFilter
{
    New,
    Saved,
    All
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
    private CancellationTokenSource? _photoRefreshCts;
    private readonly Dictionary<string, MobilePhotoItemViewModel> _photoItemsByKey = new(StringComparer.OrdinalIgnoreCase);

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

        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.Flat,
            "Single Folder",
            "Fastest, no child or date folders"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.YearMonth,
            "Year / Month",
            "Simple folders for one or two students"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.StudentYear,
            "Student / Year",
            "Good for organizing by child"));
        DownloadLayoutOptions.Add(new DownloadLayoutOption(
            DownloadLayout.StudentYearMonth,
            "Student / Year / Month",
            "Best for mixed classrooms and many dates"));

        SelectedDownloadLayout = DownloadLayoutOptions.FirstOrDefault(option => option.Layout == _settings.DownloadLayout)
                                 ?? DownloadLayoutOptions.First();
        SetDownloadLayoutSelection(SelectedDownloadLayout);
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
    [ObservableProperty] private string _cacheUsageText = "Cache: checking...";
    [ObservableProperty] private MobileReviewFilter _selectedReviewFilter = MobileReviewFilter.New;
    [ObservableProperty] private int _loadedPhotoCount;
    [ObservableProperty] private int _expectedPhotoCount;
    [ObservableProperty] private bool _isPhotoListRefreshing;

    public ObservableCollection<Student> Students { get; } = [];
    public ObservableCollection<MobilePhotoMonthGroupViewModel> MonthGroups { get; } = [];
    public ObservableCollection<MobileGalleryRowViewModel> GalleryRows { get; } = [];
    public ObservableCollection<MobilePhotoReviewGroupViewModel> ReviewGroups { get; } = [];
    public ObservableCollection<DownloadLayoutOption> DownloadLayoutOptions { get; } = [];

    public bool IsLoginVisible => State == MobileAppState.Login;
    public bool IsLoadingVisible => State == MobileAppState.LoadingStudents || State == MobileAppState.LoadingPhotos;
    public bool IsStudentSelectionVisible => State == MobileAppState.SelectStudent;
    public bool IsGalleryVisible => State == MobileAppState.Gallery || State == MobileAppState.Downloading;
    public bool IsDownloading => State == MobileAppState.Downloading;
    public bool IsThumbnailPrefetchOverlayVisible => IsGalleryVisible && IsThumbnailPrefetching;
    public bool IsPhotoLoadingProgressVisible => IsPhotoListRefreshing && LoadedPhotoCount > 0;
    public bool IsFinalPhotoSummaryVisible => IsGalleryVisible && !IsPhotoListRefreshing;
    public bool IsTimelineLoadingSummaryVisible => IsGalleryVisible && IsPhotoListRefreshing;
    public bool IsBottomProgressVisible => IsDownloading || IsPhotoListRefreshing;
    public bool IsSaveActionsEnabled => IsGalleryVisible && !IsPhotoListRefreshing && SelectedCount > 0;
    public string HeaderTitle => IsGalleryVisible && SelectedStudent != null
        ? "Timeline Explorer"
        : "Procare Photo Downloader";
    public string HeaderSubtitle => IsGalleryVisible
        ? IsPhotoListRefreshing
            ? $"{SelectedStudent?.FullName} | Loading photo list..."
            : $"{SelectedStudent?.FullName} | {UnsavedCount} new photos ready"
        : StatusMessage;

    public int TotalCount => MonthGroups.SelectMany(group => group.Photos).Count();
    public int SelectedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => photo.IsSelected);
    public int SelectedNewCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => photo.IsSelected && !photo.IsDownloaded);
    public int DownloadedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => photo.IsDownloaded);
    public int UnsavedCount => MonthGroups.SelectMany(group => group.Photos).Count(photo => !photo.IsDownloaded);
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;
    public bool ActiveFilterAllSelected => ActiveFilterPhotos.Count > 0 && ActiveFilterPhotos.All(photo => photo.IsSelected);
    public string SelectAllButtonText => AllSelected ? "Deselect All" : "Select All";
    public string SelectAllShortButtonText => AllSelected ? "Clear" : "All";
    public string ActiveFilterSelectionButtonText => ActiveFilterAllSelected ? "Deselect All" : "Select All";
    public string UnsavedDownloadButtonText => $"Save New ({UnsavedCount})";
    public string DownloadSelectedButtonText => SelectedCount > 0 ? $"Save Selected ({SelectedCount})" : "Save Selected";
    public string PrimaryDownloadButtonText => IsPhotoListRefreshing
        ? "Loading..."
        : SelectedNewCount > 0
            ? $"Save New ({SelectedNewCount})"
            : DownloadSelectedButtonText;
    public string TimelineSubtitle => IsPhotoListRefreshing
        ? $"{LoadedPhotoCount} photos found so far. Full saved and new counts appear when the scan finishes."
        : $"{TotalCount} photos | {UnsavedCount} new | {DownloadedCount} saved";
    public string BottomStatusText => IsPhotoListRefreshing
        ? "Scanning first. Save actions unlock when the full count is known."
        : $"{SelectedCount} selected";
    public string ReviewIntroTitle => SelectedReviewFilter switch
    {
        MobileReviewFilter.New => UnsavedCount == 0 ? "No new photos" : "New photos are selected",
        MobileReviewFilter.Saved => "Saved photos",
        _ => "All photos"
    };
    public string ReviewIntroSubtitle => SelectedReviewFilter switch
    {
        MobileReviewFilter.New => UnsavedCount == 0
            ? "Everything in this library has already been saved."
            : "Review the queue, deselect anything you do not want, then save once.",
        MobileReviewFilter.Saved => "These photos are already in your local download history.",
        _ => "Browse the full library and select any photos you want to save again."
    };
    public string NewTabText => $"New {UnsavedCount}";
    public string SavedTabText => $"Saved {DownloadedCount}";
    public string AllTabText => $"All {TotalCount}";
    public bool IsNewFilterActive => SelectedReviewFilter == MobileReviewFilter.New;
    public bool IsSavedFilterActive => SelectedReviewFilter == MobileReviewFilter.Saved;
    public bool IsAllFilterActive => SelectedReviewFilter == MobileReviewFilter.All;
    public string LoadingPhotoProgressText => ExpectedPhotoCount > 0
        ? $"Loaded {LoadedPhotoCount} of {ExpectedPhotoCount} photos"
        : LoadedPhotoCount > 0
            ? $"Found {LoadedPhotoCount} photos so far"
            : "";
    public string DownloadRootPath => _downloader.DownloadRootPath;
    public string DownloadLayoutPreview => MobileDownloadPathHelper.BuildLayoutPreviewPath(
        SelectedDownloadLayout?.Layout ?? DownloadLayout.StudentYearMonth,
        SelectedStudent?.FullName);
    public bool CanShowPreviousPhoto => ActiveViewerPhoto != null && GetAllPhotos().IndexOf(ActiveViewerPhoto) > 0;
    public bool CanShowNextPhoto
    {
        get
        {
            var photos = GetAllPhotos();
            var index = ActiveViewerPhoto == null ? -1 : photos.IndexOf(ActiveViewerPhoto);
            return index >= 0 && index < photos.Count - 1;
        }
    }

    partial void OnStateChanged(MobileAppState value)
    {
        OnPropertyChanged(nameof(IsLoginVisible));
        OnPropertyChanged(nameof(IsLoadingVisible));
        OnPropertyChanged(nameof(IsStudentSelectionVisible));
        OnPropertyChanged(nameof(IsGalleryVisible));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsThumbnailPrefetchOverlayVisible));
        OnPropertyChanged(nameof(IsPhotoLoadingProgressVisible));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    partial void OnSelectedStudentChanged(Student? value)
    {
        OnPropertyChanged(nameof(DownloadLayoutPreview));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    partial void OnIsThumbnailPrefetchingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThumbnailPrefetchOverlayVisible));
    }

    partial void OnSelectedReviewFilterChanged(MobileReviewFilter value)
    {
        RebuildReviewGroups();
        RefreshCounts();
        OnPropertyChanged(nameof(IsNewFilterActive));
        OnPropertyChanged(nameof(IsSavedFilterActive));
        OnPropertyChanged(nameof(IsAllFilterActive));
        OnPropertyChanged(nameof(ReviewIntroTitle));
        OnPropertyChanged(nameof(ReviewIntroSubtitle));
        OnPropertyChanged(nameof(PrimaryDownloadButtonText));
        OnPropertyChanged(nameof(ActiveFilterSelectionButtonText));
    }

    partial void OnLoadedPhotoCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsPhotoLoadingProgressVisible));
        OnPropertyChanged(nameof(LoadingPhotoProgressText));
    }

    partial void OnExpectedPhotoCountChanged(int value)
    {
        OnPropertyChanged(nameof(LoadingPhotoProgressText));
    }

    partial void OnSelectedDownloadLayoutChanged(DownloadLayoutOption? value)
    {
        if (value == null)
        {
            return;
        }

        _settings.DownloadLayout = value.Layout;
        _settingsService.Save(_settings);
        SetDownloadLayoutSelection(value);
        OnPropertyChanged(nameof(DownloadLayoutPreview));
    }

    [RelayCommand]
    public void SelectDownloadLayout(DownloadLayoutOption? option)
    {
        if (option == null)
        {
            return;
        }

        SelectedDownloadLayout = option;
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

    private void SetDownloadLayoutSelection(DownloadLayoutOption? selected)
    {
        if (selected == null)
        {
            return;
        }

        foreach (var option in DownloadLayoutOptions)
        {
            option.IsSelected = option.Layout == selected.Layout;
        }
    }

    [RelayCommand]
    public async Task OpenPhotoViewerAsync(MobilePhotoItemViewModel? photo)
    {
        if (photo == null)
        {
            return;
        }

        ActiveViewerPhoto = photo;
        OnPropertyChanged(nameof(CanShowPreviousPhoto));
        OnPropertyChanged(nameof(CanShowNextPhoto));
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

    [RelayCommand]
    public async Task ClearCacheAsync()
    {
        await _imageCache.ClearAsync();
        foreach (var photo in MonthGroups.SelectMany(group => group.Photos))
        {
            photo.ThumbnailSource = null;
            photo.FullImageSource = null;
        }

        PhotoViewerSource = ActiveViewerPhoto?.ThumbnailSource;
        RefreshCacheUsage();
        WarmInitialThumbnails(_cts?.Token ?? CancellationToken.None);
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
    public void ShowNewPhotos()
    {
        SelectedReviewFilter = MobileReviewFilter.New;
    }

    [RelayCommand]
    public void ShowSavedPhotos()
    {
        SelectedReviewFilter = MobileReviewFilter.Saved;
    }

    [RelayCommand]
    public void ShowAllPhotos()
    {
        SelectedReviewFilter = MobileReviewFilter.All;
    }

    [RelayCommand]
    public void ToggleActiveFilterSelection()
    {
        var photos = ActiveFilterPhotos;
        if (photos.Count == 0)
        {
            return;
        }

        var target = !photos.All(photo => photo.IsSelected);
        foreach (var photo in photos)
        {
            photo.IsSelected = target;
        }

        RefreshCounts();
    }

    [RelayCommand]
    public void BackToStudents()
    {
        _cts?.Cancel();
        MonthGroups.Clear();
        GalleryRows.Clear();
        ReviewGroups.Clear();
        SelectedStudent = null;
        ClosePhotoViewer();
        IsThumbnailPrefetching = false;
        State = MobileAppState.SelectStudent;
        StatusMessage = "Select a student.";
        SubStatus = "Your session is still active.";
        ProgressText = "";
        ProgressValue = 0;
        LoadedPhotoCount = 0;
        ExpectedPhotoCount = 0;
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
    public async Task DownloadPrimaryAsync()
    {
        var photos = SelectedReviewFilter == MobileReviewFilter.New
            ? MonthGroups.SelectMany(group => group.Photos).Where(photo => photo.IsSelected && !photo.IsDownloaded).ToList()
            : MonthGroups.SelectMany(group => group.Photos).Where(photo => photo.IsSelected).ToList();

        await DownloadPhotosAsync(photos, "No photos selected.");
    }

    [RelayCommand]
    public void SignOut()
    {
        _cts?.Cancel();
        _api.ClearCredentials();
        Students.Clear();
        MonthGroups.Clear();
        GalleryRows.Clear();
        ReviewGroups.Clear();
        IsThumbnailPrefetching = false;
        SelectedStudent = null;
        ClosePhotoViewer();
        IsSettingsOpen = false;
        State = MobileAppState.Login;
        StatusMessage = "Session cleared in the app. Log in again to continue.";
        SubStatus = "If Procare auto-signs you back in, sign out inside the web page and then log in with the other account.";
        ProgressText = "";
        ProgressValue = 0;
        LoadedPhotoCount = 0;
        ExpectedPhotoCount = 0;
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
        OnPropertyChanged(nameof(SelectedNewCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(UnsavedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ActiveFilterAllSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        OnPropertyChanged(nameof(SelectAllShortButtonText));
        OnPropertyChanged(nameof(ActiveFilterSelectionButtonText));
        OnPropertyChanged(nameof(UnsavedDownloadButtonText));
        OnPropertyChanged(nameof(DownloadSelectedButtonText));
        OnPropertyChanged(nameof(PrimaryDownloadButtonText));
        OnPropertyChanged(nameof(ReviewIntroTitle));
        OnPropertyChanged(nameof(ReviewIntroSubtitle));
        OnPropertyChanged(nameof(NewTabText));
        OnPropertyChanged(nameof(SavedTabText));
        OnPropertyChanged(nameof(AllTabText));
        OnPropertyChanged(nameof(DownloadLayoutPreview));
        OnPropertyChanged(nameof(HeaderSubtitle));

        foreach (var group in MonthGroups)
        {
            group.RefreshCounts();
        }

        foreach (var row in GalleryRows)
        {
            row.RefreshCounts();
        }

        foreach (var group in ReviewGroups)
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

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        State = MobileAppState.LoadingPhotos;
        StatusMessage = $"Loading photos for {SelectedStudent.FullName}...";
        SubStatus = "Scanning the full photo list before showing saved and selected totals.";
        ProgressValue = 0;
        ProgressText = "";
        LoadedPhotoCount = 0;
        ExpectedPhotoCount = 0;
        IsThumbnailPrefetching = false;
        MonthGroups.Clear();
        GalleryRows.Clear();
        ReviewGroups.Clear();

        var studentId = SelectedStudent.Id;
        var studentName = SelectedStudent.FullName;
        MobilePhotoCacheResult? staleCache = null;

        try
        {
            var cached = await _photoCache.TryLoadAsync(studentId, token);
            if (cached.HasCache)
            {
                if (cached.IsFresh)
                {
                    ApplyPhotosToGallery(cached.Photos);
                    State = MobileAppState.Gallery;
                    StatusMessage = $"{studentName} - {TotalCount} photos";
                    SubStatus = $"{UnsavedCount} new photos ready";
                    ProgressText = "";
                    ProgressValue = 0;
                    RefreshCounts();
                    WarmInitialThumbnails(token);
                    return;
                }

                staleCache = cached;
                var ageMinutes = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - cached.SavedAtUtc).TotalMinutes));
                SubStatus = $"Refreshing cached library from {ageMinutes} minutes ago.";
            }

            var progress = new Progress<(int loaded, int total)>(item =>
            {
                LoadedPhotoCount = Math.Max(0, item.loaded);
                ExpectedPhotoCount = Math.Max(0, item.total);
                ProgressValue = ExpectedPhotoCount > 0
                    ? Math.Clamp((double)LoadedPhotoCount / ExpectedPhotoCount, 0, 1)
                    : 0;
                ProgressText = LoadingPhotoProgressText;
                StatusMessage = "Loading photo list...";
            });

            var photos = await _api.GetPhotosAsync(studentId, progress, token);
            token.ThrowIfCancellationRequested();
            await _photoCache.SaveAsync(studentId, photos, token);
            token.ThrowIfCancellationRequested();

            ApplyPhotosToGallery(photos);
            State = MobileAppState.Gallery;
            StatusMessage = $"{studentName} - {TotalCount} photos";
            SubStatus = $"{UnsavedCount} new photos ready";
            ProgressText = "";
            ProgressValue = 0;
            RefreshCounts();
            WarmInitialThumbnails(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (staleCache?.HasCache == true)
            {
                ApplyPhotosToGallery(staleCache.Value.Photos);
                State = MobileAppState.Gallery;
                StatusMessage = $"{studentName} - {TotalCount} photos";
                SubStatus = $"Using cached photos. Refresh failed: {ex.Message}";
                AppLog.Error($"Mobile photo refresh failed for student {studentId}; cache kept.", ex);
                WarmInitialThumbnails(token);
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
        GalleryRows.Clear();
        ReviewGroups.Clear();

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
                    RefreshCounts,
                    isSelected: !_history.IsDownloaded(photo))).ToList(),
                RefreshCounts,
                isExpanded: false,
                WarmVisibleThumbnailsAsync));

        foreach (var group in groups)
        {
            MonthGroups.Add(group);
        }

        SelectedReviewFilter = UnsavedCount > 0 ? MobileReviewFilter.New : MobileReviewFilter.All;
        RebuildReviewGroups();
        RefreshCacheUsage();
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
                    photo.IsSelected = false;
                }
            }

            State = MobileAppState.Gallery;
            StatusMessage = $"Done! {result.Succeeded} downloaded, {result.Skipped} skipped, {result.Failed} failed.";
            SubStatus = result.Failed > 0
                ? string.Join("; ", result.Errors.Take(3))
                : $"Saved to: {result.OutputSummary}";
            ProgressText = "";
            if (SelectedReviewFilter == MobileReviewFilter.New && UnsavedCount == 0)
            {
                SelectedReviewFilter = MobileReviewFilter.All;
            }
            else
            {
                RebuildReviewGroups();
            }

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
            RefreshCacheUsage();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WarmInitialThumbnails(CancellationToken token)
    {
        var firstPhotos = ReviewGroups
            .Take(4)
            .SelectMany(group => group.PreviewPhotos)
            .ToList();

        if (firstPhotos.Count == 0)
        {
            return;
        }

        _ = WarmVisibleThumbnailsAsync(firstPhotos);
    }

    private void RebuildGalleryRows()
    {
        GalleryRows.Clear();

        foreach (var group in MonthGroups)
        {
            GalleryRows.Add(MobileGalleryRowViewModel.CreateHeader(group, RebuildGalleryRows, RefreshCounts));
            if (!group.IsExpanded)
            {
                continue;
            }

            foreach (var rowPhotos in group.Photos.Chunk(8))
            {
                GalleryRows.Add(MobileGalleryRowViewModel.CreatePhotoRow(group, rowPhotos));
            }
        }
    }

    private List<MobilePhotoItemViewModel> GetAllPhotos()
    {
        return MonthGroups.SelectMany(group => group.Photos).ToList();
    }

    private List<MobilePhotoItemViewModel> ActiveFilterPhotos => SelectedReviewFilter switch
    {
        MobileReviewFilter.New => MonthGroups.SelectMany(group => group.Photos).Where(photo => !photo.IsDownloaded).ToList(),
        MobileReviewFilter.Saved => MonthGroups.SelectMany(group => group.Photos).Where(photo => photo.IsDownloaded).ToList(),
        _ => GetAllPhotos()
    };

    private void RebuildReviewGroups()
    {
        ReviewGroups.Clear();

        foreach (var group in MonthGroups)
        {
            var photos = SelectedReviewFilter switch
            {
                MobileReviewFilter.New => group.Photos.Where(photo => !photo.IsDownloaded).ToList(),
                MobileReviewFilter.Saved => group.Photos.Where(photo => photo.IsDownloaded).ToList(),
                _ => group.Photos.ToList()
            };

            if (photos.Count == 0)
            {
                continue;
            }

            ReviewGroups.Add(new MobilePhotoReviewGroupViewModel(
                group.Label,
                photos,
                RefreshCounts,
                WarmVisibleThumbnailsAsync));
        }
    }

    private async Task MovePhotoViewerAsync(int delta)
    {
        if (ActiveViewerPhoto == null)
        {
            return;
        }

        var photos = GetAllPhotos();
        var currentIndex = photos.IndexOf(ActiveViewerPhoto);
        var nextIndex = currentIndex + delta;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= photos.Count)
        {
            return;
        }

        await OpenPhotoViewerAsync(photos[nextIndex]);
    }

    private void RefreshCacheUsage()
    {
        var usage = _imageCache.GetUsage();
        CacheUsageText = $"Cache: {usage.DisplayText}";
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
        Action onChanged,
        bool isSelected = false)
    {
        Photo = photo;
        _isDownloaded = isDownloaded;
        _thumbnailSource = thumbnailSource;
        _fullImageSource = fullImageSource;
        _isSelected = isSelected;
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

public partial class MobilePhotoReviewGroupViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyCollection<MobilePhotoItemViewModel>, Task>? _onExpandedAsync;
    private bool _isMaterialized;

    public MobilePhotoReviewGroupViewModel(
        string label,
        IReadOnlyCollection<MobilePhotoItemViewModel> photos,
        Action onChanged,
        Func<IReadOnlyCollection<MobilePhotoItemViewModel>, Task>? onExpandedAsync)
    {
        Label = label;
        Photos = new ObservableCollection<MobilePhotoItemViewModel>(photos);
        PreviewPhotos = new ObservableCollection<MobilePhotoItemViewModel>(photos.Take(3));
        VisiblePhotos = [];
        _onChanged = onChanged;
        _onExpandedAsync = onExpandedAsync;
    }

    public string Label { get; }
    public ObservableCollection<MobilePhotoItemViewModel> Photos { get; }
    public ObservableCollection<MobilePhotoItemViewModel> PreviewPhotos { get; }
    public ObservableCollection<MobilePhotoItemViewModel> VisiblePhotos { get; }

    [ObservableProperty] private bool _isExpanded;

    public int TotalCount => Photos.Count;
    public int SelectedCount => Photos.Count(photo => photo.IsSelected);
    public int DownloadedCount => Photos.Count(photo => photo.IsDownloaded);
    public int NewCount => Photos.Count(photo => !photo.IsDownloaded);
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;
    public string Subtitle => $"{TotalCount} photos | {SelectedCount} selected | {DownloadedCount} saved";
    public string SelectActionText => AllSelected ? "All" : SelectedCount > 0 ? "Some" : "Select";
    public string ExpandText => IsExpanded ? "Hide" : "Review";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandText));

        if (!value)
        {
            return;
        }

        EnsureVisiblePhotosMaterialized();
        _ = NotifyExpandedAsync();
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    public void ToggleGroupSelection()
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
        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SelectActionText));
    }

    private void EnsureVisiblePhotosMaterialized()
    {
        if (_isMaterialized)
        {
            return;
        }

        foreach (var photo in Photos)
        {
            VisiblePhotos.Add(photo);
        }

        _isMaterialized = true;
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
            AppLog.Warn($"Failed to process review group '{Label}'. {ex.Message}");
        }
    }
}

public sealed partial class MobileGalleryRowViewModel : ObservableObject
{
    private readonly MobilePhotoMonthGroupViewModel? _group;
    private readonly Action? _rebuildRows;
    private readonly Action? _refreshCounts;

    private MobileGalleryRowViewModel(
        bool isHeader,
        MobilePhotoMonthGroupViewModel group,
        IReadOnlyCollection<MobilePhotoItemViewModel> photos,
        Action? rebuildRows,
        Action? refreshCounts)
    {
        IsHeader = isHeader;
        IsPhotoRow = !isHeader;
        _group = group;
        _rebuildRows = rebuildRows;
        _refreshCounts = refreshCounts;
        Label = group.Label;
        Photos = new ObservableCollection<MobilePhotoItemViewModel>(photos);
    }

    public bool IsHeader { get; }
    public bool IsPhotoRow { get; }
    public string Label { get; }
    public ObservableCollection<MobilePhotoItemViewModel> Photos { get; }
    public int TotalCount => _group?.TotalCount ?? 0;
    public int SelectedCount => _group?.SelectedCount ?? 0;
    public int DownloadedCount => _group?.DownloadedCount ?? 0;
    public string ChevronGlyph => _group?.ChevronGlyph ?? "";
    public string SelectMonthText => _group?.SelectMonthText ?? "";

    public static MobileGalleryRowViewModel CreateHeader(
        MobilePhotoMonthGroupViewModel group,
        Action rebuildRows,
        Action refreshCounts)
    {
        return new MobileGalleryRowViewModel(true, group, [], rebuildRows, refreshCounts);
    }

    public static MobileGalleryRowViewModel CreatePhotoRow(
        MobilePhotoMonthGroupViewModel group,
        IReadOnlyCollection<MobilePhotoItemViewModel> photos)
    {
        return new MobileGalleryRowViewModel(false, group, photos, null, null);
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

        _group.ToggleMonthSelectionCommand.Execute(null);
        RefreshCounts();
        _refreshCounts?.Invoke();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadedCount));
        OnPropertyChanged(nameof(ChevronGlyph));
        OnPropertyChanged(nameof(SelectMonthText));
    }
}

public partial class MobilePhotoMonthGroupViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyCollection<MobilePhotoItemViewModel>, Task>? _onExpandedAsync;
    private bool _isMaterialized;

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
    public string ChevronGlyph => IsExpanded ? "v" : ">";
    public string SelectMonthText => AllSelected ? "Clear Month" : "Select Month";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleExpandedText));
        OnPropertyChanged(nameof(ChevronGlyph));

        if (value)
        {
            EnsureVisiblePhotosMaterialized();
            _ = NotifyExpandedAsync();
            return;
        }
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
        if (_isMaterialized)
        {
            return;
        }

        foreach (var photo in Photos)
        {
            VisiblePhotos.Add(photo);
        }

        _isMaterialized = true;
    }

    public Task EnsureMaterializedAsync()
    {
        EnsureVisiblePhotosMaterialized();
        return Task.CompletedTask;
    }

    public async Task NotifyExpandedAsync()
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

public sealed partial class DownloadLayoutOption : ObservableObject
{
    public DownloadLayoutOption(DownloadLayout layout, string label, string description)
    {
        Layout = layout;
        Label = label;
        Description = description;
    }

    public DownloadLayout Layout { get; }
    public string Label { get; }
    public string Description { get; }

    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Label;
}
