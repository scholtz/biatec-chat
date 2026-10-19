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
  }) : _clock = clock ?? DateTime.now;

  static const keyAlias = 'biatec-authenticator-device-key';

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
    final record = await store.read();
    if (record == null) {
      if (p != EnrollmentPhase.failed) _set(EnrollmentState.notEnrolled);
      return;
    }
    _set(EnrollmentState.fromRecord(record, _clock()));
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
    final DeviceKey key;
    try {
      key = await keys.generate(keyAlias);
    } on Exception catch (e) {
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not create the device key: $e'));
      return;
    }
    _set(const EnrollmentState(EnrollmentPhase.enrolling));
    try {
      final record = await client.enroll(
        key: key,
        loginToken: loginToken.trim(),
        approvedByKid: approvedByKid,
        name: deviceName,
      );
      await store.write(record);
      _set(EnrollmentState.fromRecord(record, _clock()));
    } on EnrollmentException catch (e) {
      // Nothing was enrolled, so the unused key is removed.
      await keys.delete(keyAlias);
      _set(EnrollmentState(EnrollmentPhase.failed, error: e.message));
    }
  }

  /// The one-time recovery code, only while the device is active and the code
  /// has not been shown yet. Null otherwise (so never while inactive).
  String? get recoveryCodeToShow =>
      _state.phase == EnrollmentPhase.activeRecoveryPending
          ? _state.record?.recoveryCode
          : null;

  /// The user confirmed they saved the code: it is erased and never shown
  /// again.
  Future<void> acknowledgeRecoveryCode() async {
    final record = _state.record;
    if (_state.phase != EnrollmentPhase.activeRecoveryPending ||
        record == null) {
      return;
    }
    final done = record.withCodeShown();
    await store.write(done);
    _set(EnrollmentState.fromRecord(done, _clock()));
  }
}
