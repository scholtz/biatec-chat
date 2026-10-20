import 'dart:convert';
import 'dart:math';

import 'package:flutter/foundation.dart';

import '../approvals/approval_client.dart';
import '../approvals/approval_services.dart';
import '../approvals/biometric_gate.dart';
import '../approvals/jcs.dart';
import '../config.dart';
import '../enrollment/enrollment_state.dart';
import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import 'settings_client.dart';

/// Parses a decimal EUR string; null when it is not a number.
double? _eur(String? v) => v == null ? null : double.tryParse(v);

/// True when [change] loosens anything in [current]: a higher threshold, a
/// requirement switched off, or a pause lifted. Loosening needs a consent
/// signed on this device; tightening applies at once. Anything this cannot
/// read counts as loosening, so the safe path is taken.
bool isLoosening(ConsentSettings current, Map<String, Object?> change) {
  var loosening = false;
  final threshold = change['threshold_eur'];
  if (threshold != null) {
    final next = _eur(threshold as String?);
    final now = _eur(current.thresholdEur);
    if (next == null || now == null || next > now) loosening = true;
  }
  if (change['biometric_required'] == false && current.biometricRequired) {
    loosening = true;
  }
  if (change['block_unverified_clients'] == false &&
      current.blockUnverifiedClients) {
    loosening = true;
  }
  final clients = change['clients'];
  if (clients is Map) {
    for (final entry in clients.entries) {
      final id = entry.key as String;
      final c = (entry.value as Map).cast<String, Object?>();
      final have = current.client(id);
      if (have == null) {
        loosening = true;
        continue;
      }
      if (c['always_ask'] == false && have.alwaysAsk) loosening = true;
      if (c['paused'] == false && have.pausedUntil != null) loosening = true;
      if (c.containsKey('per_client_threshold_eur')) {
        final account = _eur(current.thresholdEur);
        final now = _eur(have.perClientThresholdEur) ?? account;
        final raw = c['per_client_threshold_eur'] as String?;
        final next = raw == null ? account : _eur(raw);
        if (next == null || now == null || next > now) loosening = true;
      }
    }
  }
  return loosening;
}

enum SettingsPhase { loading, ready, unavailable }

/// Everything the settings screens need, built once and replaced with fakes
/// in tests.
class SettingsServices {
  SettingsServices({
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.gate,
    this.enabled = true,
    DateTime Function()? clock,
    String Function()? nonce,
  })  : clock = clock ?? DateTime.now,
        nonce = nonce ?? _randomNonce;

  /// Shares the key store, enrollment and biometric gate of the approvals.
  factory SettingsServices.fromConfig(
          AppConfig config, ApprovalServices approvals) =>
      SettingsServices(
        client: SettingsClient(baseUrl: config.oidcBaseUrl),
        keys: approvals.keys,
        enrollment: approvals.enrollment,
        gate: approvals.gate,
        enabled: config.settingsApi,
        clock: approvals.clock,
      );

  final SettingsClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final BiometricGate gate;

  /// False when the Biatec OIDC in use has no device and settings API yet
  /// (see `AppConfig.settingsApi`); the screens then say so and call nothing.
  final bool enabled;
  final DateTime Function() clock;
  final String Function() nonce;

  SettingsController controller() => SettingsController(
        client: client,
        keys: keys,
        enrollment: enrollment,
        gate: gate,
        clock: clock,
        nonce: nonce,
      );
}

String _randomNonce() {
  final random = Random.secure();
  return [for (var i = 0; i < 16; i++) random.nextInt(256)]
      .map((b) => b.toRadixString(16).padLeft(2, '0'))
      .join();
}

/// Loads the devices and consent settings and applies changes to them.
///
/// Tightening goes straight to Biatec OIDC. Loosening is signed on this
/// device first (biometric check, then the device key signs a
/// `biatec_settings_change`), because an identity-provider-only attacker must
/// not be able to switch the phone off.
class SettingsController extends ChangeNotifier {
  SettingsController({
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.gate,
    DateTime Function()? clock,
    String Function()? nonce,
  })  : _clock = clock ?? DateTime.now,
        _nonce = nonce ?? _randomNonce;

  final SettingsClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final BiometricGate gate;
  final DateTime Function() _clock;
  final String Function() _nonce;

  SettingsPhase _phase = SettingsPhase.loading;
  SettingsPhase get phase => _phase;

  List<DeviceInfo> _devices = const [];
  List<DeviceInfo> get devices => _devices;

  ConsentSettings? _settings;
  ConsentSettings? get settings => _settings;

  bool _busy = false;

  /// True while a call or the biometric check runs.
  bool get busy => _busy;

  String? _error;

