import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'models.dart';

typedef BrowserJsonRunner =
    Future<Object?> Function(String javaScriptExpression);

class ProcareApi {
  static const _apiBaseUrl = 'https://api-school.procareconnect.com/api/web';
  static const _parentKidsPath = '/parent/kids';
  static const _parentActivitiesPath = '/parent/activities';
  static const _parentDailyActivitiesPath = '/parent/daily_activities';
  static const _maxPhotoPages = 500;

  final http.Client _http;
  TokenInfo? _token;
  BrowserJsonRunner? _browserJsonRunner;

  ProcareApi({http.Client? client}) : _http = client ?? http.Client();

  bool get hasToken => _token?.accessToken.isNotEmpty == true;

  Map<String, String> mediaHeaders({String accept = '*/*'}) =>
      _headers(accept: accept);

  void attachBrowser(BrowserJsonRunner runner) {
    _browserJsonRunner = runner;
  }

  void setCredentials(TokenInfo token) {
    _token = token;
  }

  void clearCredentials() {
    _token = null;
  }

  Future<List<Student>> getStudents() async {
    await _syncCredentialsFromBrowser();

    final browserStudents = await _getStudentsFromBrowser();
    if (browserStudents != null) {
      return browserStudents;
    }

    if (!hasToken) {
      return const [];
    }

    final response = await _http.get(
      Uri.parse('$_apiBaseUrl$_parentKidsPath'),
      headers: _headers(),
    );

    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw StateError('Student request failed (${response.statusCode}).');
    }

