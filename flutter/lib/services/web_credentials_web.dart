import 'package:dio/dio.dart';
import 'package:dio/browser.dart';

/// On web, send cookies (the HttpOnly `refresh_token`) with cross-origin API
/// requests so the refresh endpoint can read it. The refresh token is never
/// exposed to JavaScript — the browser attaches the cookie automatically.
void configureWebCredentials(Dio dio) {
  final adapter = dio.httpClientAdapter;
  if (adapter is BrowserHttpClientAdapter) {
    adapter.withCredentials = true;
  }
}