  /// Why the last step did not go through.
  String? get error => _error;

  String? _notice;

  /// What the last step did, shown as text (never by colour alone).
  String? get notice => _notice;

  bool _offline = false;
  bool get offline => _offline;

  DateTime get now => _clock();

  bool _disposed = false;

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }

  void _changed() {
    if (!_disposed) notifyListeners();
  }

  Future<EnrollmentRecord> _record() async {
    final record = await enrollment.read();
    if (record == null) {
      throw const ApprovalException('This device is not enrolled.');
    }
    return record;
  }

  void _fail(Object e) {
    if (e is ApprovalException) {
      _error = e.message;
      _offline = e.offline;
    } else {
      _error = '$e';
      _offline = false;
    }
  }

  /// Loads devices and settings. Call on open and to retry.
  Future<void> load() async {
    if (_busy) return;
    _phase = SettingsPhase.loading;
    _error = null;
    _notice = null;
    _changed();
    try {
      final token = (await _record()).deviceToken;
      _devices = await client.devices(token);
      _settings = await client.settings(token);
      _phase = SettingsPhase.ready;
      _offline = false;
    } on Object catch (e) {
      _fail(e);
      _phase = SettingsPhase.unavailable;
    }
    _changed();
  }

  /// Cancels the pending enrollment [kid]. Cancelling is always allowed: it
  /// shuts a device out and so only tightens.
  Future<void> cancelDevice(String kid) async {
    if (_busy) return;
    _busy = true;
    _error = null;
    _notice = null;
    _changed();
    try {
      final record = await _record();
      await client.cancelDevice(record.deviceToken, kid);
      if (kid == record.kid) {
        // This device is the cancelled one: it is signed out for good.
        await enrollment.clear();
        await enrollment.clearAlias();
        try {
          await keys.delete(record.kid);
        } on Object {
          // The key is useless without an enrollment.
        }
        _devices = const [];
        _settings = null;
        _phase = SettingsPhase.unavailable;
        _error = 'This enrollment was cancelled. Enroll this device again '
            'to use it.';
      } else {
        _devices = _devices.where((d) => d.kid != kid).toList();
        _notice = 'Enrollment cancelled.';
        try {
          _devices = await client.devices(record.deviceToken);
        } on Object {
          // Best effort: the cancel went through; keep the local list.
        }
      }
    } on Object catch (e) {
      _fail(e);
    }
    _busy = false;
    _changed();
  }

  /// Applies [change] (the `change` object of `PUT /devices/settings`).
  /// Returns true when it was applied.
  Future<bool> apply(Map<String, Object?> change) async {
    final current = _settings;
    if (_busy || current == null) return false;
    _busy = true;
    _error = null;
    _notice = null;
    _changed();
    var applied = false;
    try {
      final record = await _record();
      final bool loosening = isLoosening(current, change);
      ConsentSettings next;
      if (loosening) {
        next = await _signedChange(record, current, change);
      } else {
        try {
          next = await client.change(record.deviceToken, change);
        } on ConsentRequiredException {
          // Biatec OIDC judged it a loosening after all: ask the device.
          next = await _signedChange(record, current, change);
        }
      }
      _settings = next;
      _notice = loosening
          ? 'Approved on this device and applied.'
          : 'Applied.';
      applied = true;
    } on Object catch (e) {
      _fail(e);
    }
    _busy = false;
    _changed();
    return applied;
  }

  Future<ConsentSettings> _signedChange(EnrollmentRecord record,
      ConsentSettings current, Map<String, Object?> change) async {
    if (record.activeAt.isAfter(now)) {
      throw const ApprovalException('This device is not active yet, so it '
          'cannot approve a change that lowers your protection. Changes that '
          'raise it still work.');
    }
    if (!await gate.confirm('Approve this settings change')) {
      throw const ApprovalException('Biometric check did not pass.');
    }
    final key = await keys.load(record.kid);
    if (key == null) throw const ApprovalException('The device key is missing.');
    final signed = <String, Object?>{
      'type': 'biatec_settings_change',
      'sub': current.sub,
      'kid': record.kid,
      'nonce': _nonce(),
      'issued_at': now.toUtc().millisecondsSinceEpoch ~/ 1000,
      'change': change,
    };
    final signature =
        await keys.sign(record.kid, Uint8List.fromList(canonicalBytes(signed)));
    if (signature.length != 64) {
      throw StateError('the keystore returned a ${signature.length}-byte '
          'signature; ES256 r||s is 64 bytes');
    }
    return client.change(record.deviceToken, change, consent: {
      'signed': signed,
      'alg': 'ES256',
      'signature': base64Url.encode(signature).replaceAll('=', ''),
    });
  }
}
