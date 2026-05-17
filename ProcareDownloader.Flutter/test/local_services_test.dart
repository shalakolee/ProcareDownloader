import 'package:flutter_test/flutter_test.dart';
import 'package:procare_downloader_flutter/local_services.dart';
import 'package:procare_downloader_flutter/models.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  test('download layout defaults to student name folders', () async {
    SharedPreferences.setMockInitialValues({});

    final layout = await AppSettingsStore().loadLayout();

    expect(layout, DownloadLayout.student);
  });

  test('student layout previews one folder per student', () async {
    final preview = await DownloadPathHelper.previewPath(
      DownloadLayout.student,
      'Sample Student',
      destination: DownloadDestination.cameraRoll,
    );

    expect(
      preview,
      'Camera Roll / Pictures / Procare Photo Downloader/Sample Student',
    );
  });
}
