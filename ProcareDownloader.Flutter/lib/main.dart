import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:webview_flutter/webview_flutter.dart';

import 'local_services.dart';
import 'models.dart';
import 'procare_api.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const ProcareDownloaderApp());
}

const _ink = Color(0xFF101828);
const _muted = Color(0xFF667085);
const _border = Color(0xFFDCE3EE);
const _surface = Color(0xFFFFFFFF);
const _background = Color(0xFFF4F7FB);
const _primary = Color(0xFF0F766E);
const _primarySoft = Color(0xFFE6FFFA);
const _blueSoft = Color(0xFFEAF2FF);

class ProcareDownloaderApp extends StatelessWidget {
  const ProcareDownloaderApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Procare Photo Downloader',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(seedColor: _primary),
        scaffoldBackgroundColor: _background,
        fontFamily: 'Roboto',
      ),
      home: const ProcareHomePage(),
    );
  }
}

class ProcareHomePage extends StatefulWidget {
  const ProcareHomePage({super.key});

  @override
  State<ProcareHomePage> createState() => _ProcareHomePageState();
}

class _ProcareHomePageState extends State<ProcareHomePage> {
  final _api = ProcareApi();
  final _settings = AppSettingsStore();
  final _metadataCache = PhotoMetadataCache();
  final _history = DownloadHistoryStore();

  late final WebViewController _webViewController;
  Timer? _authTimer;

  AppStage _stage = AppStage.login;
  String _status = 'Log in to Procare in the embedded browser.';
  String _subStatus =
      'The app continues automatically after it detects your active session.';
  List<Student> _students = [];
  Student? _selectedStudent;

  final Map<String, PhotoItem> _itemsByKey = {};
  final Map<String, Future<Uint8List>> _thumbnailLoads = {};
  var _loadedPhotoCount = 0;
  var _expectedPhotoCount = 0;
  var _isRefreshingPhotos = false;
  var _isDownloading = false;
  var _isContinuingBrowserSession = false;
  var _browserScriptRequestId = 0;
  var _downloadProgress = 0.0;
  var _progressText = '';
  DownloadLayout _layout = DownloadLayout.studentYearMonth;
  DownloadDestination _destination = DownloadDestination.cameraRoll;
  String? _customDirectoryUri;
  String? _customDirectoryLabel;

  static const _maxThumbnailLoads = 96;

  @override
  void initState() {
    super.initState();
    _loadSettings();
    _configureWebView();
    _authTimer = Timer.periodic(
      const Duration(seconds: 2),
      (_) => _captureTokenFromWebView(),
    );
  }

  @override
  void dispose() {
    _authTimer?.cancel();
    super.dispose();
  }

  Future<void> _loadSettings() async {
    final layout = await _settings.loadLayout();
    final destination = await _settings.loadDestination();
    final customDirectory = await _settings.loadCustomDirectory();
    if (!mounted) {
      return;
    }
    setState(() {
      _layout = layout;
      _destination =
          destination == DownloadDestination.customFolder &&
              customDirectory == null
          ? DownloadDestination.cameraRoll
          : destination;
      _customDirectoryUri = customDirectory?.uri;
      _customDirectoryLabel = customDirectory?.label;
    });
  }

  void _configureWebView() {
    final controller = WebViewController()
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..setNavigationDelegate(
        NavigationDelegate(
          onPageFinished: (url) async {
            if (_stage == AppStage.login && !url.contains('/login')) {
              setState(() {
                _status = 'Session detected. Finishing sign-in...';
                _subStatus =
                    'Using the authenticated browser session to load your students.';
              });
            }
            await _injectAuthHook();
            await _captureTokenFromWebView();
          },
        ),
      )
      ..loadRequest(Uri.parse('https://schools.procareconnect.com/login'));

    _webViewController = controller;
    _api.attachBrowser(_runBrowserJsonExpression);
  }

  Future<void> _injectAuthHook() async {
    try {
      await _webViewController.runJavaScript(_authHookScript);
    } catch (_) {
      // Procare occasionally navigates through transient pages where injection is not possible.
    }
  }

  Future<void> _captureTokenFromWebView() async {
    if (_stage != AppStage.login) {
      return;
    }

    try {
      await _injectAuthHook();
      final raw = await _webViewController.runJavaScriptReturningResult(
        _captureTokenScript,
      );
      final token = _parseTokenResult(raw);
      if (token == null) {
        await _tryContinueFromBrowserSession();
        return;
      }

      _api.setCredentials(token);
      await _loadStudents();
    } catch (_) {
      if (!mounted) {
        return;
      }
      setState(() {
        _status = 'Waiting for Procare session...';
        _subStatus =
            'Stay on the Procare page. The app is watching for the authenticated API session.';
      });
    }
  }

  Future<void> _tryContinueFromBrowserSession() async {
    if (_isContinuingBrowserSession || _stage != AppStage.login) {
      return;
    }

    _isContinuingBrowserSession = true;
    try {
      final students = await _api.getStudents();
      if (!mounted || _stage != AppStage.login || students.isEmpty) {
        return;
      }

      setState(() {
        _students = students;
        _stage = AppStage.selectStudent;
        _status = 'Select a student.';
        _subStatus = students.length == 1
            ? 'One student found.'
            : 'Choose whose photos to browse.';
      });

      if (students.length == 1) {
        await _selectStudent(students.first);
      }
    } catch (_) {
      // The page can be logged in before Procare's request helpers are ready. The timer retries.
    } finally {
      _isContinuingBrowserSession = false;
    }
  }

  Future<Object?> _runBrowserJsonExpression(String javaScriptExpression) async {
    await _injectAuthHook();

    final resultKey = '__procareDownloaderAsync_${++_browserScriptRequestId}';
    final resultKeyJson = jsonEncode(resultKey);
    final launcher =
        '''
(function() {
  const resultKey = $resultKeyJson;
  try { sessionStorage.removeItem(resultKey); } catch {}
  Promise.resolve()
    .then(async function() {
      const value = await ($javaScriptExpression);
      try {
        sessionStorage.setItem(resultKey, JSON.stringify({ ok: true, value: value ?? null }));
      } catch (error) {
        sessionStorage.setItem(resultKey, JSON.stringify({ ok: false, error: 'Could not store browser result: ' + String(error) }));
      }
    })
    .catch(function(error) {
      try {
        sessionStorage.setItem(resultKey, JSON.stringify({ ok: false, error: String(error && (error.stack || error.message) || error) }));
      } catch {}
    });
  return resultKey;
})();
''';

    await _webViewController.runJavaScript(launcher);

    for (var attempt = 0; attempt < 60; attempt++) {
      await Future<void>.delayed(const Duration(milliseconds: 200));
      final raw = await _webViewController.runJavaScriptReturningResult(
        "(function() { try { return sessionStorage.getItem($resultKeyJson) || ''; } catch { return ''; } })();",
      );
      final text = _decodeJavaScriptStringResult(raw);
      if (text == null || text.isEmpty) {
        continue;
      }

      await _webViewController.runJavaScript(
        "try { sessionStorage.removeItem($resultKeyJson); } catch {}",
      );
      final envelope = jsonDecode(text);
      if (envelope is Map && envelope['ok'] == true) {
        return envelope['value'];
      }

      return {
        'error': true,
        'body': envelope is Map
            ? '${envelope['error'] ?? 'Browser request failed.'}'
            : 'Browser request failed.',
      };
    }

    return const {'error': true, 'body': 'Browser request timed out.'};
  }

  TokenInfo? _parseTokenResult(Object? raw) {
    final text = _decodeJavaScriptStringResult(raw);
    if (text == null || text.isEmpty || text == 'null') {
      return null;
    }

    final node = jsonDecode(text);
    if (node is! Map) {
      return null;
    }

    final token = '${node['accessToken'] ?? ''}'.trim();
    if (token.isEmpty) {
      return null;
    }

    final orgId = '${node['organizationId'] ?? ''}'.trim();
    return TokenInfo(
      accessToken: token,
      organizationId: orgId.isEmpty ? null : orgId,
    );
  }

  String? _decodeJavaScriptStringResult(Object? raw) {
    if (raw == null) {
      return null;
    }

    var text = '$raw'.trim();
    if (text.isEmpty || text == 'null') {
      return null;
    }

    for (var i = 0; i < 2; i++) {
      if (text.startsWith('"') && text.endsWith('"')) {
        text = jsonDecode(text) as String;
      }
    }

    return text;
  }

