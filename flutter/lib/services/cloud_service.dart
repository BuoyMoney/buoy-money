import 'package:dio/dio.dart';

import 'api_client.dart';
import 'simplefin_service.dart';

/// Whether this self-hosted server is linked to BuoyAI Cloud (`GET /api/CloudBridge/status`, server admin only).
class CloudPairingStatus {
  final bool configured;
  final bool paired;
  final String? name;
  final DateTime? pairedAt;
  final bool awaitingOwnerConfirmation;

  const CloudPairingStatus({
    required this.configured,
    required this.paired,
    this.name,
    this.pairedAt,
    this.awaitingOwnerConfirmation = false,
  });

  factory CloudPairingStatus.fromJson(Map<String, dynamic> json) =>
      CloudPairingStatus(
        configured: json['configured'] == true,
        paired: json['paired'] == true,
        name: json['name'] as String?,
        pairedAt: DateTime.tryParse(
          (json['pairedAt'] ?? '').toString(),
        )?.toLocal(),
        awaitingOwnerConfirmation: json['awaitingOwnerConfirmation'] == true,
      );
}

/// A code link this server started (`POST /api/CloudBridge/link/device/start`): the short code to show, the Cloud
/// page where the owner approves it, and when it runs out. The secret half stays on the server.
class CloudDeviceLink {
  final String userCode;
  final String verificationUri;
  final String verificationUriComplete;
  final DateTime? expiresAt;

  /// Seconds between the server's own polls of the Cloud.
  final int interval;

  const CloudDeviceLink({
    required this.userCode,
    required this.verificationUri,
    required this.verificationUriComplete,
    this.expiresAt,
    this.interval = 5,
  });

  factory CloudDeviceLink.fromJson(Map<String, dynamic> json) {
    final uri = _str(json['verificationUri'] ?? json['VerificationUri']);
    final complete = _str(
        json['verificationUriComplete'] ?? json['VerificationUriComplete']);
    final interval = json['interval'] ?? json['Interval'];
    return CloudDeviceLink(
      userCode: _str(json['userCode'] ?? json['UserCode']),
      verificationUri: uri,
      verificationUriComplete: complete.isEmpty ? uri : complete,
      expiresAt: DateTime.tryParse(
        _str(json['expiresAt'] ?? json['ExpiresAt']),
      )?.toLocal(),
      interval: interval is num ? interval.toInt() : 5,
    );
  }
}

/// Where the code link stands (`GET /api/CloudBridge/link/device/status`).
enum CloudDeviceLinkState { none, waiting, linked, expired, denied, failed }

extension CloudDeviceLinkStateX on CloudDeviceLinkState {
  static CloudDeviceLinkState fromString(String? value) =>
      CloudDeviceLinkState.values.firstWhere(
        (s) => s.name == (value ?? '').toLowerCase(),
        orElse: () => CloudDeviceLinkState.none,
      );

  static CloudDeviceLinkState fromInt(int value) =>
      value >= 0 && value < CloudDeviceLinkState.values.length
          ? CloudDeviceLinkState.values[value]
          : CloudDeviceLinkState.none;

  String get displayName => switch (this) {
        CloudDeviceLinkState.none => 'Not started',
        CloudDeviceLinkState.waiting => 'Waiting for approval',
        CloudDeviceLinkState.linked => 'Linked',
        CloudDeviceLinkState.expired => 'Expired',
        CloudDeviceLinkState.denied => 'Turned down',
        CloudDeviceLinkState.failed => 'Failed',
      };
}

class CloudDeviceLinkStatus {
  final CloudDeviceLinkState state;

  /// The running session, when there is one: lets a reopened screen show the same code again.
  final CloudDeviceLink? link;

  /// Why it failed, in words safe to show. Only for [CloudDeviceLinkState.failed].
  final String? message;

  const CloudDeviceLinkStatus({required this.state, this.link, this.message});

