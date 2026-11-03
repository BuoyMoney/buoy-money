import 'server_capabilities.dart';

/// One way to bring accounts into the app, shown in the Accounts page's "Add a bank" chooser.
enum BankSource {
  /// Plaid Link: the hosted service's own connection.
  plaid,

  /// The BuoyAI Cloud bank feed, for a self-hosted server that is paired with a Cloud account.
  cloudFeed,

  /// SimpleFIN: a setup token the person makes at SimpleFIN and pastes in.
  simpleFin,

  /// A CSV or OFX file from the bank. Always possible, so it is not part of [bankConnectionSources].
  importFile,
}

/// The ways this server can connect a bank right now.
///
/// [providers] is what `GET /api/bank/providers` said ('Plaid', 'SimpleFin', 'Bridge'), and the server is the
/// authority: it lists Plaid only when it is the hosted service and the Cloud feed only once it is paired.
/// When the list could not be read, the answer falls back to the edition from the version handshake: the hosted
/// service offers Plaid (what it always offered), a self-hosted server offers nothing it cannot prove.
List<BankSource> bankConnectionSources(ServerCapabilities server, List<String>? providers) {
  final offered = providers ?? (server.hasBankConnection ? const ['Plaid'] : const <String>[]);
  return [
    if (offered.contains('Plaid')) BankSource.plaid,
    if (offered.contains('Bridge')) BankSource.cloudFeed,
    if (offered.contains('SimpleFin')) BankSource.simpleFin,
  ];
}

/// What the "Add a bank" chooser lists, given what this server can connect right now ([connectable], from
/// [bankConnectionSources]).
///
/// The hosted service (it offers Plaid) lists exactly what it offers, then "Import a file", as it always did. A
/// self-hosted server always lists the BuoyAI Cloud bank feed first, linked or not (see [cloudFeedNeedsLink]), then
/// SimpleFIN when the server offers it, then "Import a file". So the chooser is never empty and never a dead end.
///
/// [canLink] is false for a household member, who cannot link the server: they see the Cloud bank feed only once
/// the owner has linked it.
List<BankSource> addBankChooserSources(List<BankSource> connectable, {bool canLink = true}) {
  if (connectable.contains(BankSource.plaid)) {
    return [...connectable, BankSource.importFile];
  }
  return [
    if (canLink || connectable.contains(BankSource.cloudFeed)) BankSource.cloudFeed,
    ...connectable.where((s) => s != BankSource.cloudFeed),
    BankSource.importFile,
  ];
}

/// True on a self-hosted server whose Cloud bank feed is listed but cannot connect yet: the server is not linked
/// to BuoyAI Cloud. Choosing it explains that and offers "Link with BuoyAI Cloud" instead of failing.
bool cloudFeedNeedsLink(List<BankSource> connectable) =>
    !connectable.contains(BankSource.plaid) && !connectable.contains(BankSource.cloudFeed);