  Future<void> _loadStudents() async {
    if (!mounted) {
      return;
    }

    setState(() {
      _stage = AppStage.loadingStudents;
      _status = 'Loading students...';
      _subStatus = 'Using your authenticated Procare session.';
    });

    try {
      final students = await _api.getStudents();
      if (!mounted) {
        return;
      }

      setState(() {
        _students = students;
        _stage = AppStage.selectStudent;
        _status = 'Select a student.';
        _subStatus = students.length == 1
            ? 'One student found.'
            : 'Choose whose photos to browse.';
      });

      if (students.length == 1) {
        await _selectStudent(students.first);
      }
    } catch (error) {
      if (!mounted) {
        return;
      }
      setState(() {
        _stage = AppStage.login;
        _status = 'Could not load students.';
        _subStatus = '$error';
      });
    }
  }

  Future<void> _selectStudent(Student student) async {
    setState(() {
      _selectedStudent = student;
      _stage = AppStage.gallery;
      _itemsByKey.clear();
      _thumbnailLoads.clear();
      _loadedPhotoCount = 0;
      _expectedPhotoCount = 0;
      _isRefreshingPhotos = true;
      _downloadProgress = 0;
      _progressText = 'Starting photo scan...';
      _status = 'Timeline Explorer';
      _subStatus = student.fullName;
    });

    final cached = await _metadataCache.load(student.id);
    if (cached.hasCache && mounted) {
      final ageMinutes = DateTime.now()
          .toUtc()
          .difference(cached.savedAtUtc.toUtc())
          .inMinutes;
      await _mergePhotos(cached.photos);
      if (!mounted) {
        return;
      }
      setState(() {
        _subStatus = cached.isFresh
            ? 'Showing cached timeline while checking for changes.'
            : 'Refreshing cached library from $ageMinutes minutes ago.';
      });
    }

    try {
      final finalPhotos = await _api.getPhotos(
        student.id,
        onBatch: (batch) {
          if (!mounted || _selectedStudent?.id != student.id) {
            return;
          }
          _mergePhotos(batch.photos, loaded: batch.loaded, total: batch.total);
        },
      );

      if (!mounted || _selectedStudent?.id != student.id) {
        return;
      }

      await _metadataCache.save(student.id, finalPhotos);
      await _mergePhotos(
        finalPhotos,
        loaded: finalPhotos.length,
        total: finalPhotos.length,
      );
      setState(() {
        _isRefreshingPhotos = false;
        _loadedPhotoCount = finalPhotos.length;
        _expectedPhotoCount = finalPhotos.length;
        _progressText = '';
        _subStatus = '${_newCount()} new photos ready';
      });
    } catch (error) {
      if (!mounted || _selectedStudent?.id != student.id) {
        return;
      }

      setState(() {
        _isRefreshingPhotos = false;
        _progressText = '';
        _subStatus = _itemsByKey.isEmpty
            ? 'Photo load failed: $error'
            : 'Using cached photos. Refresh failed: $error';
      });
    }
  }

  Future<void> _mergePhotos(
    List<Photo> photos, {
    int? loaded,
    int? total,
  }) async {
    if (photos.isEmpty && loaded == null) {
      return;
    }

    final additions = <String, PhotoItem>{};
    for (final photo in photos) {
      final key = photo.key.toLowerCase();
      if (_itemsByKey.containsKey(key) || additions.containsKey(key)) {
        continue;
      }

      additions[key] = PhotoItem(
        photo: photo,
        isDownloaded: await _history.isDownloaded(photo),
      );
    }

    if (!mounted) {
      return;
    }

    setState(() {
      _itemsByKey.addAll(additions);
      if (loaded != null) {
        _loadedPhotoCount = loaded;
      } else {
        _loadedPhotoCount = _itemsByKey.length;
      }

      if (total != null) {
        _expectedPhotoCount = total;
      }

      if (_isRefreshingPhotos) {
        _progressText = _expectedPhotoCount > 0
            ? 'Loaded $_loadedPhotoCount of $_expectedPhotoCount photos'
            : 'Found $_loadedPhotoCount photos so far';
      }
    });
  }

  Future<void> _downloadSelected({bool includeDownloaded = false}) async {
    if (_isRefreshingPhotos || _isDownloading) {
      return;
    }

    final selected = _itemsByKey.values
        .where(
          (item) =>
              item.isSelected && (includeDownloaded || !item.isDownloaded),
        )
        .toList();
    if (selected.isEmpty) {
      setState(
        () => _subStatus = includeDownloaded
            ? 'Select photos to save.'
            : 'No new selected photos to save.',
      );
      return;
    }

    if (_destination == DownloadDestination.customFolder &&
        (_customDirectoryUri == null || _customDirectoryUri!.isEmpty)) {
      final picked = await _chooseCustomDirectory();
      if (picked == null) {
        if (mounted) {
          setState(() => _subStatus = 'Choose a custom folder before saving.');
        }
        return;
      }
    }

    setState(() {
      _stage = AppStage.downloading;
      _isDownloading = true;
      _downloadProgress = 0;
      _progressText = 'Preparing download...';
    });

    var succeeded = 0;
    var skipped = 0;
    var failed = 0;
    final errors = <String>[];
    final outputFolders = <String>{};

    for (var i = 0; i < selected.length; i++) {
      final item = selected[i];
      try {
        final alreadyDownloaded =
            item.isDownloaded || await _history.isDownloaded(item.photo);
        if (alreadyDownloaded && !includeDownloaded) {
          skipped++;
        } else {
          final bytes = await _api.getMediaBytes(item.photo.originalUrl);
          final saved = await DownloadStorage.savePhoto(
            destination: _destination,
            layout: _layout,
            studentName: _selectedStudent?.fullName,
            photo: item.photo,
            bytes: bytes,
            customDirectoryUri: _customDirectoryUri,
            customDirectoryLabel: _customDirectoryLabel,
            replaceExisting: includeDownloaded && alreadyDownloaded,
          );
          await _history.markDownloaded(item.photo, saved.path);
          outputFolders.add(saved.folderLabel);
          succeeded++;
        }

        item.isDownloaded = true;
        item.isSelected = false;
      } catch (error) {
        failed++;
        errors.add('${item.photo.id}: $error');
      }

      if (!mounted) {
        return;
      }
      setState(() {
        _downloadProgress = (i + 1) / selected.length;
        _progressText = 'Downloading ${i + 1}/${selected.length}';
      });
    }

    if (!mounted) {
      return;
    }

    final sortedFolders = outputFolders.toList()..sort();
    final outputSummary = sortedFolders.isEmpty
        ? await DownloadPathHelper.previewPath(
            _layout,
            _selectedStudent?.fullName,
            destination: _destination,
            customDirectoryLabel: _customDirectoryLabel,
          )
        : sortedFolders.first;
    setState(() {
      _stage = AppStage.gallery;
      _isDownloading = false;
      _downloadProgress = 0;
      _progressText = '';
      _subStatus = failed > 0
          ? 'Done with errors: ${errors.take(2).join('; ')}'
          : 'Done. $succeeded downloaded, $skipped skipped. Saved to $outputSummary.';
    });
  }

  Future<void> _reviewExportSettingsAndDownload() async {
    if (_isRefreshingPhotos || _isDownloading) {
      return;
    }

    final selectedCount = _selectedCount();
    final newSelectedCount = _selectedNewCount();
    final selectedSavedCount = _selectedSavedCount();
    if (selectedCount == 0) {
      setState(() => _subStatus = 'Select photos to save.');
      return;
    }

    final includeDownloaded = selectedSavedCount > 0;
    final shouldSave = await _openSettings(
      primaryActionText: _saveActionText(
        selectedCount: selectedCount,
        selectedNewCount: newSelectedCount,
        selectedSavedCount: selectedSavedCount,
      ),
      primaryActionDescription: _saveActionDescription(
        selectedNewCount: newSelectedCount,
        selectedSavedCount: selectedSavedCount,
      ),
      redownloadCount: selectedSavedCount,
    );
    if (shouldSave == true && mounted) {
      await _downloadSelected(includeDownloaded: includeDownloaded);
    }
  }

  void _toggleSelectAll() {
    final allSelected =
        _itemsByKey.values.isNotEmpty &&
        _itemsByKey.values.every((item) => item.isSelected);
    setState(() {
      for (final item in _itemsByKey.values) {
        item.isSelected = !allSelected;
      }
    });
  }

  void _toggleGroup(TimelineGroup group) {
    setState(() {
      final target = !group.allSelected;
      for (final item in group.items) {
        item.isSelected = target;
      }
    });
  }

  void _selectUnsaved() {
    setState(() {
      for (final item in _itemsByKey.values) {
        item.isSelected = !item.isDownloaded;
      }
      _subStatus = _newCount() == 0
          ? 'Everything in this timeline has already been saved.'
          : 'Selected all photos that have not been saved yet.';
    });
  }

