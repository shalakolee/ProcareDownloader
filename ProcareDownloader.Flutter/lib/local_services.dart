import 'dart:convert';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'models.dart';

class AppSettingsStore {
  static const _layoutKey = 'download_layout';
  static const _destinationKey = 'download_destination';
  static const _customDirectoryUriKey = 'custom_download_directory_uri';
  static const _customDirectoryLabelKey = 'custom_download_directory_label';

  Future<DownloadLayout> loadLayout() async {
    final prefs = await SharedPreferences.getInstance();
    final value = prefs.getString(_layoutKey);
    return DownloadLayout.values.firstWhere(
      (layout) => layout.name == value,
      orElse: () => DownloadLayout.student,
    );
  }

  Future<void> saveLayout(DownloadLayout layout) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_layoutKey, layout.name);
  }

  Future<DownloadDestination> loadDestination() async {
    final prefs = await SharedPreferences.getInstance();
    final value = prefs.getString(_destinationKey);
    return DownloadDestination.values.firstWhere(
      (destination) => destination.name == value,
      orElse: () => DownloadDestination.cameraRoll,
    );
  }

  Future<void> saveDestination(DownloadDestination destination) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_destinationKey, destination.name);
  }

  Future<PickedDirectory?> loadCustomDirectory() async {
    final prefs = await SharedPreferences.getInstance();
    final uri = prefs.getString(_customDirectoryUriKey);
    if (uri == null || uri.isEmpty) {
      return null;
    }

    return PickedDirectory(
      uri: uri,
      label: prefs.getString(_customDirectoryLabelKey) ?? 'Selected folder',
    );
  }

  Future<void> saveCustomDirectory(PickedDirectory directory) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_customDirectoryUriKey, directory.uri);
    await prefs.setString(_customDirectoryLabelKey, directory.label);
  }
}

class PhotoMetadataCache {
  static const freshDuration = Duration(minutes: 15);

  Future<PhotoCacheResult> load(String studentId) async {
    final file = await _cacheFile(studentId);
    if (!await file.exists()) {
      return PhotoCacheResult.miss;
    }

    try {
      final jsonText = await file.readAsString();
      final payload = jsonDecode(jsonText);
      if (payload is! Map) {
        return PhotoCacheResult.miss;
      }

      final savedAt =
          DateTime.tryParse('${payload['savedAtUtc'] ?? ''}') ??
          DateTime.fromMillisecondsSinceEpoch(0);
      final photos =
          (payload['photos'] as List?)
              ?.map(Photo.fromJson)
              .whereType<Photo>()
              .toList() ??
          const <Photo>[];
      final age = DateTime.now().toUtc().difference(savedAt.toUtc());
      return PhotoCacheResult(
        hasCache: true,
        isFresh: age <= freshDuration,
        photos: photos,
        savedAtUtc: savedAt,
      );
    } catch (_) {
      return PhotoCacheResult.miss;
    }
  }

  Future<void> save(String studentId, List<Photo> photos) async {
    final file = await _cacheFile(studentId);
    await file.parent.create(recursive: true);
    final tmp = File('${file.path}.tmp');
    await tmp.writeAsString(
      jsonEncode({
        'studentId': studentId,
        'savedAtUtc': DateTime.now().toUtc().toIso8601String(),
        'photos': photos.map((photo) => photo.toJson()).toList(),
      }),
    );

    if (await file.exists()) {
      await file.delete();
    }
    await tmp.rename(file.path);
  }

  Future<File> _cacheFile(String studentId) async {
    final root = await getApplicationSupportDirectory();
    return File(
      p.join(root.path, 'metadata-cache', '${_safeFileName(studentId)}.json'),
    );
  }
}

class PhotoCacheResult {
  const PhotoCacheResult({
    required this.hasCache,
    required this.isFresh,
    required this.photos,
    required this.savedAtUtc,
  });

  static final miss = PhotoCacheResult(
    hasCache: false,
    isFresh: false,
    photos: const [],
    savedAtUtc: DateTime.fromMillisecondsSinceEpoch(0),
  );

  final bool hasCache;
  final bool isFresh;
  final List<Photo> photos;
  final DateTime savedAtUtc;
}

class DownloadHistoryStore {
  List<DownloadHistoryRecord>? _records;