  factory CloudDeviceLinkStatus.fromJson(Map<String, dynamic> json) {
    final code = _str(json['userCode'] ?? json['UserCode']);
    final message = _str(json['message'] ?? json['Message']);
    return CloudDeviceLinkStatus(
      state: CloudDeviceLinkStateX.fromString(
        _str(json['state'] ?? json['State']),
      ),
      link: code.isEmpty ? null : CloudDeviceLink.fromJson(json),
      message: message.isEmpty ? null : message,
    );
  }
}

class CloudPrice {
  final int amountCents;
  final String currency;

  const CloudPrice({required this.amountCents, required this.currency});

  factory CloudPrice.fromJson(Map<String, dynamic> json) => CloudPrice(
        amountCents: (json['amountCents'] as num?)?.toInt() ?? 0,
        currency: (json['currency'] ?? 'usd').toString(),
      );
}

/// One Cloud add-on in the catalog (bank sync, AI, extra logins, the bundle).
class CloudProduct {
  final String name;
  final String displayName;
  final List<String> requires;
  final int? includedLogins;
  final CloudPrice? monthly;
  final CloudPrice? yearly;

  const CloudProduct({
    required this.name,
    required this.displayName,
    this.requires = const [],
    this.includedLogins,
    this.monthly,
    this.yearly,
  });

  factory CloudProduct.fromJson(Map<String, dynamic> json) {
    CloudPrice? price(dynamic v) =>
        v is Map ? CloudPrice.fromJson(Map<String, dynamic>.from(v)) : null;
    final requires = json['requires'];
    return CloudProduct(
      name: (json['name'] ?? '').toString(),
      displayName: (json['displayName'] ?? json['name'] ?? '').toString(),
      requires: requires is List
          ? requires.map((e) => e.toString()).toList()
          : const [],
      includedLogins: (json['includedLogins'] as num?)?.toInt(),
      monthly: price(json['monthly']),
      yearly: price(json['yearly']),
    );
  }
}

/// Products the screens never offer. Instant refresh is not on sale: the billing service stops returning it, and this
/// drops it too, so an older service that still lists it cannot put it back in the shop.
const _hiddenProducts = {'instant_refresh'};

/// The catalog as this user sees it: founding prices when they qualify, list prices otherwise.
class CloudCatalog {
  final bool founding;
  final List<CloudProduct> products;

  const CloudCatalog({required this.founding, required this.products});

  factory CloudCatalog.fromJson(Map<String, dynamic> json) {
    final raw = json['products'];
    return CloudCatalog(
      founding: json['founding'] == true,
      products: raw is List
          ? [
              for (final p in raw)
                if (p is Map && !_hiddenProducts.contains(p['name']))
                  CloudProduct.fromJson(Map<String, dynamic>.from(p)),
            ]
          : const [],
    );
  }
}

/// What the user holds today (`GET /api/cloud-billing/status`).
class CloudPlan {
  final bool hasSubscription;
  final bool bankSync;
  final bool ai;

  /// The user holds a live Standard or Premium plan of the older kind (any provider). Their billing is on the Billing page.
  final bool hasLivePlan;

  const CloudPlan({
    this.hasSubscription = false,
    this.bankSync = false,
    this.ai = false,
    this.hasLivePlan = false,
  });

  bool holds(String product) => switch (product) {
        'bank_sync' => bankSync,
        'ai' => ai,
        _ => false,
      };

  factory CloudPlan.fromJson(Map<String, dynamic> json) {
    final access = json['access'];
    final a = access is Map
        ? Map<String, dynamic>.from(access)
        : const <String, dynamic>{};
    return CloudPlan(
      // An unknown plan (lookup failed) or a live hosted plan is treated as "has a plan": the server
      // refuses a first-purchase checkout for both (409 already_subscribed).
      hasSubscription: json['subscription'] != null ||
          json['subscriptionLookupFailed'] == true ||
          json['hasLivePlan'] == true,
      bankSync: a['bankSync'] == true,
      ai: a['ai'] == true,
      hasLivePlan: json['hasLivePlan'] == true,
    );
  }
}

/// A self-hosted server on the signed-in user's Cloud account (`GET /api/cloud-servers`, hosted only).
class CloudServer {
  final String id;
  final String name;
  final String? serverVersion;
  final DateTime? pairedAt;
  final DateTime? confirmedAt;
  final DateTime? revokedAt;

