import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/models/nav_destination.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('the broadcast screen is an admin-only destination', () {
    final d = kNavDestinations.singleWhere((d) => d.key == 'broadcast');
    expect(d.isAdmin, isTrue);
    expect(d.path, '/admin/broadcast');
  });

  test('hosted offers the broadcast screen; a self-hosted server does not', () {
    expect(ServerCapabilities.hosted.hasAdminBroadcast, isTrue);
    expect(const ServerCapabilities(edition: 'selfhost').hasAdminBroadcast, isFalse);
  });
}