    return parseStudents(jsonDecode(response.body));
  }

  Future<List<Photo>> getPhotos(
    String studentId, {
    void Function(PhotoBatch batch)? onBatch,
  }) async {
    await _syncCredentialsFromBrowser();

    if (!hasToken) {
      final browserPhotos = await _getPhotosFromBrowser(
        studentId,
        onBatch: onBatch,
      );
      if (browserPhotos != null) {
        return browserPhotos;
      }

      onBatch?.call(const PhotoBatch(photos: [], loaded: 0, total: 0));
      return const [];
    }

    final photos = <Photo>[];
    final seen = <String>{};
    var page = 1;
    var total = 0;
    var hadSuccessfulPage = false;

    while (page <= _maxPhotoPages) {
      final pageResult = await _getPhotosPage(studentId, page);
      if (!pageResult.success) {
        break;
      }

      hadSuccessfulPage = true;
      final newPhotos = <Photo>[];
      for (final photo in pageResult.photos) {
        if (seen.add(photo.key.toLowerCase())) {
          newPhotos.add(photo);
        }
      }

      photos.addAll(newPhotos);
      total = pageResult.total > 0 ? pageResult.total : total;

      final filtered = filterPhotosForStudent(studentId, photos);
      final filteredNew = filterPhotosForStudent(studentId, newPhotos);
      final progressTotal = total > 0 ? total : filtered.length;
      if (filteredNew.isNotEmpty) {
        onBatch?.call(
          PhotoBatch(
            photos: filteredNew,
            loaded: filtered.length,
            total: progressTotal,
          ),
        );
      }

      if (pageResult.itemCount == 0 ||
          !pageResult.hasMore ||
          (pageResult.photos.isNotEmpty && newPhotos.isEmpty)) {
        break;
      }

      page++;
    }

    if (!hadSuccessfulPage) {
      final browserPhotos = await _getPhotosFromBrowser(
        studentId,
        onBatch: onBatch,
      );
      if (browserPhotos != null) {
        return browserPhotos;
      }

      onBatch?.call(const PhotoBatch(photos: [], loaded: 0, total: 0));
      return const [];
    }

    final filtered = filterPhotosForStudent(studentId, photos);
    onBatch?.call(
      PhotoBatch(
        photos: const [],
        loaded: filtered.length,
        total: filtered.length,
      ),
    );
    return filtered;
  }

  Future<List<int>> getMediaBytes(String url) async {
    await _syncCredentialsFromBrowser();

    final headerAttempts = <Map<String, String>>[
      _headers(accept: '*/*'),
      if (hasToken) _headers(accept: '*/*', includeAuth: false),
    ];

    for (final headers in headerAttempts) {
      try {
        final response = await _http.get(Uri.parse(url), headers: headers);
        if (response.statusCode >= 200 && response.statusCode < 300) {
          return response.bodyBytes;
        }
      } catch (_) {
        // Try the remaining media paths below.
      }
    }

    final browserBytes = await _getMediaBytesFromBrowser(url);
    if (browserBytes != null) {
      return browserBytes;
    }

    throw StateError('Media request failed.');
  }

  Future<_PhotoPageResult> _getPhotosPage(String studentId, int page) async {
    final failures = <String>[];
    for (final url in _buildParentActivityUrls(studentId, page)) {
      try {
        final response = await _http.get(Uri.parse(url), headers: _headers());
        if (response.statusCode < 200 || response.statusCode >= 300) {
          failures.add('${response.statusCode} $url');
          continue;
        }

        final node = jsonDecode(response.body);
        final photos = parsePhotos(node);
        final itemCount = countItems(node);
        if (itemCount == 0 && photos.isEmpty) {
          failures.add('empty $url');
          continue;
        }

        if (_hasOnlyOtherStudentPhotos(studentId, photos)) {
          failures.add('wrong student $url');
          continue;
        }

        final total = _readInt(node, 'total');
        final perPage = _readInt(
          node,
          'per_page',
          fallback: itemCount > 0 ? itemCount : 50,
        );
        final currentPage = _readInt(node, 'page', fallback: page);
        final hasMore = total > 0
            ? currentPage * perPage < total
            : itemCount >= perPage && perPage > 0;
        return _PhotoPageResult(
          photos: photos,
          total: total,
          itemCount: itemCount,
          hasMore: hasMore,
          success: itemCount > 0 || photos.isNotEmpty,
        );
      } catch (error) {
        failures.add('$error $url');
      }
    }

    return const _PhotoPageResult(
      photos: [],
      total: 0,
      itemCount: 0,
      hasMore: false,
      success: false,
    );
  }

  Future<List<Student>?> _getStudentsFromBrowser() async {
    final node = await _browserJsonRunner?.call('''
(async function() {
  const authPayload = (() => {
    try {
      return JSON.parse(window.__procareDownloaderAuth || sessionStorage.getItem('__procareDownloaderAuth') || localStorage.getItem('__procareDownloaderAuth') || 'null');
    } catch {
      return null;
    }
  })();

  if (window.req?.parentKids) {
    try {
      const result = await window.req.parentKids();
      if (result && (Array.isArray(result.kids) || Array.isArray(result.data) || Object.keys(result).length > 0)) {
        return result;
      }
    } catch {}
  }

  const headers = { Accept: 'application/json' };
  if (authPayload?.accessToken) {
    headers.Authorization = 'Bearer ' + authPayload.accessToken;
  }
  if (authPayload?.organizationId) {
    headers['X-Organization-Id'] = authPayload.organizationId;
  }

  try {
    const response = await fetch(${jsonEncode('$_apiBaseUrl$_parentKidsPath')}, {
      method: 'GET',
      headers,
      credentials: 'include'
    });

    if (!response.ok) {
      return { error: true, status: response.status, body: await response.text() };
    }

    return await response.json();
  } catch (error) {
    return { error: true, status: 0, body: String(error) };
  }
})()
''');

    if (_isErrorNode(node)) {
      return null;
    }

    final students = parseStudents(node);
    if (students.isNotEmpty || _tryGetArray(node) != null) {
      return students;
    }

    return null;
  }

  Future<List<Photo>?> _getPhotosFromBrowser(
    String studentId, {
    void Function(PhotoBatch batch)? onBatch,
  }) async {
    final photos = <Photo>[];
    final seen = <String>{};
    var page = 1;
    var total = 0;
    var hadSuccessfulPage = false;

    while (page <= _maxPhotoPages) {
      final pageResult = await _getPhotosPageFromBrowser(studentId, page);
      if (!pageResult.success) {
        break;
      }

      hadSuccessfulPage = true;
      final newPhotos = <Photo>[];
      for (final photo in pageResult.photos) {
        if (seen.add(photo.key.toLowerCase())) {
          newPhotos.add(photo);
        }
      }

      photos.addAll(newPhotos);
      total = pageResult.total > 0 ? pageResult.total : total;

      final filtered = filterPhotosForStudent(studentId, photos);
      final filteredNew = filterPhotosForStudent(studentId, newPhotos);
      final progressTotal = total > 0 ? total : filtered.length;
      if (filteredNew.isNotEmpty) {
        onBatch?.call(
          PhotoBatch(
            photos: filteredNew,
            loaded: filtered.length,
            total: progressTotal,
          ),
        );
      }

      if (pageResult.itemCount == 0 ||
          !pageResult.hasMore ||
          (pageResult.photos.isNotEmpty && newPhotos.isEmpty)) {
        break;
      }

      page++;
    }

    if (!hadSuccessfulPage) {
      return null;
    }

    final filtered = filterPhotosForStudent(studentId, photos);
    onBatch?.call(
      PhotoBatch(
        photos: const [],
        loaded: filtered.length,
        total: filtered.length,
      ),
    );
    return filtered;
  }

  Future<_PhotoPageResult> _getPhotosPageFromBrowser(
    String studentId,
    int page,
  ) async {
    final urls = _buildParentActivityUrls(studentId, page).toList();
    final node = await _browserJsonRunner?.call('''
(async function() {
  const studentId = ${jsonEncode(studentId)};
  const page = ${jsonEncode(page)};
  const perPage = 50;
  let firstEmptyPayload = null;
  const authPayload = (() => {
    try {
      return JSON.parse(window.__procareDownloaderAuth || sessionStorage.getItem('__procareDownloaderAuth') || localStorage.getItem('__procareDownloaderAuth') || 'null');
    } catch {
      return null;
    }
  })();

  const arrayKeys = ['activities', 'daily_activities', 'photos', 'data', 'items', 'results', 'value'];
  const itemArrays = (payload) => {
    if (!payload || typeof payload !== 'object') return [];
    const arrays = [];
    for (const key of arrayKeys) {
      const value = payload[key];
      if (Array.isArray(value)) {
        arrays.push(value);
      } else if (value && typeof value === 'object') {
        for (const nestedKey of arrayKeys) {
          if (Array.isArray(value[nestedKey])) arrays.push(value[nestedKey]);
        }
      }
    }
    return arrays;
  };

  const containsStudentId = (value, depth = 0) => {
    if (value == null || depth > 8) return false;
    if (Array.isArray(value)) {
      return value.some((item) => containsStudentId(item, depth + 1));
    }

    if (typeof value !== 'object') {
      return String(value) === studentId;
    }

    for (const [key, child] of Object.entries(value)) {
      const lowerKey = key.toLowerCase();
      const isStudentKey = lowerKey.includes('kid')
        || lowerKey.includes('student')
        || lowerKey.includes('participant')
        || lowerKey === 'relationships';
      if (isStudentKey && containsStudentId(child, depth + 1)) {
        return true;
      }
    }

    return false;
  };

  const acceptablePayload = (payload) => {
    const arrays = itemArrays(payload);
    if (arrays.length === 0) return null;

    if (!arrays.some((items) => items.length > 0)) {
      if (firstEmptyPayload === null) firstEmptyPayload = payload;
      return null;
    }

    return containsStudentId(payload) ? payload : null;
  };

  const attempts = [
    {
      fn: window.req?.kidActivitiesV2,
      params: [
        { kid_id: studentId, page, per_page: perPage },
        { kid_ids: [studentId], page, per_page: perPage },
        { kid_id: studentId, kid_ids: [studentId], page, per_page: perPage }
      ]
    },
    {
      fn: window.req?.kidActivities,
      params: [
        { kid_id: studentId, page, per_page: perPage },
        { kid_ids: [studentId], page, per_page: perPage },
        { kid_id: studentId, kid_ids: [studentId], page, per_page: perPage }
      ]
    }
  ];

  for (const attempt of attempts) {
    if (typeof attempt.fn !== 'function') continue;
    for (const params of attempt.params) {
      try {
        const result = await attempt.fn(params);
        const accepted = acceptablePayload(result);
        if (accepted) return accepted;
      } catch {}
    }
  }

  const headers = { Accept: 'application/json' };
  if (authPayload?.accessToken) {
    headers.Authorization = 'Bearer ' + authPayload.accessToken;
  }
  if (authPayload?.organizationId) {
    headers['X-Organization-Id'] = authPayload.organizationId;
  }

  const urls = ${jsonEncode(urls)};
  for (const url of urls) {
    try {
      const response = await fetch(url, {
        method: 'GET',
        headers,
        credentials: 'include'
      });

      if (!response.ok) {
        continue;
      }

      const payload = await response.json();
      const accepted = acceptablePayload(payload);
      if (accepted) return accepted;
    } catch {}
  }

  if (firstEmptyPayload) {
    return firstEmptyPayload;
  }

  return { error: true, status: 0, body: 'No browser activity request succeeded.' };
})()
''');

    if (_isErrorNode(node)) {
      return const _PhotoPageResult(
        photos: [],
        total: 0,
        itemCount: 0,
        hasMore: false,
        success: false,
      );
    }

    final photos = parsePhotos(node);
    final itemCount = countItems(node);
    final total = _readInt(node, 'total');
    final perPage = _readInt(
      node,
      'per_page',
      fallback: itemCount > 0 ? itemCount : 50,
    );
    final currentPage = _readInt(node, 'page', fallback: page);
    final hasMore = total > 0
        ? currentPage * perPage < total
        : itemCount >= perPage && perPage > 0;
    return _PhotoPageResult(
      photos: photos,
      total: total,
      itemCount: itemCount,
      hasMore: hasMore,
      success: itemCount > 0 || photos.isNotEmpty,
    );
  }

  Future<List<int>?> _getMediaBytesFromBrowser(String url) async {
    final node = await _browserJsonRunner?.call('''
(async function() {
  const authPayload = (() => {
    try {
      return JSON.parse(window.__procareDownloaderAuth || sessionStorage.getItem('__procareDownloaderAuth') || localStorage.getItem('__procareDownloaderAuth') || 'null');
    } catch {
      return null;
    }
  })();

  const headers = { Accept: '*/*' };
  if (authPayload?.accessToken) {
    headers.Authorization = 'Bearer ' + authPayload.accessToken;
  }
  if (authPayload?.organizationId) {
    headers['X-Organization-Id'] = authPayload.organizationId;
  }

  try {
    const response = await fetch(${jsonEncode(url)}, {
      method: 'GET',
      headers,
      credentials: 'include'
    });

    if (!response.ok) {
      return { error: true, status: response.status, body: await response.text() };
    }

    const bytes = new Uint8Array(await response.arrayBuffer());
    let binary = '';
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
    }

    return { dataBase64: btoa(binary) };
  } catch (error) {
    return { error: true, status: 0, body: String(error) };
  }
})()
''');

    if (_isErrorNode(node) || node is! Map) {
      return null;
    }

    final dataBase64 = node['dataBase64'];
    if (dataBase64 is! String || dataBase64.isEmpty) {
      return null;
    }

    return base64Decode(dataBase64);
  }

  Map<String, String> _headers({
    String accept = 'application/json',
    bool includeAuth = true,
  }) {
    final headers = <String, String>{
      'Accept': accept,
      'Origin': 'https://schools.procareconnect.com',
      'Referer': 'https://schools.procareconnect.com/',
      'User-Agent':
          'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36',
    };

    final token = _token;
    if (includeAuth && token != null) {
      headers['Authorization'] = 'Bearer ${token.accessToken}';
      final orgId = token.organizationId;
      if (orgId != null && orgId.isNotEmpty) {
        headers['X-Organization-Id'] = orgId;
      }
    }

    return headers;
  }

  Future<void> _syncCredentialsFromBrowser() async {
    if (hasToken || _browserJsonRunner == null) {
      return;
    }

    try {
      final node = await _browserJsonRunner?.call(r'''
(function() {
  const key = '__procareDownloaderAuth';
  const looksLikeJwt = (value) => typeof value === 'string' && /^[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+$/.test(value.trim());
  const normalize = (value, depth = 0, allowOpaque = false) => {
    if (depth > 6 || !value || typeof value !== 'string') return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    if (trimmed.startsWith('Bearer ')) return { accessToken: trimmed.slice(7).trim(), organizationId: null };
    if (looksLikeJwt(trimmed)) return { accessToken: trimmed, organizationId: null };
    if (allowOpaque && trimmed.length >= 16) return { accessToken: trimmed, organizationId: null };
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
      const organizationId = value.organization_id || value.organizationId || value.orgId || null;
      const candidates = [
        [value.access_token, true],
        [value.accessToken, true],
        [value.authToken, true],
        [value.auth_token, true],
        [value.token, false]
      ];
      for (const [candidate, allowOpaque] of candidates) {
        const normalizedAccessToken = typeof candidate === 'string' ? normalize(candidate, depth + 1, allowOpaque) : null;
        if (normalizedAccessToken?.accessToken) {
          return { accessToken: normalizedAccessToken.accessToken, organizationId };
        }
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
  return findAuth(window[key]) || readStore(localStorage) || readStore(sessionStorage) || null;
})()
''');
      final token = _tokenFromBrowserNode(node);
      if (token != null) {
        setCredentials(token);
      }
    } catch (_) {
      // Browser credential sync is opportunistic; callers keep their normal fallback behavior.
    }
  }

  TokenInfo? _tokenFromBrowserNode(Object? node) {
    if (node is! Map) {
      return null;
    }

    final accessToken = '${node['accessToken'] ?? node['access_token'] ?? ''}'
        .trim();
    if (accessToken.isEmpty) {
      return null;
    }

    final organizationId =
        '${node['organizationId'] ?? node['organization_id'] ?? node['orgId'] ?? ''}'
            .trim();
    return TokenInfo(
      accessToken: accessToken,
      organizationId: organizationId.isEmpty ? null : organizationId,
    );
  }

  static Iterable<String> _buildParentActivityUrls(
    String studentId,
    int page,
  ) sync* {
    yield _buildParentActivityUrl(
      _parentActivitiesPath,
      studentId,
      page,
      false,
    );
    yield _buildParentActivityUrl(_parentActivitiesPath, studentId, page, true);
    yield _buildParentActivityUrl(_parentActivitiesPath, studentId, page, null);
    yield _buildParentActivityUrl(
      _parentDailyActivitiesPath,
      studentId,
      page,
      false,
    );
    yield _buildParentActivityUrl(
      _parentDailyActivitiesPath,
      studentId,
      page,
      true,
    );
    yield _buildParentActivityUrl(
      _parentDailyActivitiesPath,
      studentId,
      page,
      null,
    );
  }

  static String _buildParentActivityUrl(
    String path,
    String studentId,
    int page,
    bool? useKidIdsArray,
  ) {
    final baseUrl = '$_apiBaseUrl$path/?page=$page&per_page=50';
    if (useKidIdsArray == null) {
      return baseUrl;
    }

    final filterKey = useKidIdsArray
        ? Uri.encodeQueryComponent('kid_ids[]')
        : 'kid_id';
    return '$baseUrl&$filterKey=${Uri.encodeQueryComponent(studentId)}';
  }
}

