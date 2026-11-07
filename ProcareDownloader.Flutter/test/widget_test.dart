import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:procare_downloader_flutter/main.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:webview_flutter/webview_flutter.dart';
import 'package:webview_flutter_platform_interface/webview_flutter_platform_interface.dart';

void main() {
  testWidgets('session checks leave the sign-in WebView mounted', (
    tester,
  ) async {
    SharedPreferences.setMockInitialValues({});
    final platform = _TestWebViewPlatform();
    WebViewPlatform.instance = platform;
    await tester.pumpWidget(const ProcareDownloaderApp());

    final webView = find.byType(WebViewWidget);
    final originalElement = tester.element(webView);
    await tester.pump(const Duration(seconds: 2));
    await tester.pump(const Duration(seconds: 2));
    expect(platform.controller.studentRequests, 0);

    platform.controller.url = 'https://schools.procareconnect.com/';
    await tester.pump(const Duration(seconds: 2));
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));
    expect(platform.controller.studentRequests, 1);
    expect(tester.element(webView), same(originalElement));

    platform.controller.authenticated = true;
    await tester.pump(const Duration(seconds: 2));
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump(const Duration(milliseconds: 200));
    await tester.pump();
    expect(
      find.text('No students are linked to this Procare account.'),
      findsOneWidget,
    );
    expect(platform.controller.navigations, 1);
    expect(
      tester.element(find.byType(WebViewWidget, skipOffstage: false)),
      same(originalElement),
    );
  });
}

class _TestWebViewPlatform extends WebViewPlatform {
  final controller = _TestWebViewController();

  @override
  PlatformWebViewController createPlatformWebViewController(
    PlatformWebViewControllerCreationParams params,
  ) => controller;

  @override
  PlatformWebViewCookieManager createPlatformCookieManager(
    PlatformWebViewCookieManagerCreationParams params,
  ) => _TestCookieManager(params);

  @override
  PlatformNavigationDelegate createPlatformNavigationDelegate(
    PlatformNavigationDelegateCreationParams params,
  ) => _TestNavigationDelegate(params);

  @override
  PlatformWebViewWidget createPlatformWebViewWidget(
    PlatformWebViewWidgetCreationParams params,
  ) => _TestWebViewWidget(params);
}

class _TestWebViewController extends PlatformWebViewController {
  _TestWebViewController()
    : super.implementation(const PlatformWebViewControllerCreationParams());

  String? url;
  var authenticated = false;
  var navigations = 0;
  var studentRequests = 0;
  var _result = '';

  @override
  Future<void> loadRequest(LoadRequestParams params) async {
    navigations++;
    url = params.uri.toString();
  }

  @override
  Future<String?> currentUrl() async => url;

  @override
  Future<void> setJavaScriptMode(JavaScriptMode javaScriptMode) async {}

  @override
  Future<void> setPlatformNavigationDelegate(
    PlatformNavigationDelegate delegate,
  ) async {}

  @override
  Future<void> runJavaScript(String javaScript) async {}

  @override
  Future<Object> runJavaScriptReturningResult(String javaScript) async {
    if (!javaScript.contains('Promise.resolve()')) {
      return _result;
    }

    if (javaScript.contains('/parent/kids')) {
      studentRequests++;
      expect(javaScript, isNot(contains('window.req')));
      _result = authenticated
          ? '{"ok":true,"value":{"kids":[]}}'
          : '{"ok":true,"value":{"error":true,"status":401}}';
    } else {
      _result = '{"ok":true,"value":null}';
    }
    return true;
  }
}

class _TestNavigationDelegate extends PlatformNavigationDelegate {
  _TestNavigationDelegate(super.params) : super.implementation();

  @override
  Future<void> setOnPageFinished(PageEventCallback onPageFinished) async {}
}

class _TestCookieManager extends PlatformWebViewCookieManager {
  _TestCookieManager(super.params) : super.implementation();
}

class _TestWebViewWidget extends PlatformWebViewWidget {
  _TestWebViewWidget(super.params) : super.implementation();

  @override
  Widget build(BuildContext context) => const SizedBox.expand();
}