  Future<bool?> _openSettings({
    String? primaryActionText,
    String? primaryActionDescription,
    int redownloadCount = 0,
  }) async {
    return showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (context) => _ExportSetupSheet(
        layout: _layout,
        destination: _destination,
        customDirectoryLabel: _customDirectoryLabel,
        selectedStudentName: _selectedStudent?.fullName,
        onLayoutChanged: (layout) async {
          await _settings.saveLayout(layout);
          if (mounted) {
            setState(() => _layout = layout);
          }
        },
        onDestinationChanged: (destination) async {
          await _setDestination(destination);
        },
        onChooseCustomDirectory: _chooseCustomDirectory,
        onClearHistory: () async {
          await _history.clear();
          for (final item in _itemsByKey.values) {
            item.isDownloaded = false;
            item.isSelected = true;
          }
          if (mounted) {
            setState(() => _subStatus = 'Download history cleared.');
          }
        },
        onSignOut: () {
          _api.clearCredentials();
          Navigator.of(context).pop(false);
          setState(() {
            _stage = AppStage.login;
            _students = [];
            _selectedStudent = null;
            _itemsByKey.clear();
            _thumbnailLoads.clear();
            _status = 'Log in to Procare in the embedded browser.';
            _subStatus =
                'The app continues automatically after it detects your active session.';
          });
          _webViewController.loadRequest(
            Uri.parse(
              'https://schools.procareconnect.com/login?reload=${DateTime.now().millisecondsSinceEpoch}',
            ),
          );
        },
        primaryActionText: primaryActionText,
        primaryActionDescription: primaryActionDescription,
        redownloadCount: redownloadCount,
      ),
    );
  }

  Future<void> _setDestination(DownloadDestination destination) async {
    await _settings.saveDestination(destination);
    if (mounted) {
      setState(() => _destination = destination);
    }
  }

  Future<PickedDirectory?> _chooseCustomDirectory() async {
    final picked = await DownloadStorage.chooseDirectory();
    if (picked == null) {
      return null;
    }

    await _settings.saveCustomDirectory(picked);
    await _settings.saveDestination(DownloadDestination.customFolder);
    if (mounted) {
      setState(() {
        _destination = DownloadDestination.customFolder;
        _customDirectoryUri = picked.uri;
        _customDirectoryLabel = picked.label;
      });
    }
    return picked;
  }

  List<TimelineGroup> _timelineGroups() {
    final groups = <String, List<PhotoItem>>{};
    final sortDates = <String, DateTime>{};
    for (final item in _itemsByKey.values) {
      final keyDate = item.photo.createdAt.millisecondsSinceEpoch == 0
          ? DateTime.fromMillisecondsSinceEpoch(0)
          : DateTime(
              item.photo.createdAt.year,
              item.photo.createdAt.month,
              item.photo.createdAt.day,
            );
      final key = keyDate.toIso8601String();
      groups.putIfAbsent(key, () => []).add(item);
      sortDates[key] = keyDate;
    }

    final timelineGroups = groups.entries.map((entry) {
      final date =
          sortDates[entry.key] ?? DateTime.fromMillisecondsSinceEpoch(0);
      final items = entry.value
        ..sort(
          (left, right) =>
              right.photo.createdAt.compareTo(left.photo.createdAt),
        );
      return TimelineGroup(
        label: _timelineLabel(date),
        sortDate: date,
        items: items,
      );
    }).toList()..sort((left, right) => right.sortDate.compareTo(left.sortDate));
    return timelineGroups;
  }

  String _timelineLabel(DateTime date) {
    if (date.millisecondsSinceEpoch == 0) {
      return 'Unknown Date';
    }

    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final target = DateTime(date.year, date.month, date.day);
    if (target == today) {
      return 'Today';
    }
    if (target == today.subtract(const Duration(days: 1))) {
      return 'Yesterday';
    }

    const months = [
      'Jan',
      'Feb',
      'Mar',
      'Apr',
      'May',
      'Jun',
      'Jul',
      'Aug',
      'Sep',
      'Oct',
      'Nov',
      'Dec',
    ];
    return '${months[date.month - 1]} ${date.day}, ${date.year}';
  }

  int _selectedCount() =>
      _itemsByKey.values.where((item) => item.isSelected).length;

  int _selectedNewCount() => _itemsByKey.values
      .where((item) => item.isSelected && !item.isDownloaded)
      .length;

  int _selectedSavedCount() => _itemsByKey.values
      .where((item) => item.isSelected && item.isDownloaded)
      .length;

  int _savedCount() =>
      _itemsByKey.values.where((item) => item.isDownloaded).length;

  int _newCount() => _itemsByKey.length - _savedCount();

  String _saveActionText({
    required int selectedCount,
    required int selectedNewCount,
    required int selectedSavedCount,
  }) {
    if (selectedCount == 0) {
      return 'Select Photos';
    }
    if (selectedSavedCount == 0) {
      return 'Save New ($selectedNewCount)';
    }
    if (selectedNewCount == 0) {
      return 'Redownload ($selectedSavedCount)';
    }
    return 'Save Selected ($selectedCount)';
  }

  String _saveActionDescription({
    required int selectedNewCount,
    required int selectedSavedCount,
  }) {
    if (selectedSavedCount == 0) {
      return '${_photoCount(selectedNewCount)} will be saved. Already saved photos stay untouched.';
    }
    if (selectedNewCount == 0) {
      return '${_alreadySavedPhotoCount(selectedSavedCount)} will be redownloaded.';
    }
    return '${_photoCount(selectedNewCount)} will be saved, and ${_alreadySavedPhotoCount(selectedSavedCount)} will be redownloaded.';
  }

  String _photoCount(int count) => '$count ${count == 1 ? 'photo' : 'photos'}';

  String _alreadySavedPhotoCount(int count) =>
      '$count already saved ${count == 1 ? 'photo' : 'photos'}';

  double _scanProgress() => _expectedPhotoCount > 0
      ? (_loadedPhotoCount / _expectedPhotoCount).clamp(0.0, 1.0)
      : 0;

  Future<Uint8List> _loadThumbnail(Photo photo) {
    final thumbUrl = photo.thumbnailUrl.trim();
    final originalUrl = photo.originalUrl.trim();
    final cacheKey = thumbUrl.isNotEmpty ? thumbUrl : originalUrl;
    final cached = _thumbnailLoads.remove(cacheKey);
    if (cached != null) {
      _thumbnailLoads[cacheKey] = cached;
      return cached;
    }

    late final Future<Uint8List> future;
    future = _fetchThumbnail(photo).catchError((
      Object error,
      StackTrace stackTrace,
    ) {
      if (identical(_thumbnailLoads[cacheKey], future)) {
        _thumbnailLoads.remove(cacheKey);
      }
      Error.throwWithStackTrace(error, stackTrace);
    });
    _thumbnailLoads[cacheKey] = future;
    while (_thumbnailLoads.length > _maxThumbnailLoads) {
      _thumbnailLoads.remove(_thumbnailLoads.keys.first);
    }
    return future;
  }

  Future<Uint8List> _fetchThumbnail(Photo photo) async {
    final thumbUrl = photo.thumbnailUrl.trim();
    final originalUrl = photo.originalUrl.trim();
    final candidates = [
      if (thumbUrl.isNotEmpty) thumbUrl,
      if (originalUrl.isNotEmpty && originalUrl != thumbUrl) originalUrl,
    ];

    Object? lastError;
    for (final url in candidates) {
      try {
        return Uint8List.fromList(await _api.getMediaBytes(url));
      } catch (error) {
        lastError = error;
      }
    }

    throw StateError(
      'Thumbnail request failed: ${lastError ?? 'No media URL'}',
    );
  }

  @override
  Widget build(BuildContext context) {
    final selectedCount = _selectedCount();
    final selectedNewCount = _selectedNewCount();
    final selectedSavedCount = _selectedSavedCount();
    final hasItems = _itemsByKey.isNotEmpty;

    return Scaffold(
      body: SafeArea(
        child: Column(
          children: [
            _AppBar(
              title:
                  _stage == AppStage.gallery || _stage == AppStage.downloading
                  ? 'Timeline Explorer'
                  : 'Procare Photo Downloader',
              subtitle:
                  _stage == AppStage.gallery || _stage == AppStage.downloading
                  ? (_selectedStudent?.fullName ?? '')
                  : _status,
              showBack:
                  _stage == AppStage.gallery || _stage == AppStage.downloading,
              showActions:
                  _stage == AppStage.gallery || _stage == AppStage.downloading,
              actionText:
                  hasItems &&
                      _itemsByKey.values.every((item) => item.isSelected)
                  ? 'Clear'
                  : 'All',
              onBack: () => setState(() {
                _stage = AppStage.selectStudent;
                _selectedStudent = null;
                _itemsByKey.clear();
                _thumbnailLoads.clear();
                _isRefreshingPhotos = false;
              }),
              onSelectAll: _toggleSelectAll,
            ),
            Expanded(child: _buildBody()),
            if (_stage == AppStage.gallery || _stage == AppStage.downloading)
              _BottomActionBar(
                isRefreshing: _isRefreshingPhotos,
                isDownloading: _isDownloading,
                progress: _isDownloading ? _downloadProgress : _scanProgress(),
                progressText: _progressText,
                selectedCount: selectedCount,
                primaryText: _isRefreshingPhotos
                    ? 'Loading...'
                    : _saveActionText(
                        selectedCount: selectedCount,
                        selectedNewCount: selectedNewCount,
                        selectedSavedCount: selectedSavedCount,
                      ),
                onSelectUnsaved: _isRefreshingPhotos ? null : _selectUnsaved,
                onSave:
                    _isRefreshingPhotos || _isDownloading || selectedCount == 0
                    ? null
                    : _reviewExportSettingsAndDownload,
              ),
          ],
        ),
      ),
    );
  }

  Widget _buildBody() {
    return switch (_stage) {
      AppStage.login => WebViewWidget(controller: _webViewController),
      AppStage.loadingStudents => _CenteredStatus(
        title: _status,
        subtitle: _subStatus,
      ),
      AppStage.selectStudent => _StudentList(
        students: _students,
        subtitle: _subStatus,
        onSelect: _selectStudent,
      ),
      AppStage.gallery || AppStage.downloading => _TimelineExplorer(
        groups: _timelineGroups(),
        totalCount: _itemsByKey.length,
        savedCount: _savedCount(),
        newCount: _newCount(),
        selectedCount: _selectedCount(),
        isRefreshing: _isRefreshingPhotos,
        loaded: _loadedPhotoCount,
        expected: _expectedPhotoCount,
        status: _subStatus,
        loadThumbnail: _loadThumbnail,
        onToggleGroup: _toggleGroup,
        onTogglePhoto: (item) =>
            setState(() => item.isSelected = !item.isSelected),
        onOpenPhoto: _openPhotoViewer,
      ),
    };
  }

  Future<void> _openPhotoViewer(PhotoItem item) async {
    final items = _timelineGroups()
        .expand((group) => group.items)
        .toList(growable: false);
    final initialIndex = items.indexWhere(
      (candidate) => candidate.photo.key == item.photo.key,
    );

    await showDialog<void>(
      context: context,
      barrierColor: Colors.black,
      builder: (context) => _PhotoViewerDialog(
        items: items.isEmpty ? [item] : items,
        initialIndex: initialIndex < 0 ? 0 : initialIndex,
        loadOriginal: _api.getMediaBytes,
        onToggleSelected: (item) {
          if (mounted) {
            setState(() => item.isSelected = !item.isSelected);
          }
        },
      ),
    );
  }
}