List<Student> parseStudents(Object? node) {
  final items = _tryGetArray(node);
  if (items == null) {
    return const [];
  }

  return items
      .map((item) {
        final source = item is Map && item['attributes'] is Map
            ? item['attributes'] as Map
            : item;
        final fullName =
            _firstValue(source, const ['full_name', 'fullName', 'name']) ?? '';
        final parts = fullName
            .split(RegExp(r'\s+'))
            .where((part) => part.isNotEmpty)
            .toList();
        return Student(
          id:
              _firstValue(source, const [
                'id',
                'student_id',
                'studentId',
                'kid_id',
                'kidId',
              ]) ??
              (item is Map ? '${item['id'] ?? ''}' : ''),
          firstName:
              _firstValue(source, const ['first_name', 'firstName']) ??
              (parts.isNotEmpty ? parts.first : ''),
          lastName:
              _firstValue(source, const ['last_name', 'lastName']) ??
              (parts.length > 1 ? parts.skip(1).join(' ') : ''),
          photoUrl: _findStudentPhotoUrl(item, source),
        );
      })
      .where((student) => student.id.isNotEmpty)
      .toList();
}

List<Photo> parsePhotos(Object? node) {
  final items = _tryGetArray(node);
  if (items == null) {
    return const [];
  }

  final seenUrls = <String>{};
  final photos = <Photo>[];
  for (final item in items) {
    final photo = _buildPhotoFromItem(item);
    if (photo == null) {
      continue;
    }

    if (seenUrls.add(photo.originalUrl.toLowerCase())) {
      photos.add(photo);
    }
  }

  return photos;
}

