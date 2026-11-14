import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show PlatformException;

import '../integrity/attestation.dart';
import '../keys/device_key_store.dart';
import '../keys/key_security_level.dart';
import 'enrollment_proof.dart';
import 'enrollment_state.dart';
import 'observable_enrollment_store.dart';
import 'oidc_enrollment_client.dart';

/// Drives the enrollment state machine. Time is injected so the 24 h delay is
/// testable.
class EnrollmentController extends ChangeNotifier {
  EnrollmentController({
    required this.keys,
    required this.store,
    required this.client,
    DateTime Function()? clock,
    this.attestation = const PlatformAttestationProvider(),
    this.attestationTimeout = const Duration(seconds: 30),
    this.twoKeys = true,
  }) : _clock = clock ?? DateTime.now;

  final DeviceKeyStore keys;

  /// Two-key enrollment (`AppConfig.twoKeys`): both keys, the server's nonce and the enrollment proofs. Off (against a Biatec OIDC without them) one key is enrolled, the one that needs no prompt, and a challenge is asked for only when the platform attestation needs one.
  final bool twoKeys;

  /// The observable store: it keeps an enrollment whose write failed, which this controller relies on.
  final ObservableEnrollmentStore store;

  /// How long enrolling waits for the disk to confirm the new record before it says the save is still pending.
  static const storeSettleTime = Duration(seconds: 2);
  final OidcEnrollmentClient client;

  /// Asks the platform for the proof that this is the real app (Play Integrity, App Attest) at enrollment and when Biatec OIDC asks for a fresh one.
  final AttestationProvider attestation;

  /// How long getting the challenge and the platform's token may take together. The attestation is optional: past this the enrollment goes ahead without one, so a stalled Play service never holds the enrollment (and the login token and the challenge expire).
  final Duration attestationTimeout;
  final DateTime Function() _clock;

  EnrollmentState _state = EnrollmentState.notEnrolled;
  EnrollmentState get state => _state;

