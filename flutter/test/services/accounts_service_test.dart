import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/dashboard_models.dart';
import 'package:buoy_ai/services/accounts_service.dart';

void main() {
  group('PlaidConnection.fromJson', () {
    test('reads requiresUserAction in both casings, default false', () {
      final base = <String, dynamic>{'id': 1, 'institutionName': 'Bank'};
      expect(PlaidConnection.fromJson(base).requiresUserAction, isFalse);
      expect(PlaidConnection.fromJson({...base, 'requiresUserAction': true}).requiresUserAction, isTrue);
      expect(PlaidConnection.fromJson({...base, 'RequiresUserAction': true}).requiresUserAction, isTrue);
    });

    // Regression for the "accounts page won't load on Android" crash: an operator
    // precedence bug (`a ?? b as List<dynamic>?` casts only `b`) meant the
    // camelCase `accounts` path built a List<dynamic> that failed to assign to
    // the List<Account> field. Web (dart2js) elided the cast; the Dart VM (this
    // test) and AOT (mobile) enforce it, so this test throws pre-fix.
    test('parses camelCase accounts into a real List<Account>', () {
      final json = <String, dynamic>{
        'id': 1,
        'institutionName': 'Test Bank',
        'accounts': <dynamic>[
          {'id': 10, 'name': 'Checking', 'type': 0},
          {'id': 11, 'name': 'Savings', 'type': 1},
        ],
      };

      final connection = PlaidConnection.fromJson(json);

      expect(connection.accounts, isA<List<Account>>());
      expect(connection.accounts, hasLength(2));
      expect(connection.accounts.first.name, 'Checking');
    });

    test('parses PascalCase Accounts into a real List<Account>', () {
      final json = <String, dynamic>{
        'Id': 2,
        'InstitutionName': 'Test Bank',
        'Accounts': <dynamic>[
          {'Id': 20, 'Name': 'Credit', 'Type': 2},
        ],
      };

      final connection = PlaidConnection.fromJson(json);

      expect(connection.accounts, isA<List<Account>>());
      expect(connection.accounts, hasLength(1));
    });

    test('defaults to an empty List<Account> when accounts key is absent', () {
      final connection =
          PlaidConnection.fromJson(<String, dynamic>{'id': 3, 'institutionName': 'X'});

      expect(connection.accounts, isA<List<Account>>());
      expect(connection.accounts, isEmpty);
    });
  });
}
