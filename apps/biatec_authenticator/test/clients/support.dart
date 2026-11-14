// Test support for the user-held trust settings: a scripted Biatec OIDC that answers as issues 27 and 30 describe (it alone decides which change loosens and answers
// `403 consent_required`; the app under test has no classifier), and a rig that wires the controller to it and to a software key.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/support.dart';
import '../settings/support.dart' show FakeGate;

/// One client of the settings document as Biatec OIDC lists it. [extra] members (an operator's display name, a `verified` flag) are what a hostile or old server could add: the app must not draw them.
Map<String, Object?> trustClient(String id,
        {String? label,
        String? domain,
        int? linkedAt = 1792000000,
        bool alwaysAsk = false,
        String? threshold,
        List<Map<String, Object?>> pauses = const [],
        List<Map<String, Object?>> apps = const [],
        Map<String, Object?> extra = const {}}) =>
    {
      'client_id': id,
      'label': label,
      'fingerprint': fingerprintOfClientId(id),
      'domain_proven': domain != null,
      if (domain != null) 'domain': domain,
      'linked_at': linkedAt,
      'always_ask': alwaysAsk,
      'per_client_threshold_eur': threshold,
      'paused_until': pauses.isEmpty
          ? null
          : pauses.map((p) => p['paused_until'] as int).reduce((a, b) => a > b ? a : b),
      'pauses': pauses,
      'trusted_applications': apps,
      ...extra,
    };

class FakeTrustOidc {
  FakeTrustOidc() {
    settings = {
      'sub': 'pw_alice',
      'biometric_required': true,
      'threshold_eur': '1000.00',
      'default_threshold_eur': '1000.00',
      'ask_on_test_networks': true,
      'block_unproven_clients': false,
      'clients': [
        trustClient('biatec-chat', domain: 'chat.biatec.io', label: 'My chat'),
        trustClient('bank-example',
            domain: 'bank.example',
            apps: [
              {'network': 'algorand-mainnet', 'app_id': 1234567},
              {'network': 'algorand-testnet', 'app_id': 7654321},
            ]),
        trustClient('unverified-app', extra: {
          'display_name': 'Totally Your Bank',
          'verified': true,
          'entity_id': 'bank-entity',
        }),
      ],
    };
  }

  static const token = 'dev-token';
  int nowSeconds = 1792425600;
  late Map<String, Object?> settings;

  /// A deployment's `Consent:Policy:MaxThresholdEur`.
  String? maxThresholdEur;

  /// The device is inactive for the server too: any change is `403 device_inactive`.
  bool deviceInactive = false;

  /// Every PUT body, in order.
  final List<Map<String, Object?>> puts = [];
  final Set<String> _spent = {};

  List<Map<String, Object?>> get clients =>
      (settings['clients'] as List).cast<Map<String, Object?>>();
  Map<String, Object?> clientById(String id) =>
      clients.firstWhere((c) => c['client_id'] == id);

  http.Response _json(int status, Object body) => http.Response(
      jsonEncode(body), status,
      headers: {'content-type': 'application/json'});

  http.Client get client => MockClient((req) async {
        if (req.headers['Authorization'] != 'Device $token') {
          return _json(401, {'error': 'invalid_token'});
        }
        final path = req.url.path;
        if (req.method == 'GET' && path == '/devices') {
          return _json(200, {
            'devices': [
              {'kid': 'dev-1', 'current': true, 'active': !deviceInactive, 'active_at': 0}
            ]
          });
        }
        if (req.method == 'GET' && path == '/devices/settings') {
          return _json(200, settings);
        }
        if (req.method == 'PUT' && path == '/devices/settings') {
          return _put((jsonDecode(req.body) as Map).cast<String, Object?>());
        }
        return _json(404, {'error': 'not_found'});
      });

  // --- the server's classifier (issue 30), written here and never in the app ---

  int _cents(String v) {
    final p = v.split('.');
    return int.parse(p[0]) * 100 + (p.length > 1 ? int.parse(p[1].padRight(2, '0')) : 0);
  }

  bool _loosening(Map<String, Object?> change) {
    if (change.containsKey('threshold_eur') &&
        _cents(change['threshold_eur'] as String) >
            _cents(settings['threshold_eur'] as String)) {
      return true;
    }
    if (change['block_unproven_clients'] == false &&
        settings['block_unproven_clients'] == true) {
      return true;
    }
    if (change['ask_on_test_networks'] == false &&
        settings['ask_on_test_networks'] == true) {
      return true;
    }
    final clients = (change['clients'] as Map?)?.cast<String, Object?>() ?? {};
    for (final e in clients.entries) {
      final have = clientById(e.key);
      final c = (e.value as Map).cast<String, Object?>();
      if (c['always_ask'] == false && have['always_ask'] == true) return true;
      if (c['paused'] == false) return true;
      if (c.containsKey('trusted_applications_add')) return true;
      if (c.containsKey('per_client_threshold_eur')) {
        final account = settings['threshold_eur'] as String;
        final now = (have['per_client_threshold_eur'] as String?) ?? account;
        final next = (c['per_client_threshold_eur'] as String?) ?? account;
        if (_cents(next) > _cents(now)) return true;
      }
    }
    return false;
  }