  KeySecurityLevel? _keyLevel;
  String? _keyLevelKid;
  bool _disposed = false;

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }

  /// A refresh that was still running when the controller was replaced or closed ends quietly.
  @override
  void notifyListeners() {
    if (_disposed) return;
    super.notifyListeners();
  }

  /// Where the enrolled device key lives, once known (null before enrollment,
  /// or when the keystore cannot be read).
  KeySecurityLevel? get keyLevel => _keyLevel;

  DateTime get now => _clock();

  void _set(EnrollmentState s) {
    _state = s;
    notifyListeners();
  }

  bool _readFailed = false;

  /// The last [refresh] could not read the store (and so reports not enrolled). The app then offers a retry rather than the enrollment flow, which would replace a valid enrollment.
  bool get readFailed => _readFailed;

  /// Bumped by [signOut]. Work that started before (a refresh, a recovery code fetch) checks it after each wait and drops what it got, so a sign-out is never undone by a slow read.
  int _session = 0;

  /// This device signed itself out (its stored enrollment was cleared on purpose): it is not enrolled from now on, without reading anything, so a storage failure cannot keep a signed-out device in the app. The record held in memory is dropped and never written back; a new enrollment found in the store by a later [refresh] is picked up as usual.
  void signOut() {
    _session++;
    // A refresh still running ends without effect; the next one starts afresh.
    _refreshing = null;
    _recoveryCode = null;
    _recoveryCodeError = null;
    // A fetch for the old enrollment that is still running must not stop the next enrollment's.
    _fetchingCode = false;
    _keyLevel = null;
    _keyLevelKid = null;
    _keysState = null;
    _set(EnrollmentState.notEnrolled);
  }

  /// Restores the persisted state. Call at startup and on a timer or resume
  /// so the inactive state turns active once the delay has passed.
  ///
  /// An enrollment whose write failed is kept and saved again by the store (see `ObservableEnrollmentStore`), which also keeps a sign-out from being undone by a late write; the controller only reports what the store says.
  ///
  /// Every call reads again (a retry must not wait on a read that hangs); a caller that repeats itself, like the enrollment screen's ticker, checks [refreshing] first.
  Future<void> refresh() {
    final next = _refresh();
    _refreshing = next;
    return next.whenComplete(() {
      if (identical(_refreshing, next)) _refreshing = null;
    });
  }

  Future<void>? _refreshing;

  /// A refresh is running.
  bool get refreshing => _refreshing != null;

  Future<void> _refresh() async {
    final p = _state.phase;
    if (p == EnrollmentPhase.generatingKey || p == EnrollmentPhase.enrolling) {
      return;
    }
    final session = _session;
    EnrollmentRecord? record;
    try {
      record = await store.read();
      _readFailed = false;
    } on Object {
      record = null; // an unreadable record counts as not enrolled
      _readFailed = true;
    }
    if (session != _session) return;
    // An enrollment started while the store was read is not undone by what the read found.
    final now = _state.phase;
    if (now == EnrollmentPhase.generatingKey ||
        now == EnrollmentPhase.enrolling) {
      return;
    }
    if (record == null) {
      // A read that failed says nothing: an enrolled device stays as it is (a locked or busy keystore), and only a store that answers "nothing" means not enrolled.
      if (_readFailed && _state.record != null) return;
      if (p != EnrollmentPhase.failed) _set(EnrollmentState.notEnrolled);
      return;
    }
    if (twoKeys && !record.hasTwoKeys) {
      // From before the two keys: its one key is not bound to the biometrics. Treated as having no keys; the device enrolls again (no silent migration). A failed attempt keeps its own error.
      _keysState = KeysState.missing;
      if (p != EnrollmentPhase.failed) {
        _set(const EnrollmentState(EnrollmentPhase.notEnrolled,
            error: legacyKeyNotice));
      }
      return;
    }
    _set(EnrollmentState.fromRecord(record, _clock()));
    if (twoKeys) await _checkKeys();
    await _ensureRecoveryCode();
    await _loadKeyLevel(record.kid);
    await _refreshAttestationIfDue(record);
  }

  /// Said on the enrollment screen for an install from before the two keys.
  static const legacyKeyNotice = 'This phone has an older device key that is '
      'not protected by your fingerprint or face. Enroll it again to approve.';

  KeysState? _keysState;

  /// What the keystore said about the two keys at the last [refresh] (null before it, or when it did not answer). `approvalKeyInvalidated` means approvals are off until the device enrolls again; `missing` means this install has no keys.
  KeysState? get keysState => _keysState;

  Future<void> _checkKeys() async {
    try {
      final state = await keys.keysState();
      if (_disposed) return;
      if (state != _keysState) {
        _keysState = state;
        notifyListeners();
      }
    } on Object {
      // The state is for display; an unreadable keystore does not stop the app.
    }
  }

  bool _attesting = false;

  /// After a failed attempt the next one waits this long, so a rejected or unavailable attestation does not repeat on every resume (Play Integrity is rate-limited).
  static const attestationRetryAfter = Duration(hours: 1);
  DateTime? _attestationRetryAt;

  /// When Biatec OIDC asked for a fresh attestation by a date that has passed, sends one. Best effort and quiet: a platform that cannot attest, or a server that is out of reach, leaves things as they are, and the next try waits [attestationRetryAfter].
  Future<void> _refreshAttestationIfDue(EnrollmentRecord record) async {
    final due = record.attestationRefreshAfter;
    final now = _clock();
    if (due == null || now.isBefore(due) || _attesting) return;
    final retry = _attestationRetryAt;
    if (retry != null && now.isBefore(retry)) return;
    _attesting = true;
    final session = _session;
    var done = false;
    try {
      if (!await attestation.supported()) return;
      final challenge =
          await client.requestChallenge(deviceToken: record.deviceToken);
      if (challenge == null) return;
      final proof =
          await attestation.token(challenge).timeout(attestationTimeout);
      if (proof == null) return;
      final next = await client.refreshAttestation(
          deviceToken: record.deviceToken,
          challenge: challenge,
          platformAttestation: proof);
      if (next == null || _disposed || session != _session) return;
      // The record may have changed while this waited (the recovery code was confirmed, say): the new date goes on the current one.
      final current = await store.read();
      if (current == null || current.kid != record.kid) return;
      await store.write(current.withAttestationRefreshAfter(next));
      done = true;
    } on Object {
      // Quiet by design.
    } finally {
      if (!done) _attestationRetryAt = _clock().add(attestationRetryAfter);
      _attesting = false;
    }
  }

  /// Reads where the key of [kid] lives, for display (after the rest of the
  /// refresh, so a slow keystore delays nothing else).
  Future<void> _loadKeyLevel(String kid) async {
    // Asked once per key, also when the keystore had nothing to say.
    if (_keyLevelKid == kid) {
      return;
    }
    final session = _session;
    try {
      final level =
          (twoKeys ? await keys.loadApproval() : await keys.loadPresence())
              ?.securityLevel;
      if (_disposed || session != _session) return;
      _keyLevel = level;
      _keyLevelKid = kid;
      notifyListeners();
    } on Object {
      // The level is for display; an unreadable keystore does not stop the app.
    }
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
    final DeviceKeys deviceKeys;
    try {
      // An install from before the two keys: its single unprotected key is removed, never migrated.
      final legacy = await store.readAlias();
      if (legacy != null) {
        try {
          await keys.deleteLegacyKey(legacy);
        } on Object {
          // Best effort: the key is useless without its enrollment.
        }
        await store.clearAlias();
      }
      deviceKeys = await keys.generateKeys();
      if (twoKeys &&
          (!deviceKeys.approval.securityLevel.isHardware ||
              !deviceKeys.approval.authBound)) {
        throw const _CannotProtectApprovals();
      }
      if (!deviceKeys.presence.securityLevel.isHardware) {
        throw _NotInHardware(deviceKeys.presence.securityLevel);
      }
    } on _CannotProtectApprovals catch (e) {
      // Not what this app promises: the keys are removed instead of enrolled.
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed, error: e.message));
      return;
    } on _NotInHardware catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed, error: e.message));
      return;
    } on PlatformException catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: e.code == 'no_biometrics'
              ? 'Set up a fingerprint or face on this phone first: approvals '
                  'need one.'
              : e.code == 'insecure_key'
                  ? const _CannotProtectApprovals().message
                  : e.code == 'key_generation_failed'
                      ? 'This phone could not create the device keys. Restart '
                          'the phone and try again.'
                      : 'Could not create the device keys: $e'));
      return;
    } on Object catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not create the device keys: $e'));
      return;
    }
    // The key that is enrolled as the device key: the approval key in two-key mode, the prompt-free key otherwise.
    final enrolled = twoKeys ? deviceKeys.approval : deviceKeys.presence;
    _keyLevel = enrolled.securityLevel;
    _keyLevelKid = enrolled.kid;
    _set(const EnrollmentState(EnrollmentPhase.enrolling));
    // The server's nonce: the enrollment proof is bound to it, and so is the platform's proof when there is one. Without a nonce there is no enrollment.
    String? challenge;
    var attest = false;
    try {
      attest = await attestation.supported();
    } on Object {
      attest = false;
    }
    // Single-key mode asks for a challenge only when the platform attestation needs one (a Biatec OIDC without the two keys has no use for it otherwise).
    if (twoKeys || attest) {
      try {
        challenge =
            await client.requestChallenge(loginToken: loginToken.trim());
      } on Object {
        challenge = null;
      }
    }
    if (twoKeys && challenge == null) {
      await _discardKeys();
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error:
              'Biatec OIDC did not give an enrollment challenge. Try again.'));
      return;
    }
    // The platform's proof, bound to the nonce. Any failure here means no proof is sent, never a made-up one.
    AttestationToken? proof;
    try {
      if (attest && challenge != null) {
        proof = await attestation.token(challenge).timeout(attestationTimeout);
      }
    } on Object {
      proof = null;
    }
    EnrollmentProofs? proofs;
    try {
      // The one biometric prompt of an enrollment: the approval key signs the proof.
      if (twoKeys) {
        proofs = await buildEnrollmentProofs(
          keys: keys,
          deviceKeys: deviceKeys,
          nonce: challenge!,
          now: _clock(),
          promptTitle: 'Enroll this phone',
          promptSubtitle: 'Approvals will need your fingerprint or face',
        );
      }
    } on KeyPromptCancelled {
      await _discardKeys();
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error: 'Enrollment cancelled. Nothing was enrolled.'));
      return;
    } on Object catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not prove possession of the device keys: $e'));
      return;
    }
    final EnrollmentRecord record;
    try {
      record = await client.enroll(
        key: enrolled,
        presenceKey: twoKeys ? deviceKeys.presence : null,
        proofs: proofs,
        loginToken: loginToken.trim(),
        approvedByKid: approvedByKid,
        name: deviceName,
        challenge: proof == null ? null : challenge,
        platformAttestation: proof,
      );
    } on Object catch (e) {
      // Nothing was enrolled, so the unused keys are removed.
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error:
              e is EnrollmentException ? e.message : 'Enrollment failed: $e'));
      return;
    }
    // The server now has the device. Never lose it over a storage hiccup: keep
    // the key, retry the write, and fall back to working from memory (refresh
    // retries the write).
    String? warning;
    var saved = false;
    try {
      await store.write(record);
      // The store works from memory and saves behind it, again and again: give the disk a moment to say whether it took it now.
      await store.flush().timeout(storeSettleTime, onTimeout: () {});
      saved = !store.unsaved;
      if (!saved) warning = 'Enrolled, but this device could not save it yet.';
    } on Object catch (e) {
      warning = 'Enrolled, but this device could not save it yet: $e';
    }
    _set(EnrollmentState.fromRecord(record, _clock(),
        error: saved ? null : warning));
    await _ensureRecoveryCode();
  }

  Future<void> _discardKeys() async {
    _keyLevel = null;
    _keyLevelKid = null;
    try {
      await keys.deleteKeys();
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
    final session = _session;
    try {
      final code = await client.requestRecoveryCode(record.deviceToken);
      // A code for a device that signed out meanwhile is not kept.
      if (session == _session) {
        _recoveryCode = code;
        _recoveryCodeError = null;
      }
    } on Object catch (e) {
      if (session == _session) {
        _recoveryCodeError = e is EnrollmentException ? e.message : '$e';
      }
    } finally {
      if (session == _session) _fetchingCode = false;
    }
    notifyListeners();
  }

  /// Drops the recovery code from memory without acknowledging it (the app locked). It is fetched again when the screen needs it.
  void forgetRecoveryCode() {
    if (_recoveryCode == null) return;
    _recoveryCode = null;
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
    final session = _session;
    try {
      await client.acknowledgeRecoveryCode(record.deviceToken);
    } on Object catch (e) {
      if (session != _session) return;
      _recoveryCodeError = e is EnrollmentException ? e.message : '$e';
      notifyListeners();
      return;
    }
    // Signed out meanwhile: the cleared enrollment is not written back.
    if (session != _session) return;
    _recoveryCode = null;
    _recoveryCodeError = null;
    final done = record.withCodeShown();
    String? warning;
    try {
      await store.write(done);
    } on Object catch (e) {
      warning = 'Could not save locally: $e';
    }
    // A write that landed after a sign-out was taken out again by the store.
    if (session != _session) return;
    _set(EnrollmentState.fromRecord(done, _clock(), error: warning));
  }
}

/// The approval key could not be made hardware-backed and bound to the biometrics.
class _CannotProtectApprovals implements Exception {
  const _CannotProtectApprovals();
  String get message => 'This phone cannot protect approvals. Its keystore '
      'cannot make a key that needs your fingerprint or face for every '
      'approval, so enrollment is refused.';
}

/// The keystore holds the key somewhere other than secure hardware.
class _NotInHardware implements Exception {
  _NotInHardware(KeySecurityLevel level)
      : message = level == KeySecurityLevel.unknown
            ? 'This device cannot say where it keeps the key, so it is not '
                'treated as secure hardware. Enrollment is refused.'
            : 'This device keeps the key in ${level.label}, not in secure '
                'hardware. Enrollment is refused.';

  final String message;
}
