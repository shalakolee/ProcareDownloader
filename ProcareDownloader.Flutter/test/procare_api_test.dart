import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:procare_downloader_flutter/models.dart';
import 'package:procare_downloader_flutter/procare_api.dart';

void main() {
  test(
    'getPhotos keeps trying when an activity response is for another child',
    () async {
      const targetStudentId = 'student-target';
      const otherStudentId = 'student-other';
      var requestCount = 0;

      final api = ProcareApi(
        client: MockClient((request) async {
          requestCount++;
          final body = requestCount == 1
              ? _activityPayload(otherStudentId, 'wrong-child-photo')
              : _activityPayload(targetStudentId, 'target-child-photo');

          return http.Response(
            jsonEncode(body),
            200,
            headers: {'content-type': 'application/json'},
          );
        }),
      )..setCredentials(const TokenInfo(accessToken: 'token'));

      final photos = await api.getPhotos(targetStudentId);

      expect(photos, hasLength(1));
      expect(photos.single.id, 'target-child-photo');
      expect(photos.single.studentIds, contains(targetStudentId));
      expect(requestCount, 2);
    },
  );
}

Map<String, Object?> _activityPayload(String studentId, String photoId) => {
  'activities': [
    {
      'id': photoId,
      'kid_id': studentId,
      'activity_type': 'photo',
      'created_at': '2026-05-13T00:00:00Z',
      'photo_url': 'https://cdn.example.com/photos/$photoId.jpg',
    },
  ],
  'total': 1,
  'per_page': 50,
  'page': 1,
};