class _AppBar extends StatelessWidget {
  const _AppBar({
    required this.title,
    required this.subtitle,
    required this.showBack,
    required this.showActions,
    required this.actionText,
    required this.onBack,
    required this.onSelectAll,
  });

  final String title;
  final String subtitle;
  final bool showBack;
  final bool showActions;
  final String actionText;
  final VoidCallback onBack;
  final VoidCallback onSelectAll;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _surface,
        border: Border(bottom: BorderSide(color: _border)),
      ),
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
      child: Row(
        children: [
          if (showBack) _SquareButton(icon: Icons.chevron_left, onTap: onBack),
          if (showBack) const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 20,
                    fontWeight: FontWeight.w800,
                    color: _ink,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  subtitle,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(fontSize: 13, color: _muted),
                ),
              ],
            ),
          ),
          if (showActions) ...[
            const SizedBox(width: 10),
            _TextButton(text: actionText, onTap: onSelectAll),
          ],
        ],
      ),
    );
  }
}

class _TimelineExplorer extends StatelessWidget {
  const _TimelineExplorer({
    required this.groups,
    required this.totalCount,
    required this.savedCount,
    required this.newCount,
    required this.selectedCount,
    required this.isRefreshing,
    required this.loaded,
    required this.expected,
    required this.status,
    required this.loadThumbnail,
    required this.onToggleGroup,
    required this.onTogglePhoto,
    required this.onOpenPhoto,
  });

  final List<TimelineGroup> groups;
  final int totalCount;
  final int savedCount;
  final int newCount;
  final int selectedCount;
  final bool isRefreshing;
  final int loaded;
  final int expected;
  final String status;
  final Future<Uint8List> Function(Photo photo) loadThumbnail;
  final ValueChanged<TimelineGroup> onToggleGroup;
  final ValueChanged<PhotoItem> onTogglePhoto;
  final ValueChanged<PhotoItem> onOpenPhoto;

  @override
  Widget build(BuildContext context) {
    return ListView.builder(
      padding: const EdgeInsets.fromLTRB(14, 12, 14, 14),
      itemCount: groups.length + 1,
      itemBuilder: (context, index) {
        if (index == 0) {
          return Column(
            children: [
              _TimelineSummaryCard(
                totalCount: totalCount,
                savedCount: savedCount,
                newCount: newCount,
                selectedCount: selectedCount,
                isRefreshing: isRefreshing,
                loaded: loaded,
                expected: expected,
                status: status,
              ),
              const SizedBox(height: 12),
            ],
          );
        }

        final group = groups[index - 1];
        return _TimelineGroupCard(
          group: group,
          loadThumbnail: loadThumbnail,
          onToggleGroup: () => onToggleGroup(group),
          onTogglePhoto: onTogglePhoto,
          onOpenPhoto: onOpenPhoto,
        );
      },
    );
  }
}

class _TimelineSummaryCard extends StatelessWidget {
  const _TimelineSummaryCard({
    required this.totalCount,
    required this.savedCount,
    required this.newCount,
    required this.selectedCount,
    required this.isRefreshing,
    required this.loaded,
    required this.expected,
    required this.status,
  });

  final int totalCount;
  final int savedCount;
  final int newCount;
  final int selectedCount;
  final bool isRefreshing;
  final int loaded;
  final int expected;
  final String status;

  @override
  Widget build(BuildContext context) {
    final progress = expected > 0 ? (loaded / expected).clamp(0.0, 1.0) : 0.0;
    return _Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Expanded(
                child: Text(
                  'Timeline Explorer',
                  style: TextStyle(
                    fontSize: 22,
                    fontWeight: FontWeight.w800,
                    color: _ink,
                  ),
                ),
              ),
              if (!isRefreshing)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  decoration: BoxDecoration(
                    color: _primarySoft,
                    borderRadius: BorderRadius.circular(18),
                  ),
                  child: Text(
                    '$selectedCount selected',
                    style: const TextStyle(
                      color: _primary,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 6),
          Text(
            isRefreshing
                ? '$loaded photos found so far. Full saved and new counts appear when the scan finishes.'
                : status,
            style: const TextStyle(color: _muted, fontSize: 13),
          ),
          const SizedBox(height: 14),
          if (isRefreshing) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(999),
              child: LinearProgressIndicator(
                value: expected > 0 ? progress : null,
                minHeight: 8,
                backgroundColor: _border,
                color: _primary,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              expected > 0
                  ? 'Loaded $loaded of $expected photos'
                  : 'Found $loaded photos so far',
              style: const TextStyle(color: _muted, fontSize: 12),
            ),
          ] else
            Row(
              children: [
                _Metric(label: 'Photos', value: totalCount),
                const SizedBox(width: 8),
                _Metric(label: 'Saved', value: savedCount),
                const SizedBox(width: 8),
                _Metric(label: 'New', value: newCount),
              ],
            ),
        ],
      ),
    );
  }
}

class _TimelineGroupCard extends StatelessWidget {
  const _TimelineGroupCard({
    required this.group,
    required this.loadThumbnail,
    required this.onToggleGroup,
    required this.onTogglePhoto,
    required this.onOpenPhoto,
  });