  Future<bool> isDownloaded(Photo photo) async {
    final records = await _load();
    for (final record in records) {
      if (!_matches(record, photo)) {
        continue;
      }

      if (_isContentDestination(record.destinationPath) ||
          await File(record.destinationPath).exists()) {
        return true;
      }
    }

    return false;
  }

  Future<void> markDownloaded(Photo photo, String destinationPath) async {
    final records = await _load();
    final existingIndex = records.indexWhere(
      (record) =>
          _matches(record, photo) || record.destinationPath == destinationPath,
    );
    final record = DownloadHistoryRecord(
      photoId: photo.id,
      originalUrl: photo.originalUrl,
      destinationPath: destinationPath,
      downloadedAt: DateTime.now(),
    );

    if (existingIndex >= 0) {
      records[existingIndex] = record;
    } else {
      records.add(record);
    }

    await _save(records);
  }

  Future<void> clear() async {
    _records = [];
    await _save(_records!);
  }

  Future<List<DownloadHistoryRecord>> _load() async {
    if (_records != null) {
      return _records!;
    }

    final file = await _historyFile();
    if (!await file.exists()) {
      _records = [];
      return _records!;
    }

    try {
      final jsonText = await file.readAsString();
      final payload = jsonDecode(jsonText);
      _records =
          (payload as List?)
              ?.map(DownloadHistoryRecord.fromJson)
              .whereType<DownloadHistoryRecord>()
              .toList() ??
          [];
    } catch (_) {
      _records = [];
    }

    return _records!;
  }

  Future<void> _save(List<DownloadHistoryRecord> records) async {
    final file = await _historyFile();
    await file.parent.create(recursive: true);
    await file.writeAsString(
      jsonEncode(records.map((record) => record.toJson()).toList()),
    );
  }

  Future<File> _historyFile() async {
    final root = await getApplicationSupportDirectory();
    return File(p.join(root.path, 'download-history.json'));
  }

  static bool _matches(DownloadHistoryRecord record, Photo photo) {
    return (record.originalUrl.isNotEmpty &&
            record.originalUrl == photo.originalUrl) ||
        (record.photoId.isNotEmpty && record.photoId == photo.id);
  }

  static bool _isContentDestination(String path) {
    return path.startsWith('content://') ||
        path.startsWith('gallery://') ||
        path.startsWith('tree://');
  }
}

class DownloadHistoryRecord {
  const DownloadHistoryRecord({
    required this.photoId,
    required this.originalUrl,
    required this.destinationPath,
    required this.downloadedAt,
  });

  final String photoId;
  final String originalUrl;
  final String destinationPath;
  final DateTime downloadedAt;

  Map<String, Object?> toJson() => {
    'photoId': photoId,
    'originalUrl': originalUrl,
    'destinationPath': destinationPath,
    'downloadedAt': downloadedAt.toIso8601String(),
  };

  static DownloadHistoryRecord? fromJson(Object? value) {
    if (value is! Map) {
      return null;
    }

    return DownloadHistoryRecord(
      photoId: '${value['photoId'] ?? ''}',
      originalUrl: '${value['originalUrl'] ?? ''}',
      destinationPath: '${value['destinationPath'] ?? ''}',
      downloadedAt:
          DateTime.tryParse('${value['downloadedAt'] ?? ''}') ??
          DateTime.fromMillisecondsSinceEpoch(0),
    );
  }
}

class DownloadPathHelper {
  static const rootFolderName = 'Procare Photo Downloader';

  static Future<String> rootPath() async {
    final directory = await getApplicationDocumentsDirectory();
    return p.join(directory.path, rootFolderName);
  }

  static Future<String> storageSummary(
    DownloadDestination destination, {
    String? customDirectoryLabel,
  }) async {
    return switch (destination) {
      DownloadDestination.cameraRoll =>
        'Camera Roll / Pictures / $rootFolderName',
      DownloadDestination.customFolder =>
        customDirectoryLabel == null || customDirectoryLabel.trim().isEmpty
            ? 'Custom folder not selected'
            : customDirectoryLabel,
      DownloadDestination.appFolder => await rootPath(),
    };
  }

