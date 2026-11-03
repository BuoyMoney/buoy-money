import 'package:dio/dio.dart';

/// No-op on non-web platforms: native clients carry the refresh token in the
/// request body, not a cookie, so cross-origin credentials aren't needed.
void configureWebCredentials(Dio dio) {}