List<Photo> filterPhotosForStudent(String studentId, Iterable<Photo> photos) {
  return photos.where((photo) {
    if (photo.studentIds.isEmpty) {
      return true;
    }

    return photo.studentIds.any(
      (id) => id.toLowerCase() == studentId.toLowerCase(),
    );
  }).toList();
}

bool _hasOnlyOtherStudentPhotos(String studentId, List<Photo> photos) {
  if (photos.isEmpty) {
    return false;
  }

  final filtered = filterPhotosForStudent(studentId, photos);
  return filtered.isEmpty &&
      photos.every((photo) => photo.studentIds.isNotEmpty);
}

int countItems(Object? node) => _findArray(node)?.length ?? 0;

bool _isErrorNode(Object? node) {
  if (node == null) {
    return true;
  }

  if (node is Map && node['error'] == true) {
    return true;
  }

  return false;
}

Photo? _buildPhotoFromItem(Object? item) {
  if (item is! Map) {
    return null;
  }

  final source = item['attributes'] is Map ? item['attributes'] as Map : item;
  final mediaUrls = _collectMediaUrls(item).toList();
  if (mediaUrls.isEmpty) {
    return null;
  }

  final activityType =
      (_firstValue(source, const ['activity_type', 'activityType', 'type']) ??
              '')
          .toLowerCase();
  final hasMediaHint = mediaUrls.any((url) {
    final path = url.path.toLowerCase();
    return path.contains('photo') ||
        path.contains('image') ||
        path.contains('video') ||
        path.contains('media') ||
        path.contains('attachment');
  });

  if (!hasMediaHint &&
      activityType.isNotEmpty &&
      !activityType.contains('photo') &&
      !activityType.contains('video') &&
      !activityType.contains('image')) {
    return null;
  }

  final originalUrl = _selectBestUrl(mediaUrls, preferLarge: true);
  if (originalUrl == null || originalUrl.isEmpty) {
    return null;
  }

  final thumbnailUrl =
      _selectBestUrl(mediaUrls, preferLarge: false) ?? originalUrl;
  final createdAt =
      DateTime.tryParse(
        _firstValue(source, const [
              'created_at',
              'createdAt',
              'activity_date',
              'activity_time',
              'date',
            ]) ??
            '',
      ) ??
      DateTime.fromMillisecondsSinceEpoch(0);
  var id =
      _firstValue(source, const [
        'id',
        'photo_id',
        'photoId',
        'activity_id',
        'activityId',
      ]) ??
      '${item['id'] ?? ''}';
  if (id.isEmpty) {
    id = _createUrlId(originalUrl);
  }

  return Photo(
    id: id,
    thumbnailUrl: thumbnailUrl,
    originalUrl: originalUrl,
    createdAt: createdAt,
    caption: _firstValue(source, const [
      'caption',
      'description',
      'notes',
      'title',
    ]),
    studentIds: _extractStudentIds(item, source),
  );
}

