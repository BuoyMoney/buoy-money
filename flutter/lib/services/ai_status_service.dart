import 'package:flutter/foundation.dart';

import 'api_client.dart';

/// One row of the capability matrix: what an AI feature does with this server's provider.
/// [status] is `works`, `limited` or `off`.
class AiFeatureStatus {
  final String id;
  final String name;
  final String status;
  final String note;

  const AiFeatureStatus({
    required this.id,
    required this.name,
    required this.status,
    required this.note,
  });

  bool get isOff => status == 'off';

  factory AiFeatureStatus.fromJson(Map<String, dynamic> json) => AiFeatureStatus(
        id: (json['id'] ?? '').toString(),
        name: (json['name'] ?? '').toString(),
        status: (json['status'] ?? 'off').toString(),
        note: (json['note'] ?? '').toString(),
      );
}

/// Connection details the server only shows to an administrator.
class AiProviderDetails {
  final String? baseUrl;
  final String? model;
  final String embeddingModel;
  final bool apiKeySet;
  final String? setupProblem;

  const AiProviderDetails({
    this.baseUrl,
    this.model,
    this.embeddingModel = '',
    this.apiKeySet = false,
    this.setupProblem,
  });

  factory AiProviderDetails.fromJson(Map<String, dynamic> json) => AiProviderDetails(
        baseUrl: json['baseUrl'] as String?,
        model: json['model'] as String?,
        embeddingModel: (json['embeddingModel'] ?? '').toString(),
        apiKeySet: json['apiKeySet'] == true,
        setupProblem: json['setupProblem'] as String?,
      );
}

/// The answer of `GET /api/aisettings`.
class AiStatus {
  final bool enabled;
  final String provider;
  final String edition;
  final List<AiFeatureStatus> features;
  final AiProviderDetails? details;

  const AiStatus({
    required this.enabled,
    required this.provider,
    required this.edition,
    required this.features,
    this.details,
  });

  factory AiStatus.fromJson(Map<String, dynamic> json) {
    final raw = json['features'];
    final features = <AiFeatureStatus>[];
    if (raw is List) {
      for (final item in raw) {
        if (item is Map) {
          features.add(AiFeatureStatus.fromJson(Map<String, dynamic>.from(item)));
        }
      }
    }
    final details = json['details'];
    return AiStatus(
      enabled: json['enabled'] == true,
      provider: (json['provider'] ?? '').toString(),
      edition: (json['edition'] ?? '').toString(),
      features: features,
      details: details is Map
          ? AiProviderDetails.fromJson(Map<String, dynamic>.from(details))
          : null,
    );
  }
}

/// One step of the live check an administrator can run.
class AiProbeStep {
  final String name;
  final bool ok;
  final String detail;
  final int milliseconds;

  const AiProbeStep({
    required this.name,
    required this.ok,
    required this.detail,
    required this.milliseconds,
  });

  factory AiProbeStep.fromJson(Map<String, dynamic> json) => AiProbeStep(
        name: (json['name'] ?? '').toString(),
        ok: json['ok'] == true,
        detail: (json['detail'] ?? '').toString(),
        milliseconds: (json['milliseconds'] as num?)?.toInt() ?? 0,
      );
}

class AiStatusService {
  Future<AiStatus> fetch() async {
    final json = await apiClient.get<Map<String, dynamic>>('/aisettings');
    return AiStatus.fromJson(json);
  }

  Future<List<AiProbeStep>> probe() async {
    final json = await apiClient.post<Map<String, dynamic>>('/aisettings/probe');
    final raw = json['steps'];
    final steps = <AiProbeStep>[];
    if (raw is List) {
      for (final item in raw) {
        if (item is Map) steps.add(AiProbeStep.fromJson(Map<String, dynamic>.from(item)));
      }
    }
    return steps;
  }
}

/// What the app may show of AI on this server.
///
/// Unknown means ON: until the server has answered (or if it never does, or is an older server
/// without the endpoint) every AI surface behaves exactly as it did before this existed, so the
/// hosted app can never lose a feature to a failed lookup. Only a clear answer from the server
/// ("AI is off", "this feature is off") hides anything.
class AiAvailability extends ChangeNotifier {
  AiAvailability({AiStatusService? service}) : _service = service ?? AiStatusService();

  final AiStatusService _service;
  AiStatus? _status;
  Future<void>? _loading;

  AiStatus? get status => _status;
  bool get loaded => _status != null;

  /// True when the server says no AI provider is set up. Unknown counts as on.
  bool get aiOff => _status != null && !_status!.enabled;

  /// Whether to show a feature (ids from the server: categorization, insights, chat, ...).
  bool featureOn(String id) {
    final s = _status;
    if (s == null) return true;
    if (!s.enabled) return false;
    for (final f in s.features) {
      if (f.id == id) return !f.isOff;
    }
    return true;
  }

  /// Loads once; later calls share the same answer. Call [refresh] to ask again.
  Future<void> ensureLoaded() => _loading ??= _load();

  Future<void> refresh() {
    _loading = null;
    return ensureLoaded();
  }

  Future<void> _load() async {
    try {
      _status = await _service.fetch();
      notifyListeners();
    } catch (_) {
      // Older server, offline, signed out: keep showing everything. Not retried by itself (an
      // older server would answer 404 on every screen); [refresh] asks again.
    }
  }

  /// Forget the answer (sign-out), so the next user asks again.
  void reset() {
    _status = null;
    _loading = null;
    notifyListeners();
  }
}

final aiAvailability = AiAvailability();
final aiStatusService = AiStatusService();
