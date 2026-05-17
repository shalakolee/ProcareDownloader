enum AppStage { login, loadingStudents, selectStudent, gallery, downloading }

enum DownloadLayout { flat, student, yearMonth, studentYear, studentYearMonth }

enum DownloadDestination { cameraRoll, appFolder, customFolder }

class Student {
  const Student({
    required this.id,
    required this.firstName,
    required this.lastName,
    this.photoUrl,
  });

  final String id;
  final String firstName;
  final String lastName;
  final String? photoUrl;

  String get fullName => '$firstName $lastName'.trim();
}

class Photo {
  const Photo({
    required this.id,
    required this.thumbnailUrl,
    required this.originalUrl,
    required this.createdAt,
    this.caption,
    this.studentIds = const [],
  });

  final String id;
  final String thumbnailUrl;
  final String originalUrl;
  final DateTime createdAt;
  final String? caption;
  final List<String> studentIds;

  String get key => id.isNotEmpty ? id : originalUrl;

  Map<String, Object?> toJson() => {
    'id': id,
    'thumbnail_url': thumbnailUrl,
    'original_url': originalUrl,
    'created_at': createdAt.toIso8601String(),
    'caption': caption,
    'student_ids': studentIds,
  };

  static Photo? fromJson(Object? value) {
    if (value is! Map) {
      return null;
    }

    final id = '${value['id'] ?? ''}';
    final originalUrl = '${value['original_url'] ?? ''}';
    if (id.isEmpty || originalUrl.isEmpty) {
      return null;
    }

    final createdRaw = '${value['created_at'] ?? ''}';
    return Photo(
      id: id,
      thumbnailUrl: '${value['thumbnail_url'] ?? originalUrl}',
      originalUrl: originalUrl,
      createdAt:
          DateTime.tryParse(createdRaw) ??
          DateTime.fromMillisecondsSinceEpoch(0),
      caption: value['caption'] as String?,
      studentIds:
          (value['student_ids'] as List?)?.map((item) => '$item').toList() ??
          const [],
    );
  }
}

class TokenInfo {
  const TokenInfo({required this.accessToken, this.organizationId});

  final String accessToken;
  final String? organizationId;
}

class PhotoItem {
  PhotoItem({required this.photo, required this.isDownloaded, bool? isSelected})
    : isSelected = isSelected ?? !isDownloaded;

  final Photo photo;
  bool isDownloaded;
  bool isSelected;
}

class PhotoBatch {
  const PhotoBatch({
    required this.photos,
    required this.loaded,
    required this.total,
  });

  final List<Photo> photos;
  final int loaded;
  final int total;
}

class TimelineGroup {
  const TimelineGroup({
    required this.label,
    required this.sortDate,
    required this.items,
  });

  final String label;
  final DateTime sortDate;
  final List<PhotoItem> items;

  int get selectedCount => items.where((item) => item.isSelected).length;
  int get savedCount => items.where((item) => item.isDownloaded).length;
  int get newCount => items.length - savedCount;
  bool get allSelected => items.isNotEmpty && selectedCount == items.length;
}

class DownloadResult {
  const DownloadResult({
    required this.succeeded,
    required this.skipped,
    required this.failed,
    required this.outputSummary,
    this.errors = const [],
  });

  final int succeeded;
  final int skipped;
  final int failed;
  final String outputSummary;
  final List<String> errors;
}