String? _firstValue(Object? node, List<String> keys) {
  if (node is! Map) {
    return null;
  }

  for (final key in keys) {
    final value = node[key];
    if (value != null && '$value'.trim().isNotEmpty) {
      return '$value';
    }
  }

  return null;
}

String? _findStudentPhotoUrl(Object? item, Object? source) {
  const keys = [
    'photo_url',
    'photoUrl',
    'profile_photo_url',
    'profilePhotoUrl',
    'profile_image_url',
    'profileImageUrl',
    'avatar_url',
    'avatarUrl',
    'image_url',
    'imageUrl',
    'picture_url',
    'pictureUrl',
    'thumbnail_url',
    'thumbnailUrl',
  ];

  return _firstValue(source, keys) ??
      _firstValue(item, keys) ??
      _findNestedStudentImageUrl(source) ??
      _findNestedStudentImageUrl(item);
}

String? _findNestedStudentImageUrl(
  Object? node, [
  String path = '',
  int depth = 0,
]) {
  if (depth > 5) {
    return null;
  }

  if (node is Map) {
    for (final entry in node.entries) {
      final childPath = path.isEmpty ? '${entry.key}' : '$path.${entry.key}';
      final value = entry.value;
      if (value is String && _isStudentImageUrl(childPath, value)) {
        return value;
      }

      final nested = _findNestedStudentImageUrl(value, childPath, depth + 1);
      if (nested != null) {
        return nested;
      }
    }
  } else if (node is List) {
    for (var i = 0; i < node.length; i++) {
      final nested = _findNestedStudentImageUrl(
        node[i],
        '$path[$i]',
        depth + 1,
      );
      if (nested != null) {
        return nested;
      }
    }
  }

  return null;
}