  http.Response _put(Map<String, Object?> body) {
    puts.add(body);
    final change = (body['change'] as Map).cast<String, Object?>();
    if (deviceInactive) {
      return _json(403, {
        'error': 'device_inactive',
        'error_description': 'this device is not active yet'
      });
    }
    final threshold = change['threshold_eur'] as String?;
    final max = maxThresholdEur;
    if (threshold != null && max != null && _cents(threshold) > _cents(max)) {
      return _json(400, {
        'error': 'invalid_request',
        'error_description': 'threshold_eur must be at most $max'
      });
    }
    final loosening = _loosening(change);
    if (loosening) {
      final reject = _json(403, {
        'error': 'insufficient_user_authentication',
        'acr_values': 'biatec:consent',
        'reason': 'consent_required',
        'error_description': 'this change needs approval on your device'
      });
      final consent = body['consent'];
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
          _spent.add('${signed['nonce']}');
      if (!ok) return reject;
    }
    _apply(change);
    return _json(200,
        {'applied': true, 'loosened': loosening, 'settings': settings});
  }

  void _apply(Map<String, Object?> change) {
    for (final k in [
      'threshold_eur',
      'block_unproven_clients',
      'ask_on_test_networks',
      'biometric_required'
    ]) {
      if (change.containsKey(k)) {
        settings[k] = k == 'threshold_eur'
            ? _two(change[k] as String)
            : change[k];
      }
    }
    final per = (change['clients'] as Map?)?.cast<String, Object?>() ?? {};
    for (final e in per.entries) {
      final d = (e.value as Map).cast<String, Object?>();
      if (d['unlink'] == true) {
        clients.removeWhere((c) => c['client_id'] == e.key);
        continue;
      }
      final c = clientById(e.key);
      for (final k in ['always_ask', 'label']) {
        if (d.containsKey(k)) c[k] = d[k];
      }
      if (d.containsKey('per_client_threshold_eur')) {
        final v = d['per_client_threshold_eur'] as String?;
        c['per_client_threshold_eur'] = v == null ? null : _two(v);
      }
      final apps = (c['trusted_applications'] as List).cast<Map<String, Object?>>();
      for (final a in (d['trusted_applications_remove'] as List? ?? const [])) {
        apps.removeWhere((x) =>
            x['network'] == (a as Map)['network'] && x['app_id'] == a['app_id']);
      }
      for (final a in (d['trusted_applications_add'] as List? ?? const [])) {
        apps.add((a as Map).cast<String, Object?>());
      }
      if (d['paused'] == false) {
        final pauses = (c['pauses'] as List).cast<Map<String, Object?>>();
        pauses.removeWhere((p) => p['origin'] == d['origin']);
        c['paused_until'] = pauses.isEmpty
            ? null
            : pauses.map((p) => p['paused_until'] as int).reduce((a, b) => a > b ? a : b);
      }
    }
  }

  static String _two(String v) {
    final p = v.split('.');
    return '${p[0]}.${(p.length > 1 ? p[1] : '').padRight(2, '0')}';
  }
}

/// Everything the trust screens need, wired to [oidc] and a software key. [keys] counts every prompt of the approval key.
class TrustRig {
  TrustRig({FakeTrustOidc? oidc, DateTime? activeAt})
      : oidc = oidc ?? FakeTrustOidc() {
    enrollment.write(EnrollmentRecord(
      kid: 'dev-1',
      presenceKid: 'dev-1-presence',
      presenceAttestation: 'p.p.s',
      deviceToken: FakeTrustOidc.token,
      activeAt: activeAt ?? DateTime.utc(2020),
      attestation: 'att',
    ));
    services = SettingsServices(
      client: SettingsClient(baseUrl: 'http://oidc.test', client: this.oidc.client),
      keys: keys,
      enrollment: enrollment,
      gate: gate,
      trustSettings: true,
      clock: () => DateTime.fromMillisecondsSinceEpoch(this.oidc.nowSeconds * 1000,
          isUtc: true),
      nonce: () => 'nonce-trust-${++_nonces}-abcdef',
    );
  }

  final FakeTrustOidc oidc;
  final keys = SoftwareKeyStore(deviceTestKey(),
      approvalKid: 'dev-1', presenceKid: 'dev-1-presence');
  final disk = MemoryEnrollmentStore();
  late final enrollment = ObservableEnrollmentStore(disk);
  final gate = FakeGate();
  late final SettingsServices services;
  int _nonces = 0;

  SettingsController controller() => services.controller();
}
