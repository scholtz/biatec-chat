import 'dart:convert';

import 'package:http/http.dart' as http;

import '../approvals/approval_client.dart';

/// One enrolled device of the user, as `GET /devices` lists it.
class DeviceInfo {
  const DeviceInfo({
    required this.kid,
    required this.current,
    required this.active,
    required this.activeAt,
  });

  final String kid;

  /// True for the device that asked.
  final bool current;

  /// False while the 24 hour wait of rule 7 runs: a pending enrollment.
  final bool active;
  final DateTime activeAt;

  bool get pending => !active;

  factory DeviceInfo.fromJson(Map<String, Object?> json) => DeviceInfo(
        kid: json['kid'] as String,
        current: json['current'] as bool? ?? false,
        active: json['active'] as bool,
        activeAt: DateTime.fromMillisecondsSinceEpoch(
            (json['active_at'] as num).toInt() * 1000,
            isUtc: true),
      );
}

/// The settings of one client, as `GET /devices/settings` lists them.
class ClientSetting {
  const ClientSetting({
    required this.clientId,
    required this.label,
    required this.verified,
    required this.alwaysAsk,
    required this.perClientThresholdEur,
    required this.pausedUntil,
  });

  final String clientId;
  final String label;
  final bool verified;
  final bool alwaysAsk;

  /// Decimal EUR string, or null to follow the account threshold.
  final String? perClientThresholdEur;

  /// Set while the 24 hour pause of rule 10 runs.
  final DateTime? pausedUntil;

  factory ClientSetting.fromJson(Map<String, Object?> json) {
    final until = json['paused_until'];
    return ClientSetting(
      clientId: json['client_id'] as String,
      label: json['label'] as String? ?? json['client_id'] as String,
      verified: json['verified'] as bool? ?? false,
      alwaysAsk: json['always_ask'] as bool,
      perClientThresholdEur: json['per_client_threshold_eur'] as String?,
      pausedUntil: until is num
          ? DateTime.fromMillisecondsSinceEpoch(until.toInt() * 1000,
              isUtc: true)
          : null,
    );
  }
}

/// The user's consent settings.
class ConsentSettings {
  const ConsentSettings({
    required this.sub,
    required this.biometricRequired,
    required this.thresholdEur,
    required this.blockUnverifiedClients,
    required this.clients,
  });

  /// The user these settings belong to; the signed change names it.
  final String sub;
  final bool biometricRequired;

  /// Decimal EUR string.
  final String thresholdEur;
  final bool blockUnverifiedClients;
  final List<ClientSetting> clients;

  ClientSetting? client(String id) {
    for (final c in clients) {
      if (c.clientId == id) return c;
    }
    return null;
  }

  factory ConsentSettings.fromJson(Map<String, Object?> json) =>
      ConsentSettings(
        sub: json['sub'] as String,
        biometricRequired: json['biometric_required'] as bool,
        thresholdEur: json['threshold_eur'] as String,
        blockUnverifiedClients: json['block_unverified_clients'] as bool,
        clients: [
          for (final c in json['clients'] as List)
            ClientSetting.fromJson((c as Map).cast<String, Object?>())
        ],
      );
}

/// Biatec OIDC refused a change because it loosens a setting and carried no
/// valid device-signed consent.
class ConsentRequiredException extends ApprovalException {
  const ConsentRequiredException(super.message);
}

/// The device and settings calls of Biatec OIDC (the mock in development),
/// using `Authorization: Device <token>`. Biatec OIDC has no such API yet; the
/// shapes are in `docs/required-changes-biatec-oidc.md`, rows 21 and 22.
class SettingsClient {
  SettingsClient({required this.baseUrl, http.Client? client})
      : _client = client ?? http.Client();

  final String baseUrl;
  final http.Client _client;

  static const _timeout = Duration(seconds: 15);

  Map<String, String> _auth(String token) =>
      {'Authorization': 'Device $token'};

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(_timeout);
    } on Object catch (e) {
      throw ApprovalException('Biatec OIDC is not reachable: $e',
          offline: true);
    }
  }

  static Map<String, Object?> _object(http.Response res) {
    try {
      final v = jsonDecode(res.body);
      if (v is Map) return v.cast<String, Object?>();
    } on Object {
      // Falls through.
    }
    throw ApprovalException('Unexpected response (${res.statusCode}).');
  }

  static String _detail(Map<String, Object?> json) =>
      '${json['error_description'] ?? json['error'] ?? 'error'}';

  /// `GET /devices`.
  Future<List<DeviceInfo>> devices(String token) async {
    final res = await _send(() =>
        _client.get(Uri.parse('$baseUrl/devices'), headers: _auth(token)));
    final json = _object(res);
    if (res.statusCode != 200) {
      throw ApprovalException(
          'Could not load devices (${res.statusCode}): ${_detail(json)}');
    }
    try {
      return [
        for (final d in json['devices'] as List)
          DeviceInfo.fromJson((d as Map).cast<String, Object?>())
      ];
    } on Object {
      throw const ApprovalException('Malformed device list.');
    }
  }

  /// `POST /devices/{kid}/cancel`: cancels a pending enrollment.
  Future<void> cancelDevice(String token, String kid) async {
    final res = await _send(() => _client.post(
        Uri.parse('$baseUrl/devices/${Uri.encodeComponent(kid)}/cancel'),
        headers: _auth(token)));
    if (res.statusCode == 200) return;
    final json = _object(res);
    throw ApprovalException(res.statusCode == 409
        ? 'Only a pending enrollment can be cancelled.'
        : 'Could not cancel (${res.statusCode}): ${_detail(json)}');
  }

  /// `GET /devices/settings`.
  Future<ConsentSettings> settings(String token) async {
    final res = await _send(() => _client.get(
        Uri.parse('$baseUrl/devices/settings'),
        headers: _auth(token)));
    final json = _object(res);
    if (res.statusCode != 200) {
      throw ApprovalException(
          'Could not load settings (${res.statusCode}): ${_detail(json)}');
    }
    try {
      return ConsentSettings.fromJson(json);
    } on Object {
      throw const ApprovalException('Malformed settings.');
    }
  }

  /// `PUT /devices/settings`. [consent] is the device-signed
  /// `biatec_settings_change`, required when [change] loosens anything.
  Future<ConsentSettings> change(String token, Map<String, Object?> change,
      {Map<String, Object?>? consent}) async {
    final res = await _send(() => _client.put(
          Uri.parse('$baseUrl/devices/settings'),
          headers: {..._auth(token), 'Content-Type': 'application/json'},
          body: jsonEncode({'change': change, if (consent != null) 'consent': consent}),
        ));
    final json = _object(res);
    if (res.statusCode == 200) {
      try {
        return ConsentSettings.fromJson(
            (json['settings'] as Map).cast<String, Object?>());
      } on Object {
        throw const ApprovalException('Malformed settings.');
      }
    }
    if (json['reason'] == 'consent_required') {
      throw ConsentRequiredException(_detail(json));
    }
    throw ApprovalException(
        'The change was refused (${res.statusCode}): ${_detail(json)}');
  }
}