bool _isStudentImageUrl(String path, String value) {
  final uri = Uri.tryParse(value);
  if (uri == null ||
      !uri.isAbsolute ||
      !(uri.scheme == 'http' || uri.scheme == 'https')) {
    return false;
  }

  final lowerPath = path.toLowerCase();
  return lowerPath.contains('avatar') ||
      lowerPath.contains('profile') ||
      lowerPath.contains('photo') ||
      lowerPath.contains('image') ||
      lowerPath.contains('picture') ||
      lowerPath.contains('thumbnail');
}

List? _tryGetArray(Object? node) {
  if (node is List) {
    return node;
  }

  if (node is Map) {
    for (final key in const [
      'data',
      'students',
      'photos',
      'results',
      'items',
      'value',
      'kids',
      'activities',
      'daily_activities',
    ]) {
      final value = node[key];
      if (value is List) {
        return value;
      }

      if (value is Map) {
        for (final nestedKey in const [
          'data',
          'students',
          'photos',
          'results',
          'items',
          'value',
          'kids',
          'activities',
          'daily_activities',
        ]) {
          final nestedValue = value[nestedKey];
          if (nestedValue is List) {
            return nestedValue;
          }
        }
      }
    }
  }

  return null;
}

List? _findArray(Object? node) => _tryGetArray(node);

