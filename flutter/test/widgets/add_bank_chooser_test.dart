import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/config/bank_sources.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/accounts_page.dart';

/// The chooser the Accounts page opens, built the way the page builds it for each edition:
/// whatever [bankConnectionSources] offers, then "Import a file".
Widget _chooser(ServerCapabilities server, List<String>? providers, List<BankSource> picked) {
  final sources = [...bankConnectionSources(server, providers), BankSource.importFile];
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(
      body: AddBankChooserSheet(sources: sources, onSelected: picked.add),
    ),
  );
}

Finder _option(BankSource source) => find.byKey(ValueKey('add-bank-${source.name}'));

void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  const selfHost = ServerCapabilities(edition: 'selfhost');

  testWidgets('hosted with Plaid only lists Plaid and Import a file', (tester) async {
    await tester.pumpWidget(_chooser(ServerCapabilities.hosted, ['Plaid'], []));

    expect(_option(BankSource.plaid), findsOneWidget);
    expect(_option(BankSource.importFile), findsOneWidget);
    expect(_option(BankSource.simpleFin), findsNothing);
    expect(_option(BankSource.cloudFeed), findsNothing);
    expect(find.text('Connect with Plaid'), findsOneWidget);
  });

  testWidgets('hosted with SimpleFIN switched on lists Plaid, SimpleFIN and Import a file', (tester) async {
    await tester.pumpWidget(_chooser(ServerCapabilities.hosted, ['Plaid', 'SimpleFin'], []));

    expect(_option(BankSource.plaid), findsOneWidget);
    expect(_option(BankSource.simpleFin), findsOneWidget);
    expect(_option(BankSource.importFile), findsOneWidget);
    expect(_option(BankSource.cloudFeed), findsNothing);
  });

  testWidgets('self-host paired to the Cloud lists the Cloud feed, SimpleFIN and Import, never Plaid', (tester) async {
    await tester.pumpWidget(_chooser(selfHost, ['SimpleFin', 'Bridge'], []));

    expect(_option(BankSource.cloudFeed), findsOneWidget);
    expect(_option(BankSource.simpleFin), findsOneWidget);
    expect(_option(BankSource.importFile), findsOneWidget);
    expect(_option(BankSource.plaid), findsNothing);
    expect(find.text('BuoyAI Cloud bank feed'), findsOneWidget);
  });

  testWidgets('self-host not paired lists SimpleFIN and Import, without the Cloud feed', (tester) async {
    await tester.pumpWidget(_chooser(selfHost, ['SimpleFin'], []));

    expect(_option(BankSource.simpleFin), findsOneWidget);
    expect(_option(BankSource.importFile), findsOneWidget);
    expect(_option(BankSource.cloudFeed), findsNothing);
    expect(_option(BankSource.plaid), findsNothing);
  });

  testWidgets('self-host with nothing enabled lists only Import a file', (tester) async {
    await tester.pumpWidget(_chooser(selfHost, <String>[], []));

    expect(_option(BankSource.importFile), findsOneWidget);
    expect(find.byType(InkWell), findsOneWidget);
  });

  testWidgets('tapping an option reports which source was chosen', (tester) async {
    final picked = <BankSource>[];
    await tester.pumpWidget(_chooser(selfHost, ['SimpleFin', 'Bridge'], picked));

    await tester.tap(_option(BankSource.simpleFin));
    await tester.tap(_option(BankSource.importFile));

    expect(picked, [BankSource.simpleFin, BankSource.importFile]);
  });

  group('self-hosted chooser (every Add a bank entry point opens this)', () {
    Widget selfHostChooser(List<String>? providers, List<BankSource> picked) {
      final connectable = bankConnectionSources(selfHost, providers);
      return MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: AddBankChooserSheet(
            sources: addBankChooserSources(connectable),
            cloudNeedsLink: cloudFeedNeedsLink(connectable),
            onSelected: picked.add,
          ),
        ),
      );
    }

    testWidgets('not linked lists BuoyAI Cloud (link first), SimpleFIN and Import, never Plaid', (tester) async {
      await tester.pumpWidget(selfHostChooser(['SimpleFin'], []));

      expect(_option(BankSource.cloudFeed), findsOneWidget);
      expect(_option(BankSource.simpleFin), findsOneWidget);
      expect(_option(BankSource.importFile), findsOneWidget);
      expect(_option(BankSource.plaid), findsNothing);
      expect(find.text('Link this server to BuoyAI Cloud first. It takes a minute.'), findsOneWidget);
    });

    testWidgets('nothing enabled still offers the Cloud and a file, so it is never a dead end', (tester) async {
      await tester.pumpWidget(selfHostChooser(<String>[], []));

      expect(_option(BankSource.cloudFeed), findsOneWidget);
      expect(_option(BankSource.importFile), findsOneWidget);
      expect(find.byType(InkWell), findsNWidgets(2));
    });

    testWidgets('linked shows the normal Cloud row', (tester) async {
      await tester.pumpWidget(selfHostChooser(['SimpleFin', 'Bridge'], []));

      expect(find.text('Sign in to your bank through your paired BuoyAI Cloud account.'), findsOneWidget);
      expect(find.text('Link this server to BuoyAI Cloud first. It takes a minute.'), findsNothing);
    });

    testWidgets('the link prompt offers Link with BuoyAI Cloud and Not now', (tester) async {
      var linked = 0;
      var closed = 0;
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: CloudLinkPromptSheet(onLink: () => linked++, onClose: () => closed++),
        ),
      ));

      expect(find.text('Link with BuoyAI Cloud'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('cloud-link-prompt-link')));
      await tester.tap(find.byKey(const ValueKey('cloud-link-prompt-close')));
      expect(linked, 1);
      expect(closed, 1);
    });

    testWidgets('a household member is told the owner manages it, with no link button', (tester) async {
      var linked = 0;
      var closed = 0;
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: CloudLinkPromptSheet(
              ownerManages: true, onLink: () => linked++, onClose: () => closed++),
        ),
      ));

      expect(find.text("Your server's owner manages this."), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-prompt-link')), findsNothing);
      expect(find.text('Link with BuoyAI Cloud'), findsNothing);
      await tester.tap(find.byKey(const ValueKey('cloud-link-prompt-close')));
      expect(closed, 1);
      expect(linked, 0);
    });
  });

  testWidgets('hosted Plaid stays as it was: Plaid only, no Cloud row, no link prompt', (tester) async {
    final connectable = bankConnectionSources(ServerCapabilities.hosted, ['Plaid']);
    expect(cloudFeedNeedsLink(connectable), isFalse);
    expect(addBankChooserSources(connectable), [BankSource.plaid, BankSource.importFile]);
    await tester.pumpWidget(_chooser(ServerCapabilities.hosted, ['Plaid'], []));

    expect(_option(BankSource.plaid), findsOneWidget);
    expect(_option(BankSource.cloudFeed), findsNothing);
    expect(find.text('Link with BuoyAI Cloud'), findsNothing);
  });
}
