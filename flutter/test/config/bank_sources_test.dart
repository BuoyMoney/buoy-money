import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/config/bank_sources.dart';
import 'package:buoy_ai/config/server_capabilities.dart';

void main() {
  const selfHost = ServerCapabilities(edition: 'selfhost');

  group('hosted', () {
    test('offers Plaid when the server says so', () {
      expect(bankConnectionSources(ServerCapabilities.hosted, ['Plaid']), [BankSource.plaid]);
    });

    test('offers Plaid when the provider list could not be read (what it always offered)', () {
      expect(bankConnectionSources(ServerCapabilities.hosted, null), [BankSource.plaid]);
    });

    test('offers SimpleFIN next to Plaid when the server turned it on', () {
      expect(
        bankConnectionSources(ServerCapabilities.hosted, ['Plaid', 'SimpleFin']),
        [BankSource.plaid, BankSource.simpleFin],
      );
    });

    test('does not offer SimpleFIN or the Cloud feed unless the list names them', () {
      expect(bankConnectionSources(ServerCapabilities.hosted, ['Plaid']), isNot(contains(BankSource.simpleFin)));
      expect(bankConnectionSources(ServerCapabilities.hosted, ['Plaid']), isNot(contains(BankSource.cloudFeed)));
    });
  });

  group('self-host', () {
    test('offers the paired Cloud feed and SimpleFIN', () {
      expect(
        bankConnectionSources(selfHost, ['SimpleFin', 'Bridge']),
        [BankSource.cloudFeed, BankSource.simpleFin],
      );
    });

    test('offers only SimpleFIN when the Cloud feed is not paired', () {
      expect(bankConnectionSources(selfHost, ['SimpleFin']), [BankSource.simpleFin]);
    });

    test('offers nothing to connect when nothing is enabled or the list could not be read', () {
      expect(bankConnectionSources(selfHost, <String>[]), isEmpty);
      expect(bankConnectionSources(selfHost, null), isEmpty);
    });

    test('trusts the server list over the edition, so a build that missed the version handshake still works', () {
      // A dev build skips the handshake and believes it is hosted; the server's list is what counts.
      expect(
        bankConnectionSources(ServerCapabilities.hosted, ['SimpleFin', 'Bridge']),
        [BankSource.cloudFeed, BankSource.simpleFin],
      );
    });
  });

  group('add a bank chooser', () {
    test('hosted with Plaid lists exactly what it offered plus a file, and never needs a link', () {
      final connectable = bankConnectionSources(ServerCapabilities.hosted, ['Plaid']);
      expect(addBankChooserSources(connectable), [BankSource.plaid, BankSource.importFile]);
      expect(cloudFeedNeedsLink(connectable), isFalse);
      expect(
        addBankChooserSources(bankConnectionSources(ServerCapabilities.hosted, ['Plaid', 'SimpleFin'])),
        [BankSource.plaid, BankSource.simpleFin, BankSource.importFile],
      );
    });

    test('self-host not linked lists the Cloud feed first (to link), SimpleFIN and a file', () {
      final connectable = bankConnectionSources(selfHost, ['SimpleFin']);
      expect(addBankChooserSources(connectable),
          [BankSource.cloudFeed, BankSource.simpleFin, BankSource.importFile]);
      expect(cloudFeedNeedsLink(connectable), isTrue);
    });

    test('self-host linked lists the Cloud feed to connect, once', () {
      final connectable = bankConnectionSources(selfHost, ['SimpleFin', 'Bridge']);
      expect(addBankChooserSources(connectable),
          [BankSource.cloudFeed, BankSource.simpleFin, BankSource.importFile]);
      expect(cloudFeedNeedsLink(connectable), isFalse);
    });

    test('self-host with nothing enabled still offers the Cloud feed and a file, never an empty list', () {
      final connectable = bankConnectionSources(selfHost, null);
      expect(addBankChooserSources(connectable), [BankSource.cloudFeed, BankSource.importFile]);
      expect(cloudFeedNeedsLink(connectable), isTrue);
    });

    test('a household member does not see the Cloud feed until the owner links the server', () {
      final notLinked = bankConnectionSources(selfHost, ['SimpleFin']);
      expect(addBankChooserSources(notLinked, canLink: false),
          [BankSource.simpleFin, BankSource.importFile]);
      expect(addBankChooserSources(bankConnectionSources(selfHost, null), canLink: false),
          [BankSource.importFile]);
    });

    test('a household member sees the Cloud feed once the server is linked', () {
      final linked = bankConnectionSources(selfHost, ['SimpleFin', 'Bridge']);
      expect(addBankChooserSources(linked, canLink: false),
          [BankSource.cloudFeed, BankSource.simpleFin, BankSource.importFile]);
    });

    test('hosted ignores canLink', () {
      final connectable = bankConnectionSources(ServerCapabilities.hosted, ['Plaid']);
      expect(addBankChooserSources(connectable, canLink: false), [BankSource.plaid, BankSource.importFile]);
    });
  });
}