List<String> _extractStudentIds(Map item, Object? source) {
  final ids = <String>{};
  void add(Object? value) {
    if (value != null && '$value'.trim().isNotEmpty) {
      ids.add('$value');
    }
  }

  void addArray(Object? value) {
    if (value is List) {
      for (final item in value) {
        if (item is Map) {
          add(item['id'] ?? item['kid_id'] ?? item['student_id']);
        } else {
          add(item);
        }
      }
    }
  }

  if (source is Map) {
    add(source['kid_id']);
    add(source['kidId']);
    add(source['student_id']);
    add(source['studentId']);
    addArray(source['kid_ids']);
    addArray(source['kidIds']);
    addArray(source['activity_participants']);
    addArray(source['activityParticipants']);
  }

  final relationships = item['relationships'];
  if (relationships is Map) {
    final kids = relationships['kids'];
    if (kids is Map) {
      final data = kids['data'];
      if (data is List) {
        for (final kid in data) {
          if (kid is Map) {
            add(kid['id']);
          }
        }
      }
    }
  }

  return ids.toList();
}

Iterable<Uri> _collectMediaUrls(
  Object? node, [
  String path = r'$',
  int depth = 0,
]) sync* {
  if (depth > 8 || node == null) {
    return;
  }

  if (node is Map) {
    for (final entry in node.entries) {
      final childPath = '$path.${entry.key}';
      final value = entry.value;
      if (value is String) {
        final uri = Uri.tryParse(value);
        if (uri != null &&
            (uri.scheme == 'http' || uri.scheme == 'https') &&
            _looksLikeMediaUrl(childPath, uri)) {
          yield uri;
        }
      }

      yield* _collectMediaUrls(value, childPath, depth + 1);
    }
  } else if (node is List) {
    for (var i = 0; i < node.length; i++) {
      yield* _collectMediaUrls(node[i], '$path[$i]', depth + 1);
    }
  }
}