  final TimelineGroup group;
  final Future<Uint8List> Function(Photo photo) loadThumbnail;
  final VoidCallback onToggleGroup;
  final ValueChanged<PhotoItem> onTogglePhoto;
  final ValueChanged<PhotoItem> onOpenPhoto;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Column(
            children: [
              Container(
                width: 12,
                height: 12,
                decoration: const BoxDecoration(
                  color: _primary,
                  shape: BoxShape.circle,
                ),
              ),
              Container(width: 2, height: 148, color: _border),
            ],
          ),
          const SizedBox(width: 10),
          Expanded(
            child: _Card(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              group.label,
                              style: const TextStyle(
                                fontSize: 18,
                                fontWeight: FontWeight.w800,
                                color: _ink,
                              ),
                            ),
                            Text(
                              '${group.items.length} photos | ${group.selectedCount} selected | ${group.savedCount} saved',
                              style: const TextStyle(
                                fontSize: 12,
                                color: _muted,
                              ),
                            ),
                          ],
                        ),
                      ),
                      _TextButton(
                        text: group.allSelected ? 'All' : 'Select',
                        onTap: onToggleGroup,
                        filled: true,
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  SizedBox(
                    height: 92,
                    child: ListView.separated(
                      scrollDirection: Axis.horizontal,
                      itemCount: group.items.length.clamp(0, 12),
                      separatorBuilder: (context, index) =>
                          const SizedBox(width: 8),
                      itemBuilder: (context, index) {
                        final item = group.items[index];
                        return _PhotoThumb(
                          item: item,
                          loadThumbnail: loadThumbnail,
                          onOpen: () => onOpenPhoto(item),
                          onToggleSelected: () => onTogglePhoto(item),
                        );
                      },
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _StudentList extends StatelessWidget {
  const _StudentList({
    required this.students,
    required this.subtitle,
    required this.onSelect,
  });

  final List<Student> students;
  final String subtitle;
  final ValueChanged<Student> onSelect;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(18),
      children: [
        const Text(
          'Choose a student',
          style: TextStyle(
            fontSize: 24,
            fontWeight: FontWeight.w800,
            color: _ink,
          ),
        ),
        const SizedBox(height: 4),
        Text(subtitle, style: const TextStyle(fontSize: 13, color: _muted)),
        const SizedBox(height: 14),
        for (final student in students)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: _Card(
              onTap: () => onSelect(student),
              child: Row(
                children: [
                  _Avatar(url: student.photoUrl, label: student.fullName),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          student.fullName,
                          style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.w800,
                            color: _ink,
                          ),
                        ),
                        const Text(
                          'Open photo timeline',
                          style: TextStyle(fontSize: 12, color: _muted),
                        ),
                      ],
                    ),
                  ),
                  const Icon(Icons.chevron_right, color: _primary),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

class _ExportSetupSheet extends StatefulWidget {
  const _ExportSetupSheet({
    required this.layout,
    required this.destination,
    required this.customDirectoryLabel,
    required this.selectedStudentName,
    required this.onLayoutChanged,
    required this.onDestinationChanged,
    required this.onChooseCustomDirectory,
    required this.onClearHistory,
    required this.onSignOut,
    this.primaryActionText,
    this.primaryActionDescription,
    this.redownloadCount = 0,
  });

  final DownloadLayout layout;
  final DownloadDestination destination;
  final String? customDirectoryLabel;
  final String? selectedStudentName;
  final ValueChanged<DownloadLayout> onLayoutChanged;
  final Future<void> Function(DownloadDestination destination)
  onDestinationChanged;
  final Future<PickedDirectory?> Function() onChooseCustomDirectory;
  final Future<void> Function() onClearHistory;
  final VoidCallback onSignOut;
  final String? primaryActionText;
  final String? primaryActionDescription;
  final int redownloadCount;

  @override
  State<_ExportSetupSheet> createState() => _ExportSetupSheetState();
}

class _ExportSetupSheetState extends State<_ExportSetupSheet> {
  late DownloadLayout _layout = widget.layout;
  late DownloadDestination _destination = widget.destination;
  late String? _customDirectoryLabel = widget.customDirectoryLabel;

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.82,
      minChildSize: 0.55,
      maxChildSize: 0.94,
      builder: (context, controller) {
        return Container(
          decoration: const BoxDecoration(
            color: _surface,
            borderRadius: BorderRadius.vertical(top: Radius.circular(26)),
          ),
          child: ListView(
            controller: controller,
            padding: const EdgeInsets.fromLTRB(18, 16, 18, 26),
            children: [
              Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: const [
                        Text(
                          'Export Setup',
                          style: TextStyle(
                            fontSize: 24,
                            fontWeight: FontWeight.w800,
                            color: _ink,
                          ),
                        ),
                        SizedBox(height: 4),
                        Text(
                          'Review settings before saving',
                          style: TextStyle(fontSize: 12, color: _muted),
                        ),
                      ],
                    ),
                  ),
                  _TextButton(
                    text: widget.primaryActionText == null ? 'Done' : 'Cancel',
                    onTap: () => Navigator.of(context).pop(false),
                  ),
                ],
              ),
              const SizedBox(height: 18),
              _Card(
                color: const Color(0xFFF8FAFC),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Text(
                      'Storage destination',
                      style: TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w800,
                        color: _ink,
                      ),
                    ),
                    const SizedBox(height: 4),
                    const Text(
                      'Choose where saved photos land.',
                      style: TextStyle(fontSize: 12, color: _muted),
                    ),
                    const SizedBox(height: 12),
                    for (final option in _destinationOptions)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 8),
                        child: _DestinationOptionCard(
                          option: option,
                          selected: option.destination == _destination,
                          customDirectoryLabel: _customDirectoryLabel,
                          onTap: () => _selectDestination(option.destination),
                        ),
                      ),
                    FutureBuilder<String>(
                      future: DownloadPathHelper.storageSummary(
                        _destination,
                        customDirectoryLabel: _customDirectoryLabel,
                      ),
                      builder: (context, snapshot) => Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: _blueSoft,
                          border: Border.all(color: _border),
                          borderRadius: BorderRadius.circular(14),
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const Text(
                              'Current storage',
                              style: TextStyle(color: _muted, fontSize: 12),
                            ),
                            const SizedBox(height: 2),
                            Text(
                              snapshot.data ?? '',
                              style: const TextStyle(color: _ink, fontSize: 12),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              _Card(
                color: const Color(0xFFF8FAFC),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Text(
                      'Save layout',
                      style: TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w800,
                        color: _ink,
                      ),
                    ),
                    const SizedBox(height: 4),
                    const Text(
                      'Choose how files are organized before export.',
                      style: TextStyle(fontSize: 12, color: _muted),
                    ),
                    const SizedBox(height: 12),
                    for (final option in _layoutOptions)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 8),
                        child: _LayoutOptionCard(
                          option: option,
                          selected: option.layout == _layout,
                          onTap: () {
                            setState(() => _layout = option.layout);
                            widget.onLayoutChanged(option.layout);
                          },
                        ),
                      ),
                    const SizedBox(height: 6),
                    FutureBuilder<String>(
                      future: DownloadPathHelper.previewPath(
                        _layout,
                        widget.selectedStudentName,
                        destination: _destination,
                        customDirectoryLabel: _customDirectoryLabel,
                      ),
                      builder: (context, snapshot) => Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: _blueSoft,
                          border: Border.all(color: _border),
                          borderRadius: BorderRadius.circular(14),
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const Text(
                              'Preview path',
                              style: TextStyle(color: _muted, fontSize: 12),
                            ),
                            const SizedBox(height: 2),
                            Text(
                              snapshot.data ?? '',
                              style: const TextStyle(color: _ink, fontSize: 12),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              if (widget.primaryActionText != null) ...[
                const SizedBox(height: 12),
                if (widget.primaryActionDescription != null) ...[
                  _SaveIntentNotice(
                    text: widget.primaryActionDescription!,
                    showCameraRollCopyNote:
                        widget.redownloadCount > 0 &&
                        _destination == DownloadDestination.cameraRoll,
                  ),
                  const SizedBox(height: 10),
                ],
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: () => Navigator.of(context).pop(true),
                    icon: const Icon(Icons.download_done_outlined),
                    label: Text(widget.primaryActionText!),
                  ),
                ),
              ],
              const SizedBox(height: 12),
              _Card(
                color: const Color(0xFFF8FAFC),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Text(
                      'Actions',
                      style: TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w800,
                        color: _ink,
                      ),
                    ),
                    const SizedBox(height: 10),
                    SizedBox(
                      width: double.infinity,
                      child: OutlinedButton(
                        onPressed: widget.onClearHistory,
                        child: const Text('Clear Download History'),
                      ),
                    ),
                    const SizedBox(height: 8),
                    SizedBox(
                      width: double.infinity,
                      child: FilledButton(
                        onPressed: widget.onSignOut,
                        child: const Text('Sign Out'),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  Future<void> _selectDestination(DownloadDestination destination) async {
    if (destination == DownloadDestination.customFolder) {
      final picked = await widget.onChooseCustomDirectory();
      if (picked == null || !mounted) {
        return;
      }

      setState(() {
        _destination = DownloadDestination.customFolder;
        _customDirectoryLabel = picked.label;
      });
      return;
    }

    await widget.onDestinationChanged(destination);
    if (!mounted) {
      return;
    }
    setState(() => _destination = destination);
  }
}

class _SaveIntentNotice extends StatelessWidget {
  const _SaveIntentNotice({
    required this.text,
    required this.showCameraRollCopyNote,
  });

  final String text;
  final bool showCameraRollCopyNote;

  @override
  Widget build(BuildContext context) {
    final copyNote = showCameraRollCopyNote
        ? ' Camera Roll redownloads create a fresh copy.'
        : '';

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: _primarySoft,
        border: Border.all(color: _primary.withValues(alpha: 0.22)),
        borderRadius: BorderRadius.circular(14),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.info_outline, color: _primary, size: 20),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              '$text$copyNote',
              style: const TextStyle(
                color: _primary,
                fontSize: 12,
                height: 1.35,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _DestinationOption {
  const _DestinationOption(
    this.destination,
    this.label,
    this.description,
    this.icon,
  );

  final DownloadDestination destination;
  final String label;
  final String description;
  final IconData icon;
}

const _destinationOptions = [
  _DestinationOption(
    DownloadDestination.cameraRoll,
    'Camera Roll',
    'Save into Photos/Gallery so the images are easy to share.',
    Icons.photo_library_outlined,
  ),
  _DestinationOption(
    DownloadDestination.appFolder,
    'App Folder',
    'Private app storage for exports you do not need in Photos.',
    Icons.folder_special_outlined,
  ),
  _DestinationOption(
    DownloadDestination.customFolder,
    'Custom Folder',
    'Pick any folder Android allows this app to write to.',
    Icons.drive_folder_upload_outlined,
  ),
];

class _DestinationOptionCard extends StatelessWidget {
  const _DestinationOptionCard({
    required this.option,
    required this.selected,
    required this.customDirectoryLabel,
    required this.onTap,
  });

  final _DestinationOption option;
  final bool selected;
  final String? customDirectoryLabel;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final description =
        option.destination == DownloadDestination.customFolder &&
            customDirectoryLabel != null &&
            customDirectoryLabel!.trim().isNotEmpty
        ? customDirectoryLabel!
        : option.description;

    return InkWell(
      borderRadius: BorderRadius.circular(14),
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: selected ? _primarySoft : _surface,
          border: Border.all(color: selected ? _primary : _border),
          borderRadius: BorderRadius.circular(14),
        ),
        child: Row(
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: selected ? _primary : const Color(0xFFE8EEF7),
                borderRadius: BorderRadius.circular(12),
              ),
              child: Icon(
                option.icon,
                color: selected ? Colors.white : _primary,
                size: 22,
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    option.label,
                    style: const TextStyle(
                      color: _ink,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    description,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(color: _muted, fontSize: 12),
                  ),
                ],
              ),
            ),
            Icon(
              selected ? Icons.radio_button_checked : Icons.radio_button_off,
              color: selected ? _primary : _muted,
            ),
          ],
        ),
      ),
    );
  }
}