  /// `awaiting_confirmation`, `linked` or `revoked`.
  final String status;

  const CloudServer({
    required this.id,
    required this.name,
    this.serverVersion,
    this.pairedAt,
    this.confirmedAt,
    this.revokedAt,
    required this.status,
  });

  bool get awaitingConfirmation => status == 'awaiting_confirmation';
  bool get revoked => status == 'revoked';

  factory CloudServer.fromJson(Map<String, dynamic> json) {
    DateTime? date(String camel, String pascal) =>
        DateTime.tryParse(_str(json[camel] ?? json[pascal]))?.toLocal();
    final version = json['serverVersion'] ?? json['ServerVersion'];
    return CloudServer(
      id: _str(json['id'] ?? json['Id']),
      name: _str(json['name'] ?? json['Name']),
      serverVersion:
          version == null || _str(version).isEmpty ? null : _str(version),
      pairedAt: date('pairedAt', 'PairedAt'),
      confirmedAt: date('confirmedAt', 'ConfirmedAt'),
      revokedAt: date('revokedAt', 'RevokedAt'),
      status: _str(json['status'] ?? json['Status'], 'linked'),
    );
  }
}

/// A one-time code the user types into their own server (`POST /api/cloud-servers/codes`).
class CloudPairingCode {
  final String code;
  final DateTime? expiresAt;

  const CloudPairingCode({required this.code, this.expiresAt});

  factory CloudPairingCode.fromJson(Map<String, dynamic> json) =>
      CloudPairingCode(
        code: _str(json['code'] ?? json['Code']),
        expiresAt: DateTime.tryParse(
          _str(json['expiresAt'] ?? json['ExpiresAt']),
        )?.toLocal(),
      );
}

/// A self-hosted server waiting to be linked by code (`GET /api/cloud-servers/link-sessions/{code}`, hosted only).
/// The server shows the code; its owner types it (or opens the QR link) in the Cloud portal and approves.
class CloudLinkSession {
  final String userCode;
  final String name;
  final String? serverVersion;
  final DateTime? createdAt;
  final DateTime? expiresAt;

  /// `pending`, `approved`, `denied` or `expired`.
  final String status;

  const CloudLinkSession({
    required this.userCode,
    required this.name,
    this.serverVersion,
    this.createdAt,
    this.expiresAt,
    required this.status,
  });

  bool get pending => status == 'pending';

  factory CloudLinkSession.fromJson(Map<String, dynamic> json) {
    DateTime? date(String camel, String pascal) =>
        DateTime.tryParse(_str(json[camel] ?? json[pascal]))?.toLocal();
    final version = json['serverVersion'] ?? json['ServerVersion'];
    return CloudLinkSession(
      userCode: _str(json['userCode'] ?? json['UserCode']),
      name: _str(json['name'] ?? json['Name']),
      serverVersion:
          version == null || _str(version).isEmpty ? null : _str(version),
      createdAt: date('createdAt', 'CreatedAt'),
      expiresAt: date('expiresAt', 'ExpiresAt'),
      status: _str(json['status'] ?? json['Status'], 'pending'),
    );
  }

  /// The letters a code is made of, as the Cloud issues them (no vowels, no 0, O, 1 or I).
  static const alphabet = 'BCDFGHJKLMNPQRSTVWXZ23456789';

  /// [raw] as `XXXX-XXXX` (upper case, spaces and dashes dropped), or null unless it is exactly 8 letters from
  /// [alphabet]. Mirrors the hosted backend's check, so a typo is caught before any request.
  static String? normalizeCode(String? raw) {
    if (raw == null || raw.length > 32) return null;
    final chars = raw
        .toUpperCase()
        .split('')
        .where((c) => c != '-' && c.trim().isNotEmpty)
        .toList();
    if (chars.length != 8 || chars.any((c) => !alphabet.contains(c))) {
      return null;
    }
    return '${chars.take(4).join()}-${chars.skip(4).join()}';
  }
}

String _str(Object? v, [String fallback = '']) =>
    v == null ? fallback : v.toString();