  static Future<String> previewPath(
    DownloadLayout layout,
    String? studentName, {
    required DownloadDestination destination,
    String? customDirectoryLabel,
  }) async {
    final root = await storageSummary(
      destination,
      customDirectoryLabel: customDirectoryLabel,
    );
    return _displayFolderPath(
      root,
      layout,
      studentName,
      year: 'YYYY',
      month: 'MM',
    );
  }

  static String fileName(Photo photo) {
    final ext = _extension(photo.originalUrl);
    final datePart = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : '${photo.createdAt.year.toString().padLeft(4, '0')}-${photo.createdAt.month.toString().padLeft(2, '0')}-${photo.createdAt.day.toString().padLeft(2, '0')}';
    return '${datePart}_${_safeFileName(photo.id)}$ext';
  }

  static Future<String> folderPath(
    DownloadLayout layout,
    String? studentName,
    Photo photo,
  ) async {
    final year = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.year.toString().padLeft(4, '0');
    final month = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.month.toString().padLeft(2, '0');
    return _folderPath(
      await rootPath(),
      layout,
      studentName,
      year: year,
      month: month,
    );
  }

  static String relativeFolderPath(
    DownloadLayout layout,
    String? studentName,
    Photo photo,
  ) {
    final year = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.year.toString().padLeft(4, '0');
    final month = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.month.toString().padLeft(2, '0');
    return _folderPath('', layout, studentName, year: year, month: month);
  }

  static Future<String> displayFolderPath(
    DownloadDestination destination,
    DownloadLayout layout,
    String? studentName,
    Photo photo, {
    String? customDirectoryLabel,
  }) async {
    final root = await storageSummary(
      destination,
      customDirectoryLabel: customDirectoryLabel,
    );
    final year = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.year.toString().padLeft(4, '0');
    final month = photo.createdAt.millisecondsSinceEpoch == 0
        ? 'unknown'
        : photo.createdAt.month.toString().padLeft(2, '0');
    return _displayFolderPath(
      root,
      layout,
      studentName,
      year: year,
      month: month,
    );
  }

  static String _folderPath(
    String root,
    DownloadLayout layout,
    String? studentName, {
    required String year,
    required String month,
  }) {
    final safeStudentName = _safeFileName(
      (studentName == null || studentName.trim().isEmpty)
          ? 'student'
          : studentName,
    );
    return switch (layout) {
      DownloadLayout.yearMonth => p.join(root, year, month),
      DownloadLayout.student => p.join(root, safeStudentName),
      DownloadLayout.studentYear => p.join(root, safeStudentName, year),
      DownloadLayout.studentYearMonth => p.join(
        root,
        safeStudentName,
        year,
        month,
      ),
      DownloadLayout.flat => root,
    };
  }

  static String _displayFolderPath(
    String root,
    DownloadLayout layout,
    String? studentName, {
    required String year,
    required String month,
  }) {
    return _folderPath(
      root,
      layout,
      studentName,
      year: year,
      month: month,
    ).replaceAll(r'\', '/');
  }

  static String _extension(String url) {
    final uri = Uri.tryParse(url);
    final ext = uri == null ? '' : p.extension(uri.path);
    return ext.isEmpty ? '.jpg' : ext;
  }
}

class PickedDirectory {
  const PickedDirectory({required this.uri, required this.label});

  final String uri;
  final String label;
}

class SavedPhotoDestination {
  const SavedPhotoDestination({required this.path, required this.folderLabel});

  final String path;
  final String folderLabel;
}

class DownloadStorage {
  static const _channel = MethodChannel('procare_downloader/storage');

  static Future<PickedDirectory?> chooseDirectory() async {
    if (!Platform.isAndroid) {
      return null;
    }

    final result = await _channel.invokeMapMethod<String, Object?>(
      'chooseDirectory',
    );
    if (result == null) {
      return null;
    }

    final uri = '${result['uri'] ?? ''}';
    if (uri.isEmpty) {
      return null;
    }

    final label = '${result['label'] ?? 'Selected folder'}';
    return PickedDirectory(uri: uri, label: label);
  }