class _LayoutOption {
  const _LayoutOption(this.layout, this.label, this.description);
  final DownloadLayout layout;
  final String label;
  final String description;
}

const _layoutOptions = [
  _LayoutOption(
    DownloadLayout.flat,
    'Single Folder',
    'Fastest, no child or date folders',
  ),
  _LayoutOption(
    DownloadLayout.yearMonth,
    'Year / Month',
    'Simple folders for one or two students',
  ),
  _LayoutOption(
    DownloadLayout.studentYear,
    'Student / Year',
    'Good for organizing by child',
  ),
  _LayoutOption(
    DownloadLayout.studentYearMonth,
    'Student / Year / Month',
    'Best for mixed classrooms and many dates',
  ),
];

class _LayoutOptionCard extends StatelessWidget {
  const _LayoutOptionCard({
    required this.option,
    required this.selected,
    required this.onTap,
  });

  final _LayoutOption option;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      borderRadius: BorderRadius.circular(14),
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: selected ? _primarySoft : _surface,
          border: Border.all(
            color: selected ? _primary : _border,
            width: selected ? 2 : 1,
          ),
          borderRadius: BorderRadius.circular(14),
        ),
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    option.label,
                    style: const TextStyle(
                      fontWeight: FontWeight.w800,
                      color: _ink,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    option.description,
                    style: const TextStyle(fontSize: 12, color: _muted),
                  ),
                ],
              ),
            ),
            if (selected)
              Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 6,
                ),
                decoration: BoxDecoration(
                  color: _primarySoft,
                  borderRadius: BorderRadius.circular(999),
                ),
                child: const Text(
                  'Selected',
                  style: TextStyle(
                    color: _primary,
                    fontWeight: FontWeight.w800,
                    fontSize: 10,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _BottomActionBar extends StatelessWidget {
  const _BottomActionBar({
    required this.isRefreshing,
    required this.isDownloading,
    required this.progress,
    required this.progressText,
    required this.selectedCount,
    required this.primaryText,
    required this.onSelectUnsaved,
    required this.onSave,
  });

  final bool isRefreshing;
  final bool isDownloading;
  final double progress;
  final String progressText;
  final int selectedCount;
  final String primaryText;
  final VoidCallback? onSelectUnsaved;
  final VoidCallback? onSave;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _surface,
        border: Border(top: BorderSide(color: _border)),
      ),
      padding: const EdgeInsets.fromLTRB(14, 10, 14, 14),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (isDownloading) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(999),
              child: LinearProgressIndicator(
                value: progress > 0 ? progress : null,
                minHeight: 6,
                backgroundColor: _border,
                color: _primary,
              ),
            ),
            const SizedBox(height: 8),
          ],
          Text(
            isRefreshing
                ? 'Save actions unlock when the photo scan finishes.'
                : '$selectedCount selected',
            style: const TextStyle(color: _muted, fontSize: 12),
          ),
          if (isDownloading && progressText.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Text(
                progressText,
                style: const TextStyle(color: _muted, fontSize: 12),
              ),
            ),
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed: onSelectUnsaved,
                  child: const Text('Select Unsaved'),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: FilledButton(
                  onPressed: onSave,
                  child: Text(primaryText),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _CenteredStatus extends StatelessWidget {
  const _CenteredStatus({required this.title, required this.subtitle});

  final String title;
  final String subtitle;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 72,
              height: 72,
              decoration: BoxDecoration(
                color: _surface,
                border: Border.all(color: _border),
                borderRadius: BorderRadius.circular(22),
              ),
              child: const Center(
                child: CircularProgressIndicator(color: _primary),
              ),
            ),
            const SizedBox(height: 16),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 18,
                fontWeight: FontWeight.w800,
                color: _ink,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              subtitle,
              textAlign: TextAlign.center,
              style: const TextStyle(color: _muted),
            ),
          ],
        ),
      ),
    );
  }
}

class _PhotoThumb extends StatelessWidget {
  const _PhotoThumb({
    required this.item,
    required this.loadThumbnail,
    required this.onOpen,
    required this.onToggleSelected,
  });