int _int(Object? v) => v is num ? v.toInt() : 0;

/// One product on the subscription: what it is, how many, and what one costs per interval.
class CloudPlanItem {
  final String product;
  final int quantity;
  final int unitAmountCents;
  final String currency;

  const CloudPlanItem({
    required this.product,
    required this.quantity,
    required this.unitAmountCents,
    required this.currency,
  });

  factory CloudPlanItem.fromJson(Map<String, dynamic> json) => CloudPlanItem(
        product: _str(json['product']),
        quantity: _int(json['quantity']),
        unitAmountCents: _int(json['unitAmountCents']),
        currency: _str(json['currency'], 'usd'),
      );
}

/// The Cloud subscription as the billing service reports it.
class CloudPlanState {
  final String status;

  /// `monthly` or `yearly`.
  final String interval;
  final bool founding;
  final DateTime? currentPeriodEnd;
  final bool cancelAtPeriodEnd;
  final List<CloudPlanItem> items;

  const CloudPlanState({
    required this.status,
    required this.interval,
    required this.founding,
    required this.currentPeriodEnd,
    required this.cancelAtPeriodEnd,
    required this.items,
  });

  factory CloudPlanState.fromJson(Map<String, dynamic> json) {
    final raw = json['items'];
    return CloudPlanState(
      status: _str(json['status']),
      interval: _str(json['interval'], 'monthly'),
      founding: json['founding'] == true,
      currentPeriodEnd: DateTime.tryParse(
        _str(json['currentPeriodEnd']),
      )?.toLocal(),
      cancelAtPeriodEnd: json['cancelAtPeriodEnd'] == true,
      items: raw is List
          ? [
              for (final i in raw)
                if (i is Map)
                  CloudPlanItem.fromJson(Map<String, dynamic>.from(i)),
            ]
          : const [],
    );
  }
}

/// One line of the next invoice. [kind] is `product`, `ai_overage`, `refresh_overage` or `other`.
class CloudInvoiceLine {
  final String kind;
  final String? product;
  final String description;
  final int quantity;

  /// 0 where there is no per-unit price (usage, one-off items): read [amountCents].
  final int unitAmountCents;
  final int amountCents;
  final bool proration;

  const CloudInvoiceLine({
    required this.kind,
    required this.product,
    required this.description,
    required this.quantity,
    required this.unitAmountCents,
    required this.amountCents,
    required this.proration,
  });

  factory CloudInvoiceLine.fromJson(Map<String, dynamic> json) =>
      CloudInvoiceLine(
        kind: _str(json['kind'], 'other'),
        product: json['product'] == null ? null : _str(json['product']),
        description: _str(json['description']),
        quantity: json['quantity'] is num ? _int(json['quantity']) : 1,
        unitAmountCents: _int(json['unitAmountCents']),
        amountCents: _int(json['amountCents']),
        proration: json['proration'] == true,
      );
}

/// What the subscription will invoice next, as Stripe previews it. [totalCents] already includes usage so far.
class CloudUpcomingInvoice {
  final String interval;
  final bool founding;
  final DateTime? nextInvoiceAt;
  final String currency;
  final int totalCents;
  final List<CloudInvoiceLine> lines;

  const CloudUpcomingInvoice({
    required this.interval,
    required this.founding,
    required this.nextInvoiceAt,
    required this.currency,
    required this.totalCents,
    required this.lines,
  });

  factory CloudUpcomingInvoice.fromJson(Map<String, dynamic> json) {
    final raw = json['lines'];
    return CloudUpcomingInvoice(
      interval: _str(json['interval'], 'monthly'),
      founding: json['founding'] == true,
      nextInvoiceAt: DateTime.tryParse(_str(json['nextInvoiceAt']))?.toLocal(),
      currency: _str(json['currency'], 'usd'),
      totalCents: _int(json['totalCents']),
      lines: raw is List
          ? [
              for (final l in raw)
                if (l is Map)
                  CloudInvoiceLine.fromJson(Map<String, dynamic>.from(l)),
            ]
          : const [],
    );
  }
}

