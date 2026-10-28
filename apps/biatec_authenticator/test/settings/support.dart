// Test support for the device, trusted-client and security screens: a scripted
// Biatec OIDC that follows the rules of the mock (tightening applies at once,
// loosening needs a device-signed biatec_settings_change), and fakes for the
// device.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/support.dart';

class FakeGate implements BiometricGate {
  bool pass = true;
  int asked = 0;
  @override
  Future<bool> confirm(String reason) async {
    asked++;
    return pass;
  }
}

Map<String, Object?> clientJson(String id,
        {String label = 'Biatec Chat',
        bool verified = true,
        bool alwaysAsk = false,
        String? threshold,
        int? pausedUntil}) =>
    {
      'client_id': id,
      'label': label,
      'verified': verified,
      'always_ask': alwaysAsk,
      'per_client_threshold_eur': threshold,
      'paused_until': pausedUntil,
    };

class FakeSettingsOidc {
  FakeSettingsOidc() {
    settings = {
      'sub': 'pw_alice',
      'biometric_required': true,
      'threshold_eur': '1000.00',
      'block_unverified_clients': false,
      'clients': [
        clientJson('biatec-chat'),
        clientJson('bank-example', label: 'Example Bank'),
        clientJson('unverified-app',
            label: 'Unverified application 3F9A-1C77', verified: false),
      ],
    };
  }

  static const token = 'dev-token';

  /// Epoch seconds the fake treats as now.
  int nowSeconds = 1792425600;

  List<Map<String, Object?>> devices = [
    {'kid': 'dev-1', 'current': true, 'active': true, 'active_at': 0},
    {
      'kid': 'dev-2',
      'current': false,
      'active': false,
      'active_at': 1792425600 + 86400
    },
  ];
  late Map<String, Object?> settings;

  bool offline = false;

  /// Makes GET /devices fail with a 503 (a refresh that fails after a cancel).
  bool failDevicesList = false;

  /// Answers every change with "consent required" unless it is signed, even
  /// a tightening, to model a server that judges differently.
  bool alwaysRequireConsent = false;

  /// Every PUT /devices/settings body, in order.
  final List<Map<String, Object?>> puts = [];
  final List<String> cancelled = [];
  final Set<String> _spentNonces = {};

  List<Map<String, Object?>> get _clients =>
      (settings['clients'] as List).cast<Map<String, Object?>>();

  Map<String, Object?> clientById(String id) =>
      _clients.firstWhere((c) => c['client_id'] == id);

  http.Response _json(int status, Object body) =>
      http.Response(jsonEncode(body), status,
          headers: {'content-type': 'application/json'});

  http.Client get client => MockClient((req) async {
        if (offline) throw http.ClientException('network is unreachable');
        if (req.headers['Authorization'] != 'Device $token') {
          return _json(401, {'error': 'invalid_token'});
        }
        final path = req.url.path;
        if (req.method == 'GET' && path == '/devices') {
          if (failDevicesList) return _json(503, {'error': 'unavailable'});
          return _json(200, {'devices': devices});
        }
        if (req.method == 'POST' && path.endsWith('/cancel')) {
          final kid = path.split('/')[2];
          final i = devices.indexWhere((d) => d['kid'] == kid);
          if (i < 0) return _json(404, {'error': 'not_found'});
          if (devices[i]['active'] == true) {
            return _json(409, {'error': 'conflict'});
          }
          devices.removeAt(i);
          cancelled.add(kid);
          return _json(200, {'kid': kid, 'cancelled': true});
        }
        if (req.method == 'GET' && path == '/devices/settings') {
          return _json(200, settings);
        }
        if (req.method == 'PUT' && path == '/devices/settings') {
          return _put((jsonDecode(req.body) as Map).cast<String, Object?>());
        }
        return _json(404, {'error': 'not_found'});
      });

  http.Response _put(Map<String, Object?> body) {
    puts.add(body);
    final change = (body['change'] as Map).cast<String, Object?>();
    final consent = body['consent'];
    final loosening = alwaysRequireConsent ||
        isLoosening(ConsentSettings.fromJson(settings), change);
    if (loosening) {
      final reject = _json(403, {
        'error': 'insufficient_user_authentication',
        'reason': 'consent_required',
        'error_description': 'loosening needs a device-signed change',
      });
      if (consent is! Map) return reject;
      final signed = (consent['signed'] as Map).cast<String, Object?>();
      final ok = signed['type'] == 'biatec_settings_change' &&
          signed['sub'] == settings['sub'] &&
          canonicalize(signed['change']) == canonicalize(change) &&
          verifyEs256(
              deviceTestKey(),
              canonicalBytes(signed),
              base64Url.decode(
                  base64Url.normalize(consent['signature'] as String))) &&
          _spentNonces.add('${signed['nonce']}');
      if (!ok) return reject;
    }
    _apply(change);
    return _json(
        200, {'applied': true, 'loosened': loosening, 'settings': settings});
  }

  void _apply(Map<String, Object?> change) {
    for (final k in [
      'threshold_eur',
      'biometric_required',
      'block_unverified_clients'
    ]) {
      if (change.containsKey(k)) settings[k] = change[k];
    }
    final clients = (change['clients'] as Map?)?.cast<String, Object?>() ?? {};
    for (final e in clients.entries) {
      final c = clientById(e.key);
      final d = (e.value as Map).cast<String, Object?>();
      if (d.containsKey('always_ask')) c['always_ask'] = d['always_ask'];
      if (d.containsKey('per_client_threshold_eur')) {
        c['per_client_threshold_eur'] = d['per_client_threshold_eur'];
      }
      if (d['paused'] == false) c['paused_until'] = null;
    }
  }
}

/// Everything the settings screens need, wired to [oidc] and a software key.
class SettingsRig {
  SettingsRig(
      {FakeSettingsOidc? oidc, DateTime? activeAt, EnrollmentRecord? record})
      : oidc = oidc ?? FakeSettingsOidc() {
    enrollment.write(record ??
        EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: FakeSettingsOidc.token,
          activeAt: activeAt ?? DateTime.utc(2020),
          attestation: 'att',
        ));
    services = SettingsServices(
      client:
          SettingsClient(baseUrl: 'http://oidc.test', client: this.oidc.client),
      keys: keys,
      enrollment: enrollment,
      gate: gate,
      clock: () => DateTime.fromMillisecondsSinceEpoch(
          this.oidc.nowSeconds * 1000,
          isUtc: true),
      nonce: () => 'nonce-${++_nonces}',
    );
  }

  final FakeSettingsOidc oidc;
  final keys = SoftwareKeyStore(deviceTestKey());
  final enrollment = MemoryEnrollmentStore();
  final gate = FakeGate();
  late final SettingsServices services;
  int _nonces = 0;

  SettingsController controller() => services.controller();
}