  final PhotoItem item;
  final Future<Uint8List> Function(Photo photo) loadThumbnail;
  final VoidCallback onOpen;
  final VoidCallback onToggleSelected;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onOpen,
      child: Container(
        width: 72,
        decoration: BoxDecoration(
          color: const Color(0xFFE8EEF7),
          border: Border.all(
            color: item.isSelected ? _primary : _border,
            width: item.isSelected ? 3 : 1,
          ),
          borderRadius: BorderRadius.circular(18),
        ),
        clipBehavior: Clip.antiAlias,
        child: Stack(
          fit: StackFit.expand,
          children: [
            FutureBuilder<Uint8List>(
              future: loadThumbnail(item.photo),
              builder: (context, snapshot) {
                if (snapshot.hasData) {
                  return Image.memory(
                    snapshot.data!,
                    fit: BoxFit.cover,
                    gaplessPlayback: true,
                  );
                }

                if (snapshot.hasError) {
                  return const Center(
                    child: Icon(Icons.image_outlined, color: _muted),
                  );
                }

                return const Center(
                  child: SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: _primary,
                    ),
                  ),
                );
              },
            ),
            Positioned(
              left: 4,
              top: 4,
              child: GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: onToggleSelected,
                child: Container(
                  width: 32,
                  height: 32,
                  alignment: Alignment.topLeft,
                  padding: const EdgeInsets.all(4),
                  child: Container(
                    width: 24,
                    height: 24,
                    decoration: BoxDecoration(
                      color: item.isSelected
                          ? _primary
                          : Colors.white.withValues(alpha: 0.86),
                      shape: BoxShape.circle,
                    ),
                    child: Icon(
                      item.isSelected ? Icons.check : Icons.add,
                      color: item.isSelected ? Colors.white : _primary,
                      size: 16,
                    ),
                  ),
                ),
              ),
            ),
            if (item.isDownloaded)
              Positioned(
                right: 4,
                bottom: 4,
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 3,
                  ),
                  decoration: BoxDecoration(
                    color: const Color(0xFF15803D),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Text(
                    'Saved',
                    style: TextStyle(
                      color: Colors.white,
                      fontSize: 9,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _PhotoViewerDialog extends StatefulWidget {
  const _PhotoViewerDialog({
    required this.items,
    required this.initialIndex,
    required this.loadOriginal,
    required this.onToggleSelected,
  });

  final List<PhotoItem> items;
  final int initialIndex;
  final Future<List<int>> Function(String url) loadOriginal;
  final ValueChanged<PhotoItem> onToggleSelected;

  @override
  State<_PhotoViewerDialog> createState() => _PhotoViewerDialogState();
}

class _PhotoViewerDialogState extends State<_PhotoViewerDialog> {
  late final PageController _pageController;
  late int _index;
  final Map<String, Future<List<int>>> _imageLoads = {};

  @override
  void initState() {
    super.initState();
    _index = widget.initialIndex.clamp(0, widget.items.length - 1);
    _pageController = PageController(initialPage: _index);
  }

  @override
  void dispose() {
    _pageController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final item = widget.items[_index];
    final caption = item.photo.caption?.trim();
    final hasCaption = caption != null && caption.isNotEmpty;

    return Dialog.fullscreen(
      backgroundColor: Colors.black,
      child: SafeArea(
        child: Stack(
          children: [
            Positioned.fill(
              child: PageView.builder(
                controller: _pageController,
                itemCount: widget.items.length,
                onPageChanged: (index) => setState(() => _index = index),
                itemBuilder: (context, index) => _PhotoViewerImagePage(
                  item: widget.items[index],
                  imageBytes: _loadImage(widget.items[index]),
                  onReload: () => _reload(widget.items[index]),
                ),
              ),
            ),
            Positioned(
              left: 0,
              top: 0,
              right: 0,
              child: _PhotoViewerTopBar(
                title: _formatPhotoDate(item.photo.createdAt),
                selected: item.isSelected,
                onClose: () => Navigator.of(context).pop(),
                onToggleSelected: _toggleSelected,
              ),
            ),
            if (hasCaption)
              Positioned(
                left: 0,
                right: 0,
                bottom: 0,
                child: _PhotoViewerCaptionBar(caption: caption),
              ),
          ],
        ),
      ),
    );
  }

  void _toggleSelected() {
    widget.onToggleSelected(widget.items[_index]);
    setState(() {});
  }

  Future<List<int>> _loadImage(PhotoItem item) {
    final key = item.photo.originalUrl;
    return _imageLoads.putIfAbsent(key, () => widget.loadOriginal(key));
  }

  void _reload(PhotoItem item) {
    setState(() {
      _imageLoads.remove(item.photo.originalUrl);
    });
  }
}

class _PhotoViewerImagePage extends StatelessWidget {
  const _PhotoViewerImagePage({
    required this.item,
    required this.imageBytes,
    required this.onReload,
  });

  final PhotoItem item;
  final Future<List<int>> imageBytes;
  final VoidCallback onReload;

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<int>>(
      future: imageBytes,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return _PhotoViewerStatus(
            icon: Icons.broken_image_outlined,
            title: 'Photo unavailable',
            action: FilledButton.icon(
              onPressed: onReload,
              icon: const Icon(Icons.refresh),
              label: const Text('Retry'),
            ),
          );
        }

        if (!snapshot.hasData) {
          return const _PhotoViewerStatus(
            icon: Icons.image_outlined,
            title: 'Loading full image',
            action: SizedBox(
              width: 26,
              height: 26,
              child: CircularProgressIndicator(
                color: Colors.white,
                strokeWidth: 2.5,
              ),
            ),
          );
        }

        return InteractiveViewer(
          minScale: 1,
          maxScale: 5,
          child: Image.memory(
            Uint8List.fromList(snapshot.data!),
            width: double.infinity,
            height: double.infinity,
            fit: BoxFit.contain,
            gaplessPlayback: true,
            semanticLabel: _formatPhotoDate(item.photo.createdAt),
          ),
        );
      },
    );
  }
}

class _PhotoViewerTopBar extends StatelessWidget {
  const _PhotoViewerTopBar({
    required this.title,
    required this.selected,
    required this.onClose,
    required this.onToggleSelected,
  });

  final String title;
  final bool selected;
  final VoidCallback onClose;
  final VoidCallback onToggleSelected;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.fromLTRB(8, 8, 12, 44),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [
            Colors.black.withValues(alpha: 0.78),
            Colors.black.withValues(alpha: 0),
          ],
        ),
      ),
      child: Row(
        children: [
          IconButton(
            onPressed: onClose,
            icon: const Icon(Icons.close, color: Colors.white),
            tooltip: 'Close',
          ),
          Expanded(
            child: Text(
              title,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                color: Colors.white,
                fontSize: 16,
                fontWeight: FontWeight.w800,
              ),
            ),
          ),
          const SizedBox(width: 8),
          _PhotoViewerSelectionButton(
            selected: selected,
            onToggleSelected: onToggleSelected,
          ),
        ],
      ),
    );
  }
}

class _PhotoViewerCaptionBar extends StatelessWidget {
  const _PhotoViewerCaptionBar({required this.caption});

  final String caption;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.fromLTRB(16, 52, 16, 16),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [
            Colors.black.withValues(alpha: 0),
            Colors.black.withValues(alpha: 0.82),
          ],
        ),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            caption,
            maxLines: 3,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              color: Colors.white,
              fontSize: 15,
              height: 1.32,
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }
}

class _PhotoViewerSelectionButton extends StatelessWidget {
  const _PhotoViewerSelectionButton({
    required this.selected,
    required this.onToggleSelected,
  });

  final bool selected;
  final VoidCallback onToggleSelected;

  @override
  Widget build(BuildContext context) {
    return FilledButton.icon(
      onPressed: onToggleSelected,
      icon: Icon(selected ? Icons.check_circle : Icons.add_circle_outline),
      label: Text(selected ? 'Selected' : 'Select'),
      style: FilledButton.styleFrom(
        backgroundColor: selected ? _primary : Colors.white,
        foregroundColor: selected ? Colors.white : _ink,
        minimumSize: const Size(118, 44),
      ),
    );
  }
}

class _PhotoViewerStatus extends StatelessWidget {
  const _PhotoViewerStatus({
    required this.icon,
    required this.title,
    required this.action,
  });

  final IconData icon;
  final String title;
  final Widget action;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, color: Colors.white70, size: 42),
            const SizedBox(height: 14),
            Text(
              title,
              textAlign: TextAlign.center,
              style: const TextStyle(
                color: Colors.white,
                fontSize: 18,
                fontWeight: FontWeight.w800,
              ),
            ),
            const SizedBox(height: 18),
            action,
          ],
        ),
      ),
    );
  }
}

String _formatPhotoDate(DateTime date) {
  if (date.millisecondsSinceEpoch == 0) {
    return 'Undated photo';
  }

  final local = date.toLocal();
  const months = [
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  final hour = local.hour % 12 == 0 ? 12 : local.hour % 12;
  final minute = local.minute.toString().padLeft(2, '0');
  final suffix = local.hour >= 12 ? 'PM' : 'AM';
  return '${months[local.month - 1]} ${local.day}, ${local.year} $hour:$minute $suffix';
}

class _Metric extends StatelessWidget {
  const _Metric({required this.label, required this.value});
  final String label;
  final int value;

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 12),
        decoration: BoxDecoration(
          color: const Color(0xFFF8FAFC),
          border: Border.all(color: _border),
          borderRadius: BorderRadius.circular(14),
        ),
        child: Column(
          children: [
            Text(
              '$value',
              style: const TextStyle(
                fontSize: 20,
                fontWeight: FontWeight.w800,
                color: _ink,
              ),
            ),
            Text(label, style: const TextStyle(fontSize: 12, color: _muted)),
          ],
        ),
      ),
    );
  }
}

class _Avatar extends StatelessWidget {
  const _Avatar({required this.url, required this.label});
  final String? url;
  final String label;

  @override
  Widget build(BuildContext context) {
    final initials = label
        .split(RegExp(r'\s+'))
        .where((part) => part.isNotEmpty)
        .take(2)
        .map((part) => part[0])
        .join();
    return Container(
      width: 54,
      height: 54,
      decoration: BoxDecoration(
        color: _primarySoft,
        borderRadius: BorderRadius.circular(18),
      ),
      clipBehavior: Clip.antiAlias,
      child: url == null || url!.isEmpty
          ? Center(
              child: Text(
                initials,
                style: const TextStyle(
                  color: _primary,
                  fontWeight: FontWeight.w800,
                ),
              ),
            )
          : Image.network(
              url!,
              fit: BoxFit.cover,
              errorBuilder: (context, error, stackTrace) =>
                  Center(child: Text(initials)),
            ),
    );
  }
}

class _Card extends StatelessWidget {
  const _Card({required this.child, this.onTap, this.color = _surface});

