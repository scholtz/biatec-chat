import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:http/http.dart' as http;

import '../approvals/approval_client.dart';
import 'device_proof.dart';
import 'security_texts.dart';
import 'pinning/pinned_http.dart';

/// What an active device needs to activate a pending one (`pairing` in `GET /devices`, required changes, "Activation by an existing device"): the new device's two key
/// thumbprints and its enrollment nonce. The pairing code is computed from them by the app ([pairingCode]); `serverCode` is the code Biatec OIDC computed, and the app
/// refuses to activate when the two differ.
class PairingData {
  const PairingData({
    required this.approvalThumbprint,
    required this.presenceThumbprint,
    required this.nonce,
    this.serverCode,
  });

  final String approvalThumbprint;
  final String presenceThumbprint;
  final String nonce;
  final String? serverCode;

  /// [json] as pairing data, or null when it is missing or malformed: one odd device then has no activation offer, instead of the whole device list failing.
  static PairingData? tryParse(Object? json) {
    if (json is! Map) return null;
    final a = json['new_approval_jwk_thumbprint'];
    final p = json['new_presence_jwk_thumbprint'];
    final n = json['nonce'];
    final c = json['pairing_code'];
    if (a is! String || p is! String || n is! String) return null;
    if (c != null && c is! String) return null;
    return PairingData(
        approvalThumbprint: a,
        presenceThumbprint: p,
        nonce: n,
        serverCode: c as String?);
  }
}

/// One enrolled device of the user, as `GET /devices` lists it.
class DeviceInfo {
  const DeviceInfo({
    required this.kid,
    required this.current,
    required this.active,
    required this.activeAt,
    this.name,
    this.pairing,
  });

  final String kid;

  /// The label the device gave itself when it enrolled, when it did.
  final String? name;

  /// Present for a pending device that has two keys: what an active device needs to activate it.
  final PairingData? pairing;

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
        name: json['name'] as String?,
        pairing: PairingData.tryParse(json['pairing']),
      );
}

/// An application the user trusts for one service: "do not ask again while the amount is under my threshold".
class TrustedApplication {
  const TrustedApplication({required this.network, required this.appId});

  final String network;
  final int appId;

  /// The member of `trusted_applications_add` / `_remove` of a settings change.
  Map<String, Object?> toJson() => {'network': network, 'app_id': appId};

  static TrustedApplication? tryParse(Object? json) {
    if (json is! Map) return null;
    final network = json['network'];
    final id = json['app_id'];
    if (network is! String || id is! int || id < 1) return null;
    return TrustedApplication(network: network, appId: id);
  }

  @override
  bool operator ==(Object other) =>
      other is TrustedApplication &&
      other.network == network &&
      other.appId == appId;

  @override
  int get hashCode => Object.hash(network, appId);
}

/// The first 16 hex characters of the SHA-256 of a `client_id` (specification section 6): used only when Biatec OIDC did not send the fingerprint.
String fingerprintOfClientId(String clientId) =>
    sha256.convert(utf8.encode(clientId)).toString().substring(0, 16);

/// A canonical origin as the signed schema and `lib.mjs` define it: scheme, lower-case host of well-formed labels, a port 1 to 65535 that is not the scheme's default.
final RegExp canonicalOrigin = RegExp(
    r'^(?!https://[^/]*:443$)(?!http://[^/]*:80$)https?://[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:([1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?$');

final RegExp _fingerprintShape = RegExp(r'^[0-9a-f]{16}$');

DateTime? _epochOrIso(Object? v) {
  if (v is num) {
    return DateTime.fromMillisecondsSinceEpoch(v.toInt() * 1000, isUtc: true);
  }
  if (v is String) return DateTime.tryParse(v)?.toUtc();
  return null;
}

/// A pause of one origin of a service (rule 10).
class PausedOrigin {
  const PausedOrigin({required this.origin, required this.pausedUntil});

  final String origin;
  final DateTime pausedUntil;
}

/// A service the user linked to the authenticator, as `GET /devices/settings` lists it.
///
/// Biatec OIDC vouches for no service: the only things shown about it are its proven domain, or its fingerprint when it has none, and the user's own [label]. There is no
/// name, no "verified" and no logo here, and a member a server sends under such a name is not read.
class ClientSetting {
  const ClientSetting({
    required this.clientId,
    required this.fingerprint,
    required this.domainProven,
    required this.alwaysAsk,
    required this.perClientThresholdEur,
    required this.pausedUntil,
    this.label,
    this.domain,
    this.linkedAt,
    this.linked = true,
    this.pauses = const [],
    this.trustedApplications = const [],
  });

  final String clientId;

  /// The user's own name for the service, or null.
  final String? label;

  /// 16 lowercase hex characters.
  final String fingerprint;
  final bool domainProven;

  /// The proven ASCII host, only when [domainProven].
  final String? domain;
  final DateTime? linkedAt;

