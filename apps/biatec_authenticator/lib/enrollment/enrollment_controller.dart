import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show MissingPluginException, PlatformException;

import '../integrity/attestation.dart';
import '../config.dart';
import '../keys/device_key_store.dart';
import '../security/device_proof.dart';
import '../keys/key_security_level.dart';
import '../keys/prompt_free_key.dart';
import '../approvals/approval_client.dart' show ApprovalException;
import '../security/settings_client.dart';
import 'auth_client.dart';
import 'enrollment_proof.dart';
import 'enrollment_strings.dart';
import 'pairing_code.dart';
import 'recovery_code_format.dart';
import 'enrollment_state.dart';
import 'observable_enrollment_store.dart';
import 'oidc_enrollment_client.dart';

/// How one [EnrollmentController.enroll] ended.
enum EnrollResult {
  /// The device is enrolled (active, or waiting for its activation).
  enrolled,

  /// Nothing was enrolled; [EnrollmentState.error] says why.
  failed,

  /// Biatec OIDC says the login is too old (`401 insufficient_user_authentication`, `max_age`): nothing was enrolled and a new sign-in is needed.
  staleLogin,

  /// Not started: this device is already enrolling or enrolled.
  ignored,
}

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
    this.auth,
    this.loginRequest,
    this.devices,
  }) : _clock = clock ?? DateTime.now;

  /// The one way the app builds its controller from [config] (the app gate and the enrollment screen both use it): the in-app login and the waiting device's calls exist
  /// only against a Biatec OIDC that has them (`AppConfig.inAppLogin`), and the calls carry the proof of possession of the device key ([proof], by default the keystore's).
  factory EnrollmentController.forConfig(
    AppConfig config, {
    required DeviceKeyStore keys,
    required ObservableEnrollmentStore store,
    DeviceProofSigner? proof,
    AuthClient? authClient,
    bool? twoKeys,
  }) =>
      EnrollmentController(
        keys: keys,
        store: store,
        client: OidcEnrollmentClient(baseUrl: config.oidcBaseUrl),
        twoKeys: twoKeys ?? config.twoKeys,
        auth: config.signIn ? (authClient ?? authClientFor(config)) : null,
        loginRequest: config.signIn ? AuthRequest.forConfig(config) : null,
        devices: SettingsClient(
            baseUrl: config.oidcBaseUrl,
            proof: proof ?? KeystoreDeviceProofSigner(keys: keys, enrollment: store)),
      );

  /// The system browser login (null: no in-app login, `AppConfig.inAppLogin` is off and the token comes from outside) and what it asks for.
  final AuthClient? auth;
  final AuthRequest? loginRequest;

  /// The device list and cancel calls, for the waiting device to learn that it was activated and to cancel itself. Null: no polling.
  final SettingsClient? devices;

  /// The state error of a cancelled sign-in, a failed one, and of the recovery code at enrollment (English; the screen shows the phone's language).
  static String get loginCancelledError => EnrollStrings.en.loginCancelled;
  static const loginFailedPrefix = 'Sign-in failed:';

  /// The state error on a platform without a key store (see [enroll]).
  static String get noKeyStoreError => EnrollStrings.en.noKeyStore;

  /// The state errors of a computer with no TPM, and of one where Windows Hello is not set up.
  static String get noTpmError => EnrollStrings.en.noTpm;
  static String get noHelloError => EnrollStrings.en.noHello;
  static String get helloUnavailableError => EnrollStrings.en.helloUnavailable;
  static String get recoveryWrongError => EnrollStrings.en.recoveryWrong;
  static String get recoveryLockedError => EnrollStrings.en.recoveryLocked;
  static String get staleLoginError => EnrollStrings.en.staleLoginAgain;
  static String get recoveryFormatError => EnrollStrings.en.recoveryFormat;

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
    // A desktop sign-in that waits for the browser is ended with its screen: the listener is closed and no code is redeemed for a controller that is gone.
    if (_waitingForBrowser) auth?.cancel();
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

  /// A controller that was disposed (its screen left while the browser was open) changes nothing any more.
  void _set(EnrollmentState s) {
    if (_disposed) return;
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
      // (A device that was just cancelled keeps the notice that says so.)
      if (p != EnrollmentPhase.failed && p != EnrollmentPhase.notEnrolled) {
        _set(EnrollmentState.notEnrolled);
      }
      return;
    }
    if (twoKeys && !record.hasTwoKeys) {
      // From before the two keys: its one key is not bound to the biometrics. Treated as having no keys; the device enrolls again (no silent migration). A failed attempt keeps its own error.
      if (p != EnrollmentPhase.failed) {
        _set(const EnrollmentState(EnrollmentPhase.notEnrolled,
            error: legacyKeyNotice));
      }
      return;
    }
    _set(EnrollmentState.fromRecord(record, _clock()));
    await _ensureRecoveryCode();
    await _loadKeyLevel(record.kid);
    await _refreshAttestationIfDue(record);
  }

  /// Said on the enrollment screen for an install from before the two keys.
  static const legacyKeyNotice = 'This phone has an older device key that is '
      'not protected by your fingerprint or face. Enroll it again to approve.';

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
      final record = await store.read();
      final key = twoKeys || record == null
          ? await keys.loadApproval()
          : await PromptFreeKey.of(record).load(keys);
      final level = key?.securityLevel;
      if (_disposed || session != _session) return;
      _keyLevel = level;
      _keyLevelKid = kid;
      notifyListeners();
    } on Object {
      // The level is for display; an unreadable keystore does not stop the app.
    }
  }

  /// What a new device calls itself, for the other devices to show ("Activate Android phone?"). Nothing personal: the platform only.
  static String get defaultDeviceName => switch (defaultTargetPlatform) {
        TargetPlatform.android => 'Android phone',
        TargetPlatform.iOS => 'iPhone',
        TargetPlatform.windows => 'Windows computer',
        _ => 'Biatec Authenticator',
      };

  /// Key generation on a platform with no native key store: the channel has no implementation. Only that call is read this way; a missing plugin anywhere else is an ordinary failure.
  static Future<T> _made<T>(Future<T> Function() generate) async {
    try {
      return await generate();
    } on MissingPluginException {
      throw const _NoKeyStore();
    }
  }

  bool _signingIn = false;

  /// The browser is open for the sign-in (or the enrollment that follows it runs).
  bool get signingIn => _signingIn;

  bool _waitingForBrowser = false;

  /// The sign-in waits for the browser and can be stopped with [cancelSignIn] (the desktop login; a phone's browser session ends when it is closed).
  bool get canCancelSignIn => _waitingForBrowser && (auth?.canCancel ?? false);

  /// Stops waiting for the browser: the sign-in ends as cancelled and nothing is enrolled.
  void cancelSignIn() {
    if (_waitingForBrowser) auth?.cancel();
  }

  /// Signs the user in with the system browser and enrolls this device with the token that gives. The token lives in a local variable for the enrollment calls and is dropped; it is not written to any store, and no refresh token is asked for. A login Biatec OIDC finds too old starts the sign-in again, once.
  Future<EnrollResult> signInAndEnroll(
      {String? recoveryCode, String? deviceName}) async {
    final signIn = auth;
    final request = loginRequest;
    if (signIn == null || request == null) {
      throw StateError('no in-app login is configured');
    }
    final p = _state.phase;
    if (_signingIn ||
        (p != EnrollmentPhase.notEnrolled && p != EnrollmentPhase.failed)) {
      return EnrollResult.ignored;
    }
    // A recovery code that cannot be one is refused before the browser opens: nothing is spent, and no second login is needed because of a typo.
    final typed = recoveryCode?.trim();
    final code = typed == null || typed.isEmpty ? null : normalizeRecoveryCode(typed);
    if (typed != null && typed.isNotEmpty && code == null) {
      _set(EnrollmentState(EnrollmentPhase.failed, error: recoveryFormatError));
      return EnrollResult.failed;
    }
    recoveryCode = code;
    // The guard against a second sign-in is set first: the probe below waits for native code, and a second press in that time must not start a second sign-in.
    _signingIn = true;
    notifyListeners();
    try {
      // A computer that has no key store is told so before the browser opens: a sign-in that can only end in "cannot be enrolled" is not asked of the user. The probe
      // reads what the key store can do (no prompt, nothing is made); only a missing channel and a missing TPM or Windows Hello count, any other failure is left to the enrollment itself.
      try {
        switch (await _made<KeyStoreStatus>(keys.status)) {
          case KeyStoreStatus.ready:
            break;
          case KeyStoreStatus.noTpm:
            _set(EnrollmentState(EnrollmentPhase.failed, error: noTpmError));
            return EnrollResult.failed;
          case KeyStoreStatus.noHello:
            _set(EnrollmentState(EnrollmentPhase.failed, error: noHelloError));
            return EnrollResult.failed;
          case KeyStoreStatus.helloUnavailable:
            _set(EnrollmentState(EnrollmentPhase.failed, error: helloUnavailableError));
            return EnrollResult.failed;
        }
      } on _NoKeyStore {
        _set(EnrollmentState(EnrollmentPhase.failed, error: noKeyStoreError));
        return EnrollResult.failed;
      } on Object {
        // The probe could not read the keys for another reason; the enrollment shows what is wrong, if anything is.
      }
      for (var attempt = 0;; attempt++) {
        String? token;
        try {
          _waitingForBrowser = true;
          notifyListeners();
          try {
            token = await signIn.signIn(request);
          } finally {
            _waitingForBrowser = false;
          }
          if (!_disposed) notifyListeners();
        } on LoginCancelled {
          if (_disposed) return EnrollResult.ignored;
          _set(EnrollmentState(EnrollmentPhase.failed,
              error: loginCancelledError));
          return EnrollResult.failed;
        } on LoginFailed catch (e) {
          if (_disposed) return EnrollResult.ignored;
          _set(EnrollmentState(EnrollmentPhase.failed,
              error: '$loginFailedPrefix ${e.message}'));
          return EnrollResult.failed;
        }
        // The browser flow outlived the screen: nothing is enrolled for a screen that is gone, and the token is dropped.
        if (_disposed) return EnrollResult.ignored;
        final result = await enroll(
            loginToken: token,
            recoveryCode: recoveryCode,
            deviceName: deviceName ?? defaultDeviceName);
        token = null; // dropped: the enrollment is over
        if (result != EnrollResult.staleLogin) return result;
        if (_disposed) return result;
        if (attempt >= 1) {
          _set(EnrollmentState(EnrollmentPhase.failed, error: staleLoginError));
          return EnrollResult.failed;
        }
        // The state now says "Sign in again" (a notice); the login restarts once.
      }
    } finally {
      _signingIn = false;
      if (!_disposed) notifyListeners();
    }
  }

  /// Generates the key and enrolls it. [loginToken] is from a fresh login. [recoveryCode] activates the device at once.
  Future<EnrollResult> enroll({
    required String loginToken,
    String? recoveryCode,
    String? deviceName,
  }) async {
    final p = _state.phase;
    if (p != EnrollmentPhase.notEnrolled && p != EnrollmentPhase.failed) {
      return EnrollResult.ignored;
    }
    if (loginToken.trim().isEmpty) {
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error: 'A fresh login is required to enroll.'));
      return EnrollResult.failed;
    }
    final typed = recoveryCode?.trim();
    if (typed != null && typed.isNotEmpty) {
      final code = normalizeRecoveryCode(typed);
      if (code == null) {
        _set(EnrollmentState(EnrollmentPhase.failed, error: recoveryFormatError));
        return EnrollResult.failed;
      }
      recoveryCode = code;
    } else {
      recoveryCode = null;
    }
    _set(const EnrollmentState(EnrollmentPhase.generatingKey));
    DeviceKeys? deviceKeys;
    final DeviceKey enrolled;
    try {
      // An install from before the two keys: its single unprotected key is removed when a NEW enrollment starts, never migrated.
      final legacy = await store.readAlias();
      if (legacy != null) {
        try {
          await keys.deleteLegacyKey(legacy);
        } on Object {
          // Best effort: the key is useless without its enrollment.
        }
        await store.clearAlias();
      }
      if (twoKeys) {
        final made = deviceKeys = await _made<DeviceKeys>(keys.generateKeys);
        if (!made.approval.securityLevel.isHardware ||
            !made.approval.gestureRequired) {
          throw const _CannotProtectApprovals();
        }
        if (!made.presence.securityLevel.isHardware) {
          throw _NotInHardware(made.presence.securityLevel);
        }
        enrolled = made.approval;
      } else {
        // Single-key mode: ONLY the prompt-free key is made (no approval key, no biometric requirement), exactly as before T-241.
        enrolled = await _made<DeviceKey>(keys.generatePresenceKey);
        if (!enrolled.securityLevel.isHardware) {
          throw _NotInHardware(enrolled.securityLevel);
        }
      }
    } on _CannotProtectApprovals catch (e) {
      // Not what this app promises: the keys are removed instead of enrolled.
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed, error: e.message));
      return EnrollResult.failed;
    } on _NotInHardware catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed, error: e.message));
      return EnrollResult.failed;
    } on PlatformException catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: e.code == 'no_tpm'
              ? noTpmError
              : e.code == 'no_hello'
                  ? noHelloError
                  : e.code == 'hello_unavailable'
                      ? helloUnavailableError
                  : e.code == 'no_biometrics'
              ? 'Set up a fingerprint or face on this phone first: approvals '
                  'need one.'
              : e.code == 'insecure_key'
                  ? const _CannotProtectApprovals().message
                  : e.code == 'key_generation_failed'
                      ? 'This phone could not create the device keys. Restart '
                          'the phone and try again.'
                      : 'Could not create the device keys: $e'));
      return EnrollResult.failed;
    } on _NoKeyStore {
      // A platform this app has no key store for (a desktop build): there is no protected place for a device key, so nothing is enrolled.
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed, error: noKeyStoreError));
      return EnrollResult.failed;
    } on Object catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not create the device keys: $e'));
      return EnrollResult.failed;
    }
    _keyLevel = enrolled.securityLevel;
    _keyLevelKid = enrolled.kid;
    _set(const EnrollmentState(EnrollmentPhase.enrolling));
    var attest = false;
    try {
      attest = await attestation.supported();
    } on Object {
      attest = false;
    }
    final EnrollmentRecord record;
    try {
      record = await _enrollOnServer(
        loginToken: loginToken.trim(),
        recoveryCode: recoveryCode,
        deviceName: deviceName,
        enrolled: enrolled,
        deviceKeys: deviceKeys,
        attest: attest,
      );
    } on KeyPromptCancelled {
      await _discardKeys();
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error: 'Enrollment cancelled. Nothing was enrolled.'));
      return EnrollResult.failed;
    } on _NoChallenge {
      await _discardKeys();
      _set(const EnrollmentState(EnrollmentPhase.failed,
          error:
              'Biatec OIDC did not give an enrollment challenge. Try again.'));
      return EnrollResult.failed;
    } on _ProofFailed catch (e) {
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Could not prove possession of the device keys: ${e.cause}'));
      return EnrollResult.failed;
    } on EnrollmentException catch (e) {
      // Nothing was enrolled, so the unused keys are removed.
      await _discardKeys();
      if (e.isStaleLogin) {
        // `401 insufficient_user_authentication` with `max_age`: not a failure of this device, the login has to be done again.
        _set(EnrollmentState(EnrollmentPhase.notEnrolled,
            notice: EnrollStrings.of().staleLoginNotice));
        return EnrollResult.staleLogin;
      }
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: e.isWrongRecoveryCode
              ? recoveryWrongError
              : e.isRecoveryLocked
                  ? recoveryLockedError
                  : e.message));
      return EnrollResult.failed;
    } on Object catch (e) {
      // Nothing was enrolled, so the unused keys are removed.
      await _discardKeys();
      _set(EnrollmentState(EnrollmentPhase.failed,
          error: 'Enrollment failed: $e'));
      return EnrollResult.failed;
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
    return EnrollResult.enrolled;
  }

  /// A challenge older than this is not used (Biatec OIDC lets one live 300 s; the platform's token and the biometric prompt both wait for something outside the app).
  static const challengeMaxAge = Duration(seconds: 240);

  /// Asks for the challenge, the platform's token and (two-key mode) the enrollment proofs, and enrolls. The proofs are built last, so their `iat` is fresh after the platform's wait; if the challenge may have expired meanwhile (older than [challengeMaxAge], or the server says so) a fresh one is fetched and the proofs are made again, once.
  Future<EnrollmentRecord> _enrollOnServer({
    required String loginToken,
    required String? recoveryCode,
    required String? deviceName,
    required DeviceKey enrolled,
    required DeviceKeys? deviceKeys,
    required bool attest,
  }) async {
    for (var attempt = 0;; attempt++) {
      String? challenge;
      // Single-key mode asks for a challenge only when the platform attestation needs one (a Biatec OIDC without the two keys has no use for it otherwise).
      if (twoKeys || attest) {
        try {
          challenge = await client.requestChallenge(loginToken: loginToken);
        } on EnrollmentException catch (e) {
          // A login that is too old is told to the caller; any other failure is "no challenge".
          if (e.isStaleLogin) rethrow;
          challenge = null;
        } on Object {
          challenge = null;
        }
      }
      if (twoKeys && challenge == null) throw const _NoChallenge();
      var challengeAt = _clock();
      // The platform's proof, bound to the nonce. Any failure here means no proof is sent, never a made-up one.
      AttestationToken? proof;
      try {
        if (attest && challenge != null) {
          proof =
              await attestation.token(challenge).timeout(attestationTimeout);
        }
      } on Object {
        proof = null;
      }
      // A slow platform: the nonce may be too old to use now (nothing was signed yet, so no prompt has been shown).
      if (_clock().difference(challengeAt) > challengeMaxAge && attempt == 0) {
        continue;
      }
      EnrollmentProofs? proofs;
      if (twoKeys) {
        try {
          // The one biometric prompt of an enrollment: the approval key signs the proof.
          proofs = await buildEnrollmentProofs(
            keys: keys,
            deviceKeys: deviceKeys!,
            nonce: challenge!,
            now: _clock(),
            promptTitle: 'Enroll this phone',
            promptSubtitle: 'Approvals will need your fingerprint or face',
          );
        } on KeyPromptCancelled {
          rethrow;
        } on Object catch (e) {
          throw _ProofFailed(e);
        }
        // The user took long at the prompt. The proof's `iat` is set when it is built, BEFORE the prompt (signing and then stamping is impossible: the
        // iat is part of what is signed), so it is only good for the server's window. Only when more than [challengeMaxAge] passed is it made again with a fresh
        // challenge, and the user is told that the biometric is asked a second time.
        if (_clock().difference(challengeAt) > challengeMaxAge &&
            attempt == 0) {
          _set(const EnrollmentState(EnrollmentPhase.enrolling,
              notice: 'Confirming again...'));
          continue;
        }
      }
      try {
        final record = await client.enroll(
          key: enrolled,
          presenceKey: twoKeys ? deviceKeys!.presence : null,
          proofs: proofs,
          loginToken: loginToken,
          recoveryCode: recoveryCode,
          name: deviceName,
          challenge: proof == null ? null : challenge,
          platformAttestation: proof,
        );
        // The code this device shows while it waits for another device: from its own keys and the nonce it enrolled with, never from what the server says.
        return twoKeys && challenge != null && isPairingNonce(challenge)
            ? record.copyWith(
                pairingCode: pairingCode(deviceKeys!.approval.thumbprint,
                    deviceKeys.presence.thumbprint, challenge))
            : record;
      } on EnrollmentException catch (e) {
        // The server says, by its machine-readable error code, that the challenge or the proof is no good (expired, used): one more try with a fresh
        // challenge. The user is told that the biometric is asked again (never silently).
        if (twoKeys &&
            attempt == 0 &&
            (e.code == 'invalid_challenge' || e.code == 'invalid_proof')) {
          _set(const EnrollmentState(EnrollmentPhase.enrolling,
              notice: 'Confirming again...'));
          continue;
        }
        rethrow;
      }
    }
  }

  bool _polling = false;

  /// A device that is waiting can be activated by another device. Asks Biatec OIDC whether this one is active now (call about every 5 seconds while the enrollment screen is open); when it is, the record says so and the app opens. Quiet when Biatec OIDC cannot be reached.
  Future<void> pollActivation() async {
    final record = _state.record;
    final api = devices;
    if (api == null ||
        _state.phase != EnrollmentPhase.inactive ||
        record == null ||
        _polling) {
      return;
    }
    _polling = true;
    final session = _session;
    try {
      final list = await api.devices(record.deviceToken);
      if (_disposed || session != _session) return;
      final me = list.where((d) => d.kid == record.kid).firstOrNull;
      // A device that is no longer listed was cancelled.
      if (me == null) {
        await _cancelledElsewhere(session);
        return;
      }
      if (!me.active) return;
      // A server clock a little ahead of the phone's must not leave an activated device "inactive".
      final now = _clock();
      final done =
          record.copyWith(activeAt: me.activeAt.isAfter(now) ? now : me.activeAt);
      try {
        await store.write(done);
      } on Object {
        // Kept in memory; refresh writes it again.
      }
      if (_disposed || session != _session) return;
      _set(EnrollmentState.fromRecord(done, _clock()));
      await _ensureRecoveryCode();
    } on ApprovalException catch (e) {
      // Biatec OIDC no longer knows this device's token: it was cancelled (the email link, another phone). Not a hiccup to retry.
      if (_isCancelledStatus(e.status)) await _cancelledElsewhere(session);
    } on Object {
      // Quiet: the next poll tries again.
    } finally {
      _polling = false;
    }
  }

  /// 401, 403 and 404 on a call of a waiting device: the device was cancelled (or never existed for this account).
  static bool _isCancelledStatus(int? status) =>
      status == 401 || status == 403 || status == 404;

  /// This device was cancelled elsewhere: its record and keys go, it is signed out, and the start screen says so.
  Future<void> _cancelledElsewhere(int session) async {
    await _removeLocalEnrollment();
    if (_disposed || session != _session) return;
    signOut();
    _set(EnrollmentState(EnrollmentPhase.notEnrolled,
        notice: EnrollStrings.of().deviceCancelled));
  }

  /// Removes the stored enrollment and the keys; a failure of either is not allowed to stop the sign-out.
  Future<void> _removeLocalEnrollment() async {
    final record = _state.record;
    try {
      await store.clearAlias();
    } on Object {
      // A stale alias would only be reused for the next key.
    }
    try {
      await store.clear();
    } on Object {
      // Cleared in memory; the disk is retried by the store.
    }
    if (record != null) {
      try {
        await deleteDeviceKeys(keys, record);
      } on Object {
        // The keys are useless without the enrollment; a new enrollment removes them again.
      }
    }
  }

  String? _cancelError;

  /// Why [cancelThisDevice] did not work.
  String? get cancelError => _cancelError;

  /// "Cancel this device": the pending enrollment is cancelled at Biatec OIDC (`POST /devices/{kid}/cancel`), this device's keys and record are removed and the app is back at the start. Returns false (and says why in [cancelError]) when Biatec OIDC could not be reached.
  Future<bool> cancelThisDevice() async {
    final record = _state.record;
    final api = devices;
    if (record == null || api == null) return false;
    _cancelError = null;
    var already = false;
    try {
      await api.cancelDevice(record.deviceToken, record.kid);
    } on ApprovalException catch (e) {
      // Already cancelled elsewhere (the token is no longer known): the same end, with the notice that says so.
      if (!_isCancelledStatus(e.status)) {
        _cancelError = e.message;
        notifyListeners();
        return false;
      }
      already = true;
    } on Object catch (e) {
      _cancelError = '$e';
      notifyListeners();
      return false;
    }
    // The server cancelled it: whatever the local clean-up meets, the device ends signed out (the store signs out in memory first, see ObservableEnrollmentStore.clear).
    await _removeLocalEnrollment();
    signOut();
    if (already) {
      _set(EnrollmentState(EnrollmentPhase.notEnrolled,
          notice: EnrollStrings.of().deviceCancelled));
    }
    return true;
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

class _NoChallenge implements Exception {
  const _NoChallenge();
}

class _ProofFailed implements Exception {
  const _ProofFailed(this.cause);
  final Object cause;
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

class _NoKeyStore implements Exception {
  const _NoKeyStore();
}