/// The billing screen's data, the same on the hosted service and on a self-hosted server. [lookupFailed] means the
/// billing service could not be asked (or only the invoice could not): a missing piece is unknown, not absent.
class CloudBillingSummary {
  final CloudPlanState? subscription;
  final CloudUpcomingInvoice? upcoming;
  final bool lookupFailed;

  const CloudBillingSummary({
    this.subscription,
    this.upcoming,
    this.lookupFailed = false,
  });

  factory CloudBillingSummary.fromJson(Map<String, dynamic> json) {
    final sub = json['subscription'];
    final up = json['upcoming'];
    return CloudBillingSummary(
      subscription: sub is Map
          ? CloudPlanState.fromJson(Map<String, dynamic>.from(sub))
          : null,
      upcoming: up is Map
          ? CloudUpcomingInvoice.fromJson(Map<String, dynamic>.from(up))
          : null,
      lookupFailed: json['lookupFailed'] == true,
    );
  }
}

/// Talks to the endpoints behind the Cloud screen: pairing, the Cloud bank feed, the add-on catalog and billing. Every call is the signed-in user's own session; nothing here holds a secret.
class CloudService {
  static const bridgeProvider = 'Bridge';

  /// Pairing state. Null when the caller is not the server administrator (403) or the server is hosted (404).
  Future<CloudPairingStatus?> pairingStatus() async {
    try {
      final json = await apiClient.get<Map<String, dynamic>>(
        '/CloudBridge/status',
      );
      return CloudPairingStatus.fromJson(json);
    } on Object catch (e) {
      if (_isStatus(e, const [403, 404])) return null;
      rethrow;
    }
  }