  /// The user linked the service (approved its first request). A service can be listed without it, because it holds a setting (a label, a threshold, a running pause).
  final bool linked;
  final bool alwaysAsk;

  /// Decimal EUR string, or null to follow the account threshold.
  final String? perClientThresholdEur;

  /// Set while the 24 hour pause of rule 10 runs: the latest across origins.
  final DateTime? pausedUntil;
  final List<PausedOrigin> pauses;
  final List<TrustedApplication> trustedApplications;

  factory ClientSetting.fromJson(Map<String, Object?> json) {
    final id = json['client_id'] as String;
    final proven = json['domain_proven'] as bool? ?? false;
    final domain = json['domain'];
    final label = json['label'];
    final fingerprint = json['fingerprint'];
    final pauses = <PausedOrigin>[];
    final rawPauses = json['pauses'];
    if (rawPauses is List) {
      for (final p in rawPauses) {
        if (p is! Map) continue;
        final until = _epochOrIso(p['paused_until']);
        final origin = p['origin'];
        if (until != null && origin is String && canonicalOrigin.hasMatch(origin)) {
          pauses.add(PausedOrigin(origin: origin, pausedUntil: until));
        }
      }
    }
    final apps = <TrustedApplication>[];
    final rawApps = json['trusted_applications'];
    if (rawApps is List) {
      for (final a in rawApps) {
        final app = TrustedApplication.tryParse(a);
        if (app != null) apps.add(app);
      }
    }
    return ClientSetting(
      clientId: id,
      label: label is String && label.isNotEmpty ? label : null,
      // Only 16 lowercase hex characters is a fingerprint; anything else the server sends is never drawn as an identity.
      fingerprint: fingerprint is String && _fingerprintShape.hasMatch(fingerprint)
          ? fingerprint
          : fingerprintOfClientId(id),
      domainProven: proven && domain is String && domain.isNotEmpty,
      // A domain is taken only from a service that proved it.
      domain: proven && domain is String && domain.isNotEmpty ? domain : null,
      linkedAt: _epochOrIso(json['linked_at']),
      // An older server sends no `linked`: it listed linked clients only.
      linked: json['linked'] is bool ? json['linked'] as bool : true,
      alwaysAsk: json['always_ask'] as bool? ?? false,
      perClientThresholdEur: json['per_client_threshold_eur'] as String?,
      pausedUntil: _epochOrIso(json['paused_until']),
      pauses: pauses,
      trustedApplications: apps,
    );
  }
}

/// The user's consent settings.
class ConsentSettings {
  const ConsentSettings({
    required this.sub,
    required this.biometricRequired,
    required this.thresholdEur,
    required this.blockUnprovenClients,
    required this.clients,
    this.defaultThresholdEur,
    this.askOnTestNetworks = true,
  });

  /// The user these settings belong to; the signed change names it.
  final String sub;
  final bool biometricRequired;

  /// Decimal EUR string.
  final String thresholdEur;

  /// What an account without settings uses (the starting value), when Biatec OIDC says.
  final String? defaultThresholdEur;

  /// Ask for a test-network request whose value is unknown or above the threshold. Turning it off is loosening.
  final bool askOnTestNetworks;

  /// Refuse services without a proven domain.
  final bool blockUnprovenClients;
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
        biometricRequired: json['biometric_required'] as bool? ?? true,
        thresholdEur: json['threshold_eur'] as String,
        defaultThresholdEur: json['default_threshold_eur'] as String?,
        askOnTestNetworks: json['ask_on_test_networks'] as bool? ?? true,
        blockUnprovenClients: (json['block_unproven_clients'] ??
                json['block_unverified_clients']) as bool? ??
            false,
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
  SettingsClient({required this.baseUrl, http.Client? client, this.proof})
      : _client = client ?? PinnedHttp.clientFor(baseUrl);

  final String baseUrl;
  final http.Client _client;

  /// Adds the proof of possession of the device key to every call (BCHAT-030); null sends the bearer token alone.
  final DeviceProofSigner? proof;

  static const _timeout = Duration(seconds: 15);

  Future<Map<String, String>> _auth(String method, Uri url, String token,
          [Map<String, String> extra = const {}]) async =>
      {
        'Authorization': 'Device $token',
        ...extra,
        ...?await _proof(method, url, token),
      };

  /// The proof of possession, or null when none is configured. A proof that cannot be made is a refusal of the call, not an unexplained crash
  /// and not a silent fall back to the bearer token alone.
  Future<Map<String, String>?> _proof(String method, Uri url, String token) async {
    final signer = proof;
    if (signer == null) return null;
    try {
      return await signer.headers(method, url, token);
    } on Object catch (e) {
      throw ApprovalException(
          SecurityTexts.of().text('proofFailed', {'reason': '$e'}));
    }
  }

