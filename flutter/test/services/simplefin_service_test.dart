import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/services/accounts_service.dart';
import 'package:buoy_ai/services/simplefin_service.dart';

void main() {
  group('BankConnectionSummary.fromJson', () {
    test('reads the camelCase the server sends', () {
      final c = BankConnectionSummary.fromJson({
        'id': 4,
        'provider': 'SimpleFin',
        'institutionName': 'My Bank',
        'isActive': true,
        'requiresUserAction': true,
        'status': 'LoginRequired',
        'lastSyncAt': '2026-10-09T12:00:00Z',
        'message': 'Make a new token.',
        'accountCount': 2,
      });

      expect(c.id, 4);
      expect(c.isSimpleFin, isTrue);
      expect(c.institutionName, 'My Bank');
      expect(c.requiresUserAction, isTrue);
      expect(c.status, 'LoginRequired');
      expect(c.lastSyncAt, DateTime.utc(2026, 10, 9, 12));
      expect(c.message, 'Make a new token.');
      expect(c.accountCount, 2);
      expect(c.isHealthy, isFalse);
    });

    test('also reads PascalCase, and copes with missing fields', () {
      final c = BankConnectionSummary.fromJson({'Id': 1, 'Provider': 'Plaid', 'InstitutionName': 'Ally'});

      expect(c.id, 1);
      expect(c.isSimpleFin, isFalse);
      expect(c.isActive, isTrue);
      expect(c.status, 'Active');
      expect(c.lastSyncAt, isNull);
      expect(c.message, isNull);
      expect(c.accountCount, 0);
      expect(c.isHealthy, isTrue);
    });

    test('a disconnected or flagged connection is not healthy', () {
      expect(
        BankConnectionSummary.fromJson({'provider': 'SimpleFin', 'isActive': false, 'status': 'Disconnected'}).isHealthy,
        isFalse,
      );
      expect(
        BankConnectionSummary.fromJson({'provider': 'SimpleFin', 'requiresUserAction': true}).isHealthy,
        isFalse,
      );
    });
  });

  group('SimpleFinConnectResult.fromJson', () {
    test('success with no message', () {
      final r = SimpleFinConnectResult.fromJson({'provider': 'SimpleFin', 'success': true, 'message': null});

      expect(r.success, isTrue);
      expect(r.message, isNull);
    });

    test('failure carries the message', () {
      final r = SimpleFinConnectResult.fromJson({'Success': false, 'Message': 'Make a new one.'});

      expect(r.success, isFalse);
      expect(r.message, 'Make a new one.');
    });
  });

  group('PlaidConnection.provider', () {
    Map<String, dynamic> json([String? provider]) => {
          'id': 1,
          'institutionId': 'ins_1',
          'institutionName': 'Bank',
          'accounts': <dynamic>[],
          'lastSyncAt': '2026-10-09T12:00:00Z',
          if (provider != null) 'provider': provider,
        };

    test('is Plaid when the server does not say (older servers)', () {
      final c = PlaidConnection.fromJson(json());

      expect(c.provider, 'Plaid');
      expect(c.isSimpleFin, isFalse);
    });

    test('knows a SimpleFIN connection', () {
      expect(PlaidConnection.fromJson(json('SimpleFin')).isSimpleFin, isTrue);
    });
  });
}