  static Future<SavedPhotoDestination> savePhoto({
    required DownloadDestination destination,
    required DownloadLayout layout,
    required String? studentName,
    required Photo photo,
    required List<int> bytes,
    String? customDirectoryUri,
    String? customDirectoryLabel,
    bool replaceExisting = false,
  }) async {
    final fileName = DownloadPathHelper.fileName(photo);
    final relativeFolder = DownloadPathHelper.relativeFolderPath(
      layout,
      studentName,
      photo,
    );
    final folderLabel = await DownloadPathHelper.displayFolderPath(
      destination,
      layout,
      studentName,
      photo,
      customDirectoryLabel: customDirectoryLabel,
    );

    return switch (destination) {
      DownloadDestination.cameraRoll => _saveToCameraRoll(
        bytes: bytes,
        fileName: fileName,
        relativeFolder: relativeFolder,
        folderLabel: folderLabel,
      ),
      DownloadDestination.customFolder => _saveToCustomFolder(
        bytes: bytes,
        fileName: fileName,
        relativeFolder: relativeFolder,
        folderLabel: folderLabel,
        treeUri: customDirectoryUri,
      ),
      DownloadDestination.appFolder => _saveToAppFolder(
        bytes: bytes,
        layout: layout,
        studentName: studentName,
        photo: photo,
        fileName: fileName,
        folderLabel: folderLabel,
        replaceExisting: replaceExisting,
      ),
    };
  }

  static Future<SavedPhotoDestination> _saveToAppFolder({
    required List<int> bytes,
    required DownloadLayout layout,
    required String? studentName,
    required Photo photo,
    required String fileName,
    required String folderLabel,
    required bool replaceExisting,
  }) async {
    final folder = await DownloadPathHelper.folderPath(
      layout,
      studentName,
      photo,
    );
    final file = File(p.join(folder, fileName));
    await file.parent.create(recursive: true);
    if (replaceExisting || !await file.exists()) {
      await file.writeAsBytes(bytes);
    }

    return SavedPhotoDestination(path: file.path, folderLabel: folderLabel);
  }

  static Future<SavedPhotoDestination> _saveToCameraRoll({
    required List<int> bytes,
    required String fileName,
    required String relativeFolder,
    required String folderLabel,
  }) async {
    if (!Platform.isAndroid) {
      throw UnsupportedError(
        'Camera Roll saving is only implemented for Android in this build.',
      );
    }

    final path = await _channel.invokeMethod<String>('saveToCameraRoll', {
      'bytes': Uint8List.fromList(bytes),
      'fileName': fileName,
      'relativePath': _joinMediaPath(
        DownloadPathHelper.rootFolderName,
        relativeFolder,
      ),
      'mimeType': _mimeType(fileName),
    });

    return SavedPhotoDestination(
      path: path ?? 'gallery://$fileName',
      folderLabel: folderLabel,
    );
  }

  static Future<SavedPhotoDestination> _saveToCustomFolder({
    required List<int> bytes,
    required String fileName,
    required String relativeFolder,
    required String folderLabel,
    required String? treeUri,
  }) async {
    if (!Platform.isAndroid || treeUri == null || treeUri.isEmpty) {
      throw StateError('Choose a custom folder before saving.');
    }

    final path = await _channel.invokeMethod<String>('saveToTree', {
      'treeUri': treeUri,
      'bytes': Uint8List.fromList(bytes),
      'fileName': fileName,
      'relativePath': relativeFolder,
      'mimeType': _mimeType(fileName),
    });

    return SavedPhotoDestination(
      path: path ?? 'tree://$fileName',
      folderLabel: folderLabel,
    );
  }

  static String _joinMediaPath(String root, String relativeFolder) {
    final parts = [
      root,
      ...relativeFolder
          .split(RegExp(r'[\\/]'))
          .where((part) => part.trim().isNotEmpty),
    ];
    return parts.join('/');
  }

  static String _mimeType(String fileName) {
    final extension = p.extension(fileName).toLowerCase();
    return switch (extension) {
      '.png' => 'image/png',
      '.gif' => 'image/gif',
      '.webp' => 'image/webp',
      '.heic' || '.heif' => 'image/heic',
      '.mp4' => 'video/mp4',
      '.mov' => 'video/quicktime',
      '.m4v' => 'video/x-m4v',
      _ => 'image/jpeg',
    };
  }
}

String _safeFileName(String value) {
  final sanitized = value
      .replaceAll(RegExp(r'[<>:"/\\|?*\x00-\x1F]'), '_')
      .trim();
  return sanitized.isEmpty ? 'unknown' : sanitized;
}