  final Widget child;
  final VoidCallback? onTap;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: color,
      borderRadius: BorderRadius.circular(20),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(20),
        child: Container(
          width: double.infinity,
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            border: Border.all(color: _border),
            borderRadius: BorderRadius.circular(20),
          ),
          child: child,
        ),
      ),
    );
  }
}

class _SquareButton extends StatelessWidget {
  const _SquareButton({required this.icon, required this.onTap});
  final IconData icon;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(14),
      child: Container(
        width: 44,
        height: 44,
        decoration: BoxDecoration(
          border: Border.all(color: _border),
          borderRadius: BorderRadius.circular(14),
          color: _surface,
        ),
        child: Icon(icon, color: _ink),
      ),
    );
  }
}

class _TextButton extends StatelessWidget {
  const _TextButton({
    required this.text,
    required this.onTap,
    this.filled = false,
  });
  final String text;
  final VoidCallback onTap;
  final bool filled;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(14),
      child: Container(
        height: 44,
        padding: const EdgeInsets.symmetric(horizontal: 14),
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: filled ? _primarySoft : _surface,
          border: Border.all(color: filled ? _primarySoft : _border),
          borderRadius: BorderRadius.circular(14),
        ),
        child: Text(
          text,
          style: TextStyle(
            color: filled ? _primary : _ink,
            fontWeight: filled ? FontWeight.w700 : FontWeight.w500,
          ),
        ),
      ),
    );
  }
}

const _authHookScript = r'''
(function() {
  const key = '__procareDownloaderAuth';
  const looksLikeJwt = (value) => typeof value === 'string' && /^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(value.trim());
  const persistAuth = (accessToken, organizationId, url) => {
    if (!accessToken || typeof accessToken !== 'string') return false;
    const payload = JSON.stringify({
      accessToken: accessToken.trim(),
      organizationId: organizationId || null,
      url: url || location.href,
      capturedAt: new Date().toISOString()
    });
    try { window[key] = payload; } catch {}
    try { sessionStorage.setItem(key, payload); } catch {}
    try { localStorage.setItem(key, payload); } catch {}
    return true;
  };
  const normalizeString = (value, allowOpaque = false) => {
    if (!value || typeof value !== 'string') return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('Bearer ')) return { accessToken: trimmed.slice(7).trim(), organizationId: null };
    if (looksLikeJwt(trimmed)) return { accessToken: trimmed, organizationId: null };
    if (allowOpaque && trimmed.length >= 16) return { accessToken: trimmed, organizationId: null };
    return null;
  };
  const findAuth = (value, depth = 0) => {
    if (depth > 6 || value == null) return null;
    if (typeof value === 'string') {
      const direct = normalizeString(value);
      if (direct) return direct;
      try { return findAuth(JSON.parse(value), depth + 1); } catch { return null; }
    }
    if (Array.isArray(value)) {
      for (const item of value) {
        const found = findAuth(item, depth + 1);
        if (found) return found;
      }
      return null;
    }
    if (typeof value === 'object') {
      const candidates = [
        [value.access_token, true],
        [value.accessToken, true],
        [value.authToken, true],
        [value.auth_token, true],
        [value.token, false]
      ];
      for (const [candidate, allowOpaque] of candidates) {
        const direct = normalizeString(candidate, allowOpaque);
        if (direct) return { accessToken: direct.accessToken, organizationId: value.organization_id || value.organizationId || value.orgId || null };
      }
      for (const nested of Object.values(value)) {
        const found = findAuth(nested, depth + 1);
        if (found) return found;
      }
    }
    return null;
  };
  const readHeaders = (headers) => {
    let authorization = null;
    let organizationId = null;
    if (!headers) return { authorization, organizationId };
    if (headers instanceof Headers) {
      authorization = headers.get('authorization');
      organizationId = headers.get('x-organization-id');
      return { authorization, organizationId };
    }
    if (Array.isArray(headers)) {
      for (const entry of headers) {
        if (!Array.isArray(entry) || entry.length < 2) continue;
        const name = String(entry[0]).toLowerCase();
        if (name === 'authorization') authorization = entry[1];
        if (name === 'x-organization-id') organizationId = entry[1];
      }
      return { authorization, organizationId };
    }
    for (const [name, value] of Object.entries(headers)) {
      const lower = String(name).toLowerCase();
      if (lower === 'authorization') authorization = value;
      if (lower === 'x-organization-id') organizationId = value;
    }
    return { authorization, organizationId };
  };
  const captureAuthHeader = (authorization, organizationId, url) => {
    const normalized = normalizeString(authorization);
    if (!normalized) return false;
    return persistAuth(normalized.accessToken, organizationId || normalized.organizationId, url);
  };
  if (!window.__procareDownloaderHookInstalled) {
    window.__procareDownloaderHookInstalled = true;
    const originalFetch = window.fetch;
    if (typeof originalFetch === 'function') {
      window.fetch = function(input, init) {
        try {
          const requestUrl = typeof input === 'string' ? input : (input && input.url) || location.href;
          const headers = readHeaders((init && init.headers) || (input && input.headers));
          captureAuthHeader(headers.authorization, headers.organizationId, requestUrl);
        } catch {}
        return originalFetch.apply(this, arguments);
      };
    }
    const originalOpen = XMLHttpRequest.prototype.open;
    const originalSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
    XMLHttpRequest.prototype.open = function(method, url) {
      this.__procareDownloaderUrl = url;
      return originalOpen.apply(this, arguments);
    };
    XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
      try {
        const lower = String(name).toLowerCase();
        this.__procareDownloaderHeaders = this.__procareDownloaderHeaders || {};
        this.__procareDownloaderHeaders[lower] = value;
        captureAuthHeader(this.__procareDownloaderHeaders['authorization'], this.__procareDownloaderHeaders['x-organization-id'], this.__procareDownloaderUrl);
      } catch {}
      return originalSetRequestHeader.apply(this, arguments);
    };
  }
  const scanStore = (store) => {
    if (!store) return false;
    for (let i = 0; i < store.length; i++) {
      try {
        const raw = store.getItem(store.key(i));
        const found = findAuth(raw);
        if (found) return persistAuth(found.accessToken, found.organizationId, location.href);
      } catch {}
    }
    return false;
  };
  return scanStore(sessionStorage) || scanStore(localStorage) || 'hooked';
})();
''';

const _captureTokenScript = r'''
(function() {
  const key = '__procareDownloaderAuth';
  const looksLikeJwt = (value) => typeof value === 'string' && /^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(value.trim());
  const normalize = (value, depth = 0, allowOpaque = false) => {
    if (depth > 6 || !value || typeof value !== 'string') return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('Bearer ')) return { accessToken: trimmed.slice(7).trim() };
    if (looksLikeJwt(trimmed)) return { accessToken: trimmed };
    if (allowOpaque && trimmed.length >= 16) return { accessToken: trimmed };
    try { return findAuth(JSON.parse(trimmed), depth + 1); } catch {}
    return null;
  };
  const findAuth = (value, depth = 0) => {
    if (depth > 6 || value == null) return null;
    if (typeof value === 'string') return normalize(value, depth + 1);
    if (Array.isArray(value)) {
      for (const item of value) {
        const found = findAuth(item, depth + 1);
        if (found) return found;
      }
      return null;
    }
    if (typeof value === 'object') {
      const organizationId = value.organization_id || value.organizationId || value.orgId;
      const candidates = [
        [value.access_token, true],
        [value.accessToken, true],
        [value.authToken, true],
        [value.auth_token, true],
        [value.token, false]
      ];
      for (const [candidate, allowOpaque] of candidates) {
        const normalizedAccessToken = typeof candidate === 'string' ? normalize(candidate, depth + 1, allowOpaque) : null;
        if (normalizedAccessToken?.accessToken) return { accessToken: normalizedAccessToken.accessToken, organizationId };
      }
      for (const nested of Object.values(value)) {
        const found = findAuth(nested, depth + 1);
        if (found) return found;
      }
    }
    return null;
  };
  const readStore = (store) => {
    try {
      const captured = findAuth(store.getItem(key));
      if (captured) return captured;
    } catch {}
    const directKeys = [
      ['access_token', true],
      ['accessToken', true],
      ['authToken', true],
      ['auth_token', true],
      ['token', false]
    ];
    for (const [itemKey, allowOpaque] of directKeys) {
      try {
        const found = normalize(store.getItem(itemKey), 0, allowOpaque);
        if (found) return found;
      } catch {}
    }
    for (let i = 0; i < store.length; i++) {
      try {
        const found = normalize(store.getItem(store.key(i)));
        if (found) return found;
      } catch {}
    }
    return null;
  };
  return JSON.stringify(findAuth(window[key]) || readStore(localStorage) || readStore(sessionStorage) || null);
})();
''';