  Future<http.Response> _send(Future<http.Response> Function() call) async {
    try {
      return await call().timeout(_timeout);
    } on PinningException catch (e) {
      // Not "offline": retrying will not help, and the user is told to update. The same words as PinningException.message (which stays English: the enrollment screen recognises it), in the phone's language.
      throw ApprovalException(
          SecurityTexts.of().text('pinning', {'host': e.host}));
    } on Object catch (e) {
      throw ApprovalException(
          SecurityTexts.of().text('unreachable', {'reason': '$e'}),
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
    throw ApprovalException(SecurityTexts.of()
        .text('unexpected', {'status': '${res.statusCode}'}));
  }

  /// The body of a refusal as an object, or an empty one when it is not (the status is what counts then).
  static Map<String, Object?> _lenient(http.Response res) {
    try {
      return _object(res);
    } on ApprovalException {
      return const {};
    }
  }

  static String _detail(Map<String, Object?> json) =>
      '${json['error_description'] ?? json['error'] ?? 'error'}';

  /// `GET /devices`.
  Future<List<DeviceInfo>> devices(String token) async {
    final url = Uri.parse('$baseUrl/devices');
    final res = await _send(
        () async => _client.get(url, headers: await _auth('GET', url, token)));
    if (res.statusCode != 200) {
      throw ApprovalException(
          SecurityTexts.of().text('loadDevicesFailed', {
            'status': '${res.statusCode}',
            'detail': _detail(_lenient(res)),
          }),
          status: res.statusCode);
    }
    final json = _object(res);
    try {
      return [
        for (final d in json['devices'] as List)
          DeviceInfo.fromJson((d as Map).cast<String, Object?>())
      ];
    } on Object {
      throw ApprovalException(SecurityTexts.of().text('malformedDevices'));
    }
  }

  /// `POST /devices/{kid}/cancel`: cancels a pending enrollment.
  Future<void> cancelDevice(String token, String kid) async {
    final url = Uri.parse('$baseUrl/devices/${Uri.encodeComponent(kid)}/cancel');
    final res = await _send(() async =>
        _client.post(url, headers: await _auth('POST', url, token)));
    if (res.statusCode == 200) return;
    final json = _lenient(res);
    final t = SecurityTexts.of();
    throw ApprovalException(
        res.statusCode == 409
            ? t.text('onlyPending')
            : t.text('cancelFailed',
                {'status': '${res.statusCode}', 'detail': _detail(json)}),
        status: res.statusCode);
  }

  /// `POST /devices/{kid}/activate`: an active device activates the pending device [kid] with [approval], the `biatec_device_approval` it signed with its approval key
  /// (`{signed, alg, signature, device}`).
  Future<void> activateDevice(
      String token, String kid, Map<String, Object?> approval) async {
    final url =
        Uri.parse('$baseUrl/devices/${Uri.encodeComponent(kid)}/activate');
    final res = await _send(() async => _client.post(
          url,
          headers: await _auth(
              'POST', url, token, {'Content-Type': 'application/json'}),
          body: jsonEncode({'approval': approval}),
        ));
    if (res.statusCode == 200) return;
    final json = _object(res);
    final t = SecurityTexts.of();
    throw ApprovalException(res.statusCode == 409
        ? t.text('alreadyActive')
        : res.statusCode == 404
            ? t.text('noLongerWaiting')
            : t.text('activateFailed',
                {'status': '${res.statusCode}', 'detail': _detail(json)}));
  }

  /// `GET /devices/settings`.
  Future<ConsentSettings> settings(String token) async {
    final url = Uri.parse('$baseUrl/devices/settings');
    final res = await _send(
        () async => _client.get(url, headers: await _auth('GET', url, token)));
    final json = _object(res);
    if (res.statusCode != 200) {
      throw ApprovalException(SecurityTexts.of().text('loadSettingsFailed',
          {'status': '${res.statusCode}', 'detail': _detail(json)}));
    }
    try {
      return ConsentSettings.fromJson(json);
    } on Object {
      throw ApprovalException(SecurityTexts.of().text('malformedSettings'));
    }
  }

  /// `PUT /devices/settings`. [consent] is the device-signed
  /// `biatec_settings_change`, required when [change] loosens anything.
  Future<ConsentSettings> change(String token, Map<String, Object?> change,
      {Map<String, Object?>? consent}) async {
    final url = Uri.parse('$baseUrl/devices/settings');
    final res = await _send(() async => _client.put(
          url,
          headers: await _auth(
              'PUT', url, token, {'Content-Type': 'application/json'}),
          body: jsonEncode({'change': change, if (consent != null) 'consent': consent}),
        ));
    final json = _object(res);
    if (res.statusCode == 200) {
      try {
        return ConsentSettings.fromJson(
            (json['settings'] as Map).cast<String, Object?>());
      } on Object {
        throw ApprovalException(SecurityTexts.of().text('malformedSettings'));
      }
    }
    if (json['reason'] == 'consent_required') {
      throw ConsentRequiredException(_detail(json));
    }
    throw ApprovalException(SecurityTexts.of().text('refused',
        {'status': '${res.statusCode}', 'detail': _detail(json)}));
  }
}