  Future<CloudPairingStatus> pair(String code, String? name) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/CloudBridge/pair',
      data: {'code': code, if (name != null && name.isNotEmpty) 'name': name},
    );
    return CloudPairingStatus.fromJson(json);
  }

  /// Starts "Link with BuoyAI Cloud" by code (administrator): the server gets a short code from the Cloud and polls
  /// for the owner's approval by itself. Works on plain http servers too.
  Future<CloudDeviceLink> startDeviceLink(String? name) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/CloudBridge/link/device/start',
      data: {if (name != null && name.isNotEmpty) 'name': name},
    );
    return CloudDeviceLink.fromJson(json);
  }

  /// Where the code link stands. The card asks every few seconds while it waits.
  Future<CloudDeviceLinkStatus> deviceLinkStatus() async {
    final json = await apiClient.get<Map<String, dynamic>>(
      '/CloudBridge/link/device/status',
    );
    return CloudDeviceLinkStatus.fromJson(json);
  }

  /// Stops waiting for approval. The code simply runs out on the Cloud.
  Future<void> cancelDeviceLink() async {
    await apiClient.delete<dynamic>('/CloudBridge/link/device');
  }

  /// The older browser round trip (kept as a fallback on the server). Returns the buoy-ai.com address to open; the
  /// owner approves the server there and the browser comes back to this server.
  Future<String> startCloudLink(String? name) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/CloudBridge/link/start',
      data: {if (name != null && name.isNotEmpty) 'name': name},
    );
    return _str(json['url'] ?? json['Url']);
  }

  Future<void> unpair() async {
    await apiClient.delete<dynamic>('/CloudBridge/pair');
  }

  /// True when this server has the Cloud bank feed turned on (the Bridge provider is registered).
  Future<bool> bankFeedAvailable() async {
    try {
      final providers = await apiClient.get<List<dynamic>>('/aggregators');
      return providers.map((e) => e.toString()).contains(bridgeProvider);
    } on Object {
      return false;
    }
  }

  /// Whether the Cloud bank feed can connect a bank right now: true once this server is linked to the Cloud
  /// (`GET /api/bank/providers` lists the feed only then; every signed-in member may ask). Null when that could not
  /// be read.
  Future<bool?> bankFeedLinked() async {
    try {
      final providers = await apiClient.get<List<dynamic>>('/bank/providers');
      return providers.map((e) => e.toString()).contains(bridgeProvider);
    } on Object {
      return null;
    }
  }

  Future<List<BankConnectionSummary>> bankConnections() async {
    final data = await apiClient.get<List<dynamic>>('/aggregators/connections');
    return [
      for (final item in data)
        if (item is Map)
          BankConnectionSummary.fromJson(Map<String, dynamic>.from(item)),
    ].where((c) => c.provider == bridgeProvider && c.isActive).toList();
  }

  /// Starts the Cloud page where the person signs in at their bank. Returns the address to open.
  Future<String?> startBankConnect() async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/$bridgeProvider/connect/start',
      data: <String, dynamic>{},
    );
    final value = (json['value'] ?? json['Value'])?.toString();
    return (value == null || value.isEmpty) ? null : value;
  }

  /// Called when the person says they finished at the bank. Null when the bank is in, else a message to show.
  Future<String?> finishBankConnect(String pageUrl) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/$bridgeProvider/connect/complete',
      data: {'artifact': pageUrl},
    );
    final ok = (json['success'] ?? json['Success']) == true;
    final message = (json['message'] ?? json['Message'])?.toString();
    return ok ? null : (message ?? '');
  }

  /// The add-ons and their prices. Hosted only; null on a server without Cloud billing (404).
  Future<CloudCatalog?> catalog() async {
    try {
      final json = await apiClient.get<Map<String, dynamic>>(
        '/cloud-billing/catalog',
      );
      return CloudCatalog.fromJson(json);
    } on Object catch (e) {
      if (_isStatus(e, const [404])) return null;
      rethrow;
    }
  }

  Future<CloudPlan> plan() async {
    final json = await apiClient.get<Map<String, dynamic>>(
      '/cloud-billing/status',
    );
    return CloudPlan.fromJson(json);
  }

  /// Starts a Stripe Checkout for a first purchase. Returns the hosted page to open.
  Future<String> checkout({
    required String interval,
    required List<String> products,
    required String platform,
  }) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/cloud-billing/checkout',
      data: {
        'interval': interval,
        'products': [
          for (final p in products) {'product': p, 'quantity': 1},
        ],
        'platform': platform,
      },
    );
    return (json['url'] ?? '').toString();
  }

  /// The plan and the next invoice (the billing screen). Hosted reads `/cloud-billing/summary`; a self-hosted server asks
  /// the Cloud through its pairing at `/cloud/billing`. Null when the server has no Cloud billing (404).
  Future<CloudBillingSummary?> billingSummary({required bool selfHost}) async {
    try {
      final json = await apiClient.get<Map<String, dynamic>>(
        selfHost ? '/cloud/billing' : '/cloud-billing/summary',
      );
      return CloudBillingSummary.fromJson(json);
    } on Object catch (e) {
      if (_isStatus(e, const [404])) return null;
      rethrow;
    }
  }

  /// A Stripe page for the card, billing details and invoices. Returns the address to open. [fromPortal] (the Cloud
  /// portal) makes Stripe send the customer back there instead of into the hosted app.
  Future<String> billingPortal({
    required bool selfHost,
    bool fromPortal = false,
  }) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      selfHost
          ? '/cloud/billing/portal-session'
          : fromPortal
              ? '/cloud-billing/portal-session?from=portal'
              : '/cloud-billing/portal-session',
      data: <String, dynamic>{},
    );
    return (json['url'] ?? '').toString();
  }

  /// The servers on the user's Cloud account. Hosted only; null when this app has no Cloud portal (404).
  /// A user with no Cloud account yet gets a 409 with code `no_cloud_account`.
  Future<List<CloudServer>?> cloudServers() async {
    try {
      final data = await apiClient.get<List<dynamic>>('/cloud-servers');
      return [
        for (final item in data)
          if (item is Map)
            CloudServer.fromJson(Map<String, dynamic>.from(item)),
      ];
    } on Object catch (e) {
      if (_isStatus(e, const [404])) return null;
      rethrow;
    }
  }

  /// Whether the hosted app's Settings shows "Your servers": only for someone with a Cloud add-on or a server already
  /// on their account. False when the server has no Cloud portal (404) or cannot say.
  Future<bool> serversVisible() async {
    try {
      final json =
          await apiClient.get<Map<String, dynamic>>('/cloud-servers/visibility');
      return (json['show'] ?? json['Show']) == true;
    } on Object catch (e) {
      if (_isStatus(e, const [404])) return false;
      rethrow;
    }
  }

  /// The server waiting behind [code] (already normalised, see [CloudLinkSession.normalizeCode]).
  /// 404 with code `invalid_code` when it is unknown or has run out.
  Future<CloudLinkSession> linkSession(String code) async {
    final json = await apiClient.get<Map<String, dynamic>>(
      '/cloud-servers/link-sessions/${Uri.encodeComponent(code)}',
    );
    return CloudLinkSession.fromJson(json);
  }

  /// Links the waiting server to this account and returns the session (status `approved`). Needs a fresh step-up,
  /// like [createPairingCode]. A code someone else (or a deny) already decided is a 409 `already_decided`.
  Future<CloudLinkSession> approveLinkSession(String code) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/cloud-servers/link-sessions/${Uri.encodeComponent(code)}/approve',
      data: <String, dynamic>{},
    );
    return CloudLinkSession.fromJson(json);
  }

  /// Refuses the waiting server and returns the session (status `denied`). No step-up.
  Future<CloudLinkSession> denyLinkSession(String code) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/cloud-servers/link-sessions/${Uri.encodeComponent(code)}/deny',
      data: <String, dynamic>{},
    );
    return CloudLinkSession.fromJson(json);
  }

  /// Makes a pairing code. Needs a fresh step-up: a 403 `MfaRequired` means run the step-up dialog and try again,
  /// a 403 with code `email_verification_required` means confirm the account's email first (see
  /// [needsVerifiedEmail]): the emailed step-up code is the factor every account has.
  Future<CloudPairingCode> createPairingCode() async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/cloud-servers/codes',
      data: <String, dynamic>{},
    );
    return CloudPairingCode.fromJson(json);
  }

  /// Approves a self-hosted server that sent the user here (hosted). Needs a fresh step-up, like
  /// [createPairingCode]. Returns the address to send the browser to: the server's callback with the code.
  Future<String> approveCloudLink({
    required String callback,
    required String state,
    String? name,
  }) async {
    final json = await apiClient.post<Map<String, dynamic>>(
      '/cloud-servers/link',
      data: {
        'callback': callback,
        'state': state,
        if (name != null && name.isNotEmpty) 'name': name,
      },
    );
    return _str(json['redirect'] ?? json['Redirect']);
  }

  /// Lets a newly paired server receive data. Needs a fresh step-up, like [createPairingCode].
  Future<void> confirmServer(String id) async {
    await apiClient.post<dynamic>(
      '/cloud-servers/$id/confirm',
      data: <String, dynamic>{},
    );
  }

  Future<void> revokeServer(String id) async {
    await apiClient.delete<dynamic>('/cloud-servers/$id');
  }

  /// The HTTP status of a failed call, or null when the error is not an HTTP one.
  static int? statusOf(Object error) =>
      error is DioException ? error.response?.statusCode : null;

  /// The machine-readable `code` of a failed call's JSON body, or null when there is none.
  static String? codeOf(Object error) {
    final data = error is DioException ? error.response?.data : null;
    final code = data is Map ? data['code'] : null;
    return code is String ? code : null;
  }

  /// The `error` field of a failed call's JSON body (for example `MfaRequired`), or null.
  static String? errorOf(Object error) {
    final data = error is DioException ? error.response?.data : null;
    final value = data is Map ? data['error'] : null;
    return value is String ? value : null;
  }

  /// True for the 403 that asks the account to confirm its email before a server can be linked. Once confirmed,
  /// the step-up dialog can send its code there, so an account with no authenticator or passkey can still approve.
  static bool needsVerifiedEmail(Object error) =>
      statusOf(error) == 403 && codeOf(error) == 'email_verification_required';

  static bool _isStatus(Object error, List<int> statuses) =>
      error is DioException && statuses.contains(error.response?.statusCode);
}

final cloudService = CloudService();