bool _looksLikeMediaUrl(String path, Uri uri) {
  final lowerPath = path.toLowerCase();
  final lowerUrlPath = uri.path.toLowerCase();
  return lowerPath.contains('url') &&
      (lowerPath.contains('photo') ||
          lowerPath.contains('image') ||
          lowerPath.contains('media') ||
          lowerPath.contains('attachment') ||
          lowerPath.contains('video') ||
          RegExp(
            r'\.(jpg|jpeg|png|webp|heic|mp4|mov)$',
          ).hasMatch(lowerUrlPath));
}

String? _selectBestUrl(List<Uri> urls, {required bool preferLarge}) {
  if (urls.isEmpty) {
    return null;
  }

  int score(Uri uri) {
    final text = uri.toString().toLowerCase();
    var score = 0;
    if (text.contains('original')) score += 50;
    if (text.contains('large')) score += 40;
    if (text.contains('full')) score += 40;
    if (text.contains('medium')) score += 20;
    if (text.contains('thumb')) score -= 30;
    if (text.contains('small')) score -= 20;
    return preferLarge ? score : -score;
  }

  final copy = [...urls]
    ..sort((left, right) => score(right).compareTo(score(left)));
  return copy.first.toString();
}

String _createUrlId(String url) {
  final bytes = utf8.encode(url);
  var hash = 2166136261;
  for (final byte in bytes) {
    hash ^= byte;
    hash = (hash * 16777619) & 0xffffffff;
  }

  return hash.toRadixString(16);
}

int _readInt(Object? node, String key, {int fallback = 0}) {
  if (node is! Map) {
    return fallback;
  }

  final value = node[key];
  if (value is int) {
    return value;
  }

  if (value is num) {
    return value.toInt();
  }

  return int.tryParse('$value') ?? fallback;
}

class _PhotoPageResult {
  const _PhotoPageResult({
    required this.photos,
    required this.total,
    required this.itemCount,
    required this.hasMore,
    required this.success,
  });

  final List<Photo> photos;
  final int total;
  final int itemCount;
  final bool hasMore;
  final bool success;
}
