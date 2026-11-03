import 'api_client.dart';

/// Trusted Device Model
class TrustedDevice {
  final int id;
  final String deviceFingerprint;
  final String? deviceName;
  final String? deviceType;
  final String? location;
  final DateTime lastUsedAt;
  final DateTime expiresAt;
  final bool isActive;
  final DateTime createdAt;
  final bool isCurrentDevice;
  final int daysUntilExpiration;
  final String? lastUsedIpAddress;
  final String? lastUsedLocation;

  TrustedDevice({
    required this.id,
    required this.deviceFingerprint,
    this.deviceName,
    this.deviceType,
    this.location,
    required this.lastUsedAt,
    required this.expiresAt,
    required this.isActive,
    required this.createdAt,
    this.isCurrentDevice = false,
    this.daysUntilExpiration = 0,
    this.lastUsedIpAddress,
    this.lastUsedLocation,
  });

  factory TrustedDevice.fromJson(Map<String, dynamic> json) {
    return TrustedDevice(
      id: json['id'] as int,
      deviceFingerprint: json['deviceFingerprint'] as String? ?? '',
      deviceName: json['deviceName'] as String?,
      deviceType: json['deviceType'] as String?,
      location: json['location'] as String?,
      lastUsedAt: json['lastUsedAt'] != null
          ? DateTime.parse(json['lastUsedAt'] as String)
          : DateTime.now(),
      expiresAt: json['expiresAt'] != null
          ? DateTime.parse(json['expiresAt'] as String)
          : DateTime.now().add(const Duration(days: 30)),
      isActive: json['isActive'] as bool? ?? true,
      createdAt: json['createdAt'] != null
          ? DateTime.parse(json['createdAt'] as String)
          : DateTime.now(),
      isCurrentDevice: json['isCurrentDevice'] as bool? ?? false,
      daysUntilExpiration: json['daysUntilExpiration'] as int? ?? 0,
      lastUsedIpAddress: json['lastUsedIpAddress'] as String?,
      lastUsedLocation: json['lastUsedLocation'] as String?,
    );
  }

  /// Get icon for device type
  String get icon {
    switch (deviceType?.toLowerCase()) {
      case 'mobile':
      case 'phone':
        return '📱';
      case 'tablet':
        return '📲';
      case 'desktop':
      case 'computer':
        return '🖥️';
      case 'laptop':
        return '💻';
      default:
        return '📱'; // Default to mobile for app
    }
  }

  /// Human-readable device name
  String get displayName {
    if (deviceName != null && deviceName!.isNotEmpty) {
      return deviceName!;
    }
    return deviceType ?? 'Unknown Device';
  }
}

/// Device Management Service
class DeviceService {
  /// Get all trusted devices
  Future<List<TrustedDevice>> getTrustedDevices() async {
    final response = await apiClient.get<List<dynamic>>(
      '/trusted-devices',
    );
    return response.map((json) => TrustedDevice.fromJson(json as Map<String, dynamic>)).toList();
  }

  /// Revoke a specific device
  Future<void> revokeDevice(int deviceId) async {
    await apiClient.delete<Map<String, dynamic>>(
      '/trusted-devices/$deviceId',
    );
  }

  /// Revoke all devices (logout everywhere)
  Future<void> revokeAllDevices() async {
    await apiClient.delete<Map<String, dynamic>>(
      '/trusted-devices',
    );
  }
}

/// Singleton instance
final deviceService = DeviceService();
