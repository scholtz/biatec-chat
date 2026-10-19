import 'dart:math';

import 'package:flutter/foundation.dart';

import '../keys/device_key_store.dart';
import 'enrollment_state.dart';
import 'enrollment_store.dart';
import 'oidc_enrollment_client.dart';

/// Drives the enrollment state machine. Time is injected so the 24 h delay is
/// testable.
class EnrollmentController extends ChangeNotifier {
  EnrollmentController({
    required this.keys,
    required this.store,
    required this.client,
    DateTime Function()? clock,
    String Function()? newAlias,
  })  : _clock = clock ?? DateTime.now,
        _newAlias = newAlias ?? _randomAlias;

  /// A random keystore alias, which is also the kid sent to Biatec OIDC. It
  /// must differ per device: the server rejects a kid it has already seen.
  static String _randomAlias() {
    final r = Random.secure();
    final hex = List.generate(16, (_) => r.nextInt(256))
        .map((b) => b.toRadixString(16).padLeft(2, '0'))
        .join();
    return 'biatec-authenticator-$hex';
  }

  final String Function() _newAlias;

  final DeviceKeyStore keys;
  final EnrollmentStore store;
  final OidcEnrollmentClient client;
  final DateTime Function() _clock;

  EnrollmentState _state = EnrollmentState.notEnrolled;
  EnrollmentState get state => _state;

  DateTime get now => _clock();

  void _set(EnrollmentState s) {
    _state = s;
    notifyListeners();
  }

  /// Restores the persisted state. Call at startup and on a timer or resume
  /// so the inactive state turns active once the delay has passed.
  Future<void> refresh() async {
    final p = _state.phase;
    if (p == EnrollmentPhase.generatingKey || p == EnrollmentPhase.enrolling) {
      return;
    }
    EnrollmentRecord? record;
    try {
      record = await store.read();
    } on Object {
      record = null; // an unreadable record counts as not enrolled
    }
    if (record == null) {
      final held = _state.record;
      if (held != null) {
        // Enrolled in this session but the write failed earlier: try again.
        var saved = true;
        try {
          await store.write(held);
        } on Object {
          saved = false; // keep working from memory
        }
        _set(EnrollmentState.fromRecord(held, _clock(),
            error: saved ? null : _state.error));
        await _ensureRecoveryCode();
        return;
      }
      if (p != EnrollmentPhase.failed) _set(EnrollmentState.notEnrolled);
      return;
    }
    _set(EnrollmentState.fromRecord(record, _clock()));
    await _ensureRecoveryCode();
  }

  /// Generates the key and enrolls it. [loginToken] is from a fresh login.
  Future<void> enroll({
    required String loginToken,
    String? approvedByKid,
    String? deviceName,
  }) async {
    final p = _state.phase;
    if (p != EnrollmentPhase.notEnrolled && p != EnrollmentPhase.failed) {
      return;
    }
    if (loginToken.trim().isEmpty) {
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error: 'A fresh login is required to enroll.'));
      return;
    }
    _set(const EnrollmentState(EnrollmentPhase.generatingKey));
    String? alias;
    final DeviceKey key;
    try {
      // Reuse the alias of an interrupted attempt, otherwise mint a new one.
      alias = await store.readAlias();
      if (alias == null) {
        alias = _newAlias();
        await store.writeAlias(alias);
      }
      key = await keys.generate(alias);
    } on Object catch (e) {
      await _discardKey(alias);
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not create the device key: $e'));
      return;
    }
    _set(const EnrollmentState(EnrollmentPhase.enrolling));
    final EnrollmentRecord record;
    try {
      record = await client.enroll(
        key: key,
        loginToken: loginToken.trim(),
        approvedByKid: approvedByKid,
        name: deviceName,
      );
    } on Object catch (e) {
      // Nothing was enrolled, so the unused key and alias are removed.
      await _discardKey(alias);
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: e is EnrollmentException ? e.message : 'Enrollment failed: $e'));
      return;
    }
    // The server now has the device. Never lose it over a storage hiccup: keep
    // the key, retry the write, and fall back to working from memory (refresh
    // retries the write).
    String? warning;
    var saved = false;
    for (var i = 0; i < 3 && !saved; i++) {
      try {
        await store.write(record);
        saved = true;
      } on Object catch (e) {
        warning = 'Enrolled, but this device could not save it yet: $e';
      }
    }
    _set(EnrollmentState.fromRecord(record, _clock(),
        error: saved ? null : warning));
    await _ensureRecoveryCode();
  }

  Future<void> _discardKey(String? alias) async {
    if (alias != null) {
      try {
        await keys.delete(alias);
      } on Object {
        // Best effort.
      }
    }
    try {
      await store.clearAlias();
    } on Object {
      // Best effort.
    }
  }

  String? _recoveryCode;
  bool _fetchingCode = false;
  String? _recoveryCodeError;

  /// Why the recovery code could not be fetched, if it could not.
  String? get recoveryCodeError => _recoveryCodeError;

  /// Fetches the recovery code from Biatec OIDC. The server issues it only
  /// after the device is active (rule 7), and it is held in memory only.
  Future<void> _ensureRecoveryCode() async {
    final record = _state.record;
    if (_state.phase != EnrollmentPhase.activeRecoveryPending ||
        record == null ||
        _recoveryCode != null ||
        _fetchingCode) {
      return;
    }
    _fetchingCode = true;
    try {
      _recoveryCode = await client.requestRecoveryCode(record.deviceToken);
      _recoveryCodeError = null;
    } on Object catch (e) {
      _recoveryCodeError = e is EnrollmentException ? e.message : '$e';
    } finally {
      _fetchingCode = false;
    }
    notifyListeners();
  }

  /// Retry after a failed recovery code fetch.
  Future<void> loadRecoveryCode() => _ensureRecoveryCode();

  /// The one-time recovery code, only while the device is active and the code
  /// has not been acknowledged. Null otherwise (so never while inactive).
  String? get recoveryCodeToShow =>
      _state.phase == EnrollmentPhase.activeRecoveryPending
          ? _recoveryCode
          : null;

  /// The user confirmed they saved the code: the server is told, the code is
  /// dropped from memory and it is never shown again.
  Future<void> acknowledgeRecoveryCode() async {
    final record = _state.record;
    if (_state.phase != EnrollmentPhase.activeRecoveryPending ||
        record == null) {
      return;
    }
    try {
      await client.acknowledgeRecoveryCode(record.deviceToken);
    } on Object catch (e) {
      _recoveryCodeError = e is EnrollmentException ? e.message : '$e';
      notifyListeners();
      return;
    }
    _recoveryCode = null;
    _recoveryCodeError = null;
    final done = record.withCodeShown();
    String? warning;
    try {
      await store.write(done);
    } on Object catch (e) {
      warning = 'Could not save locally: $e';
    }
    _set(EnrollmentState.fromRecord(done, _clock(), error: warning));
  }
}
