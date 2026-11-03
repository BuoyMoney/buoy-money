import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/notification_service.dart';

void main() {
  group('AppNotification.fromJson', () {
    // Regression for the "notifications won't load on Android" crash: the backend sends
    // RelatedConnectionId as an int? (e.g. 42 on a Plaid connection-error notification), and
    // the parser cast it with a bare `as String?`. Web (dart2js) elided the cast; the Dart VM
    // (this test) and AOT (mobile) enforce it, so `42 as String?` threw a _TypeError and the
    // whole notifications fetch failed. The field stays String?, so the int is stringified.
    test('tolerates an int relatedConnectionId (camelCase)', () {
      final json = <String, dynamic>{
        'id': 1,
        'type': 'plaid_error',
        'title': 'Reconnect your bank',
        'message': 'Please re-authenticate',
        'relatedConnectionId': 42,
        'isRead': false,
        'isArchived': false,
        'createdAt': '2026-07-11T00:00:00Z',
      };

      final n = AppNotification.fromJson(json);

      expect(n.relatedConnectionId, '42');
    });

    test('tolerates an int RelatedConnectionId (PascalCase)', () {
      final json = <String, dynamic>{
        'Id': 2,
        'Type': 'plaid_error',
        'Title': 'Reconnect your bank',
        'Message': 'Please re-authenticate',
        'RelatedConnectionId': 7,
        'IsRead': false,
        'IsArchived': false,
        'CreatedAt': '2026-07-11T00:00:00Z',
      };

      final n = AppNotification.fromJson(json);

      expect(n.relatedConnectionId, '7');
    });

    test('leaves a null relatedConnectionId null', () {
      final json = <String, dynamic>{
        'id': 3,
        'type': 'info',
        'title': 'Hello',
        'message': 'World',
        'relatedConnectionId': null,
        'isRead': false,
        'isArchived': false,
        'createdAt': '2026-07-11T00:00:00Z',
      };

      final n = AppNotification.fromJson(json);

      expect(n.relatedConnectionId, isNull);
    });
  });
}
