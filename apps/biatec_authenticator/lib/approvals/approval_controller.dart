import 'dart:ui' show Locale;

import 'package:flutter/foundation.dart';

import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../keys/key_security_level.dart';
import '../enrollment/enrollment_state.dart';
import '../clients/service_identity.dart';
import '../clients/trust_strings.dart';
import 'approval_client.dart';
import 'approval_texts.dart';
import 'biometric_gate.dart';
import 'consent_request.dart';
import 'device_identity.dart';
import 'history_store.dart';
import 'jcs.dart';
import '../keys/prompt_free_key.dart';
import 'receipt.dart';

enum ApprovalPhase {
  /// Fetching the request.
  loading,

  /// The card is shown and waits for the user.
  ready,

  /// Biometric check, signing or posting.
  working,

  approved,
  denied,

  /// The third wrong code ended the request (rule 2).
  codeFailed,

  expired,

  /// The request could not be loaded or this device cannot decide.
  unavailable,
}

/// Six digits, the binding code format (rule 2).
final _codeFormat = RegExp(r'^[0-9]{6}$');

/// A text of the approvals in the device's language: the controller has no widget tree, and the screen that shows its message is in the same language.
String _t(String key, [Map<String, Object?> args = const {}]) =>
    ApprovalTexts.of().text(key, args);

/// Drives one approval: fetch, card, code entry, OS biometric prompt, signed
/// receipt. Biatec OIDC is the authority on the code and its three attempts;
/// the controller mirrors what it answers.
class ApprovalController extends ChangeNotifier {
  ApprovalController({
    required this.authReqId,
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.history,
    required this.gate,
    this.twoKeys = true,
    this.trustSettings = false,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final String authReqId;
  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final HistoryStore history;

  /// Single-key mode only (`AppConfig.twoKeys` off): the app's own biometric check before the approval is signed. In two-key mode the OS prompt of the approval key is the gate and this is never asked.
  final BiometricGate gate;

  /// `AppConfig.twoKeys`: the two device keys. Off, the one prompt-free key signs everything and [gate] asks first (the behaviour before T-241).
  final bool twoKeys;

  /// `AppConfig.trustSettings`: the card offers "don't ask again" and an approval signs the ticked ids into `remember`. Off, nothing is ever remembered.
  final bool trustSettings;
  final DateTime Function() _clock;

  final Set<String> _remembered = <String>{};

  /// The application ids the user ticked on the card. Unticked by default: nothing is ticked for the user.
  Set<String> get remembered => Set.unmodifiable(_remembered);

  /// Ticks or clears "don't ask again" for [id], when the card offers it for that application.
  void setRemember(String id, bool on) {
    final shown = _request;
    if (!trustSettings || shown == null || _phase != ApprovalPhase.ready) {
      return;
    }
    final offered = shown.rememberable.any((a) => a.id == id);
    if (!offered) return;
    if (on ? _remembered.add(id) : _remembered.remove(id)) notifyListeners();
  }

  /// The ticked ids, as the card of [request] offers them, in the order of the request.
  List<int> _rememberFor(ConsentRequest request) => !trustSettings
      ? const []
      : [
          for (final a in request.rememberable)
            if (_remembered.contains(a.id)) int.parse(a.id)
        ];

  ApprovalPhase _phase = ApprovalPhase.loading;
  ApprovalPhase get phase => _phase;

  ConsentRequest? _request;

  /// The request exactly as last fetched from Biatec OIDC. The card renders
  /// only this.
  ConsentRequest? get request => _request;

  String? _error;

  /// Why the last step did not go through, if it did not.
  String? get error => _error;

  bool _offline = false;

  /// True when Biatec OIDC was unreachable on the last call. An offline
  /// device never approves: approval needs a live fetch right before signing.
  bool get offline => _offline;

  int? _attemptsRemaining;

  /// Attempts left as Biatec OIDC last reported them, or null before the
  /// first wrong code in this session (the server keeps the count, and it may
  /// already be lower if codes were tried earlier).
  int? get attemptsRemaining => _attemptsRemaining;

  KeysState? _keysState;

  /// What the keystore said about the keys at the last check, or null before it. [KeysState.approvalKeyInvalidated] turns Approve off; Deny keeps working.
  KeysState? get keysState => _keysState;

  /// Approve is possible as far as the keys go. (An unknown state, from a keystore that did not answer, does not turn it off: signing then fails by itself.)
  bool get canApprove => _keysState == null || _keysState == KeysState.ok;

  /// The last well-formed code typed, signed into a deny or code_failed
  /// receipt.
  String _lastCode = '';

  DateTime get now => _clock();

  bool _disposed = false;

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }

  void _set(ApprovalPhase phase, {String? error, bool offline = false}) {
    // Async steps keep running after the screen is closed.
    if (_disposed) return;
    _phase = phase;
    _error = error;
    _offline = offline;
    notifyListeners();
  }

  Future<String> _deviceToken() async {
    final record = await enrollment.read();
    if (record == null) {
      throw ApprovalException(_t('notEnrolled'));
    }
    return record.deviceToken;
  }

  /// Loads the request. Call on open and to retry.
  Future<void> load() async {
    if (_phase == ApprovalPhase.working) return;
    _set(ApprovalPhase.loading);
    try {
      final token = await _deviceToken();
      final request = await client.fetch(token, authReqId);
      // The request object carries no status: one missing from the pending
      // list was already decided (or expired) and is not offered again.
      final pending = await client.pending(token);
      if (!pending.any((p) => p.authReqId == authReqId)) {
        _request = null;
        _set(ApprovalPhase.unavailable,
            error: request.isExpired(now)
                ? _t('expired')
                : _t('noLongerPending'));
        return;
      }
      _request = request;
      await _checkKeys();
    } on ApprovalException catch (e) {
      _set(ApprovalPhase.unavailable, error: e.message, offline: e.offline);
      return;
    } on Object catch (e) {
      _set(ApprovalPhase.unavailable, error: '$e');
      return;
    }
    _set(
        _request!.isExpired(now) ? ApprovalPhase.expired : ApprovalPhase.ready);
  }

  /// Asks the keystore whether the keys are usable (no prompt). An answer that cannot be had leaves the state as it was.
  Future<void> _checkKeys() async {
    if (!twoKeys) return;
    try {
      _keysState = await keys.keysState();
    } on Object {
      // The keystore did not answer: the signing call will say what is wrong.
    }
  }

  /// The enrolled, active device that signs: its record and its keys. Throws [ApprovalException] when this device cannot decide (rule 7: an inactive device approves nothing). The approval key is null in the identity when it is invalidated or gone: the device can still deny.
  Future<(String, DeviceIdentity)> _device() async {
    final record = await enrollment.read();
    if (record == null) {
      throw ApprovalException(_t('notEnrolled'));
    }
    if (record.activeAt.isAfter(now)) {
      throw ApprovalException(_t('notActiveYet'));
    }
    if (!twoKeys) {
      // The single enrolled key is the prompt-free key: this build's presence key, or the old alias of an install from before the two keys.
      final promptFree = PromptFreeKey.of(record);
      final key = await promptFree.load(keys);
      if (key == null) {
        throw ApprovalException(_t('keyMissing'));
      }
      if (record.attestation == null) {
        throw ApprovalException(_t('noAttestation'));
      }
      if (key.securityLevel.blocksSigning) {
        throw ApprovalException(KeySecurityLevel.softwareKeyMessage);
      }
      return (
        record.deviceToken,
        DeviceIdentity(
            approvalKey: key,
            approvalAttestation: record.attestation,
            presenceKey: key,
            presenceAttestation: record.attestation!,
            legacyAlias: promptFree.legacy ? promptFree.kid : null),
      );
    }
    return (record.deviceToken, await twoKeyIdentityOf(record, keys));
  }

  /// Why this device cannot approve, if it cannot: an approval needs the approval key, in hardware and bound to the biometrics.
  String? _cannotApprove(DeviceIdentity device) {
    if (!twoKeys) {
      return device.approvalAttestation == null
          ? _t('noAttestation')
          : null;
    }
    return cannotApproveWith(device, _keysState);
  }

  /// Approves with the binding [code] the requesting surface shows.
  Future<void> approve(String code) async {
    final shown = _request;
    if (_phase != ApprovalPhase.ready || shown == null) return;
    // A request this version cannot show is never approved, whatever surface asks (the screen also turns the button off).
    if (!shown.isShowable) return;
    code = code.trim();
    if (!_codeFormat.hasMatch(code)) {
      _set(ApprovalPhase.ready, error: _t('enterCode'));
      return;
    }
    if (shown.isExpired(now)) {
      _set(ApprovalPhase.expired);
      return;
    }
    _set(ApprovalPhase.working);
    try {
      final (token, device) = await _device();
      // No keystore probe here: the state was read when the card loaded, and the signature itself reports an invalidated key before any prompt is shown.
      final blocked = _cannotApprove(device);
      if (blocked != null) {
        _set(ApprovalPhase.ready, error: blocked);
        return;
      }
      // Single-key mode: the app's own biometric check (two-key mode: the OS prompt of the signature is the check).
      if (!twoKeys && !await gate.confirm(_t('promptTitle'))) {
        _set(ApprovalPhase.ready, error: _t('biometricFailed'));
        return;
      }
      // Never approve from a cached card: fetch again, and stop if anything
      // differs from what the user looked at.
      final fresh = await client.fetch(token, authReqId);
      if (canonicalize(fresh.json) != canonicalize(shown.json)) {
        _request = fresh;
        // Nothing is ticked for the user after a review again.
        _remembered.clear();
        _set(ApprovalPhase.ready,
            error: _t('requestChanged'));
        return;
      }
      if (fresh.isExpired(now)) {
        _set(ApprovalPhase.expired);
        return;
      }
      _lastCode = code;
      // The operating system asks for a strong biometric for this one signature: the prompt is the gate, nothing else asks. The subtitle names the client and the total, from the request that was just fetched.
      final prompt = ApprovalPrompt(
        title: _t('promptTitle'),
        subtitle: '${clientLabel(fresh.client)}: ${promptSummary(fresh.detail)}',
      );
      final Map<String, Object?> receipt;
      try {
        receipt = await signReceipt(
          keys: keys,
          device: device,
          request: fresh,
          code: code,
          decision: Decision.approve,
          now: now,
          clock: () => now,
          prompt: prompt,
          singleKey: !twoKeys,
          remember: _rememberFor(fresh),
        );
      } on KeyPromptCancelled {
        // Nothing was signed or sent: the request stays pending and no code attempt is used.
        _set(ApprovalPhase.ready, error: _t('approvalCancelled'));
        return;
      } on ApprovalKeyInvalidated {
        _keysState = KeysState.approvalKeyInvalidated;
        _set(ApprovalPhase.ready, error: ApprovalKeyInvalidated.message);
        return;
      } on KeyPromptFailed catch (e) {
        _set(ApprovalPhase.ready,
            error: _t('couldNotApprove', {'why': e.message}));
        return;
      }
      final outcome = await client.postReceipt(token, authReqId, receipt);
      switch (outcome) {
        case ReceiptAccepted(status: 'approved'):
          await _remember(fresh, Decision.approve, receipt);
          _set(ApprovalPhase.approved);
        case WrongCode(:final attemptsRemaining):
          _attemptsRemaining = attemptsRemaining;
          _set(ApprovalPhase.ready,
              error: switch (attemptsRemaining) {
                null => _t('wrongCode'),
                1 => _t('wrongCodeOne'),
                final n => _t('wrongCodeN', {'n': n}),
              });
        case CodeFailed():
          _attemptsRemaining = 0;
          await _finishCodeFailed(token, device, fresh);
        case ReceiptAccepted():
        case ReceiptRefused():
          _refused(outcome);
      }
    } on ApprovalException catch (e) {
      _set(ApprovalPhase.ready, error: e.message, offline: e.offline);
    } on Object catch (e) {
      _set(ApprovalPhase.ready, error: _t('couldNotApprove', {'why': '$e'}));
    }
  }

  /// The server ended the request after the third wrong code: the device
  /// records that with a signed code_failed receipt.
  Future<void> _finishCodeFailed(
      String token, DeviceIdentity device, ConsentRequest request) async {
    String? warning;
    try {
      final receipt = await signReceipt(
        keys: keys,
        device: device,
        request: request,
        code: _lastCode,
        decision: Decision.codeFailed,
        now: now,
        singleKey: !twoKeys,
      );
      final outcome = await client.postReceipt(token, authReqId, receipt);
      if (outcome is ReceiptAccepted) {
        await _remember(request, Decision.codeFailed, receipt);
      } else {
        warning = _t('codeFailedNotAccepted');
      }
    } on Object catch (e) {
      warning = e is ApprovalException
          ? e.message
          : _t('couldNotSign', {'why': '$e'});
    }
    // The request is over either way; Biatec OIDC already refused it.
    _set(ApprovalPhase.codeFailed, error: warning);
  }

  /// Denies. Deny is signed like approve (rule 10) but needs no code and no
  /// biometric check: refusing must always be easy. It is signed by the
  /// presence key, so it works when the approval key is invalidated too.
  Future<void> deny() async {
    final shown = _request;
    if (_phase != ApprovalPhase.ready || shown == null) return;
    // A receipt over a request that cannot be shown could not be a valid one.
    if (shown.detail is UnsupportedDetail) return;
    _set(ApprovalPhase.working);
    try {
      final (token, device) = await _device();
      // The receipt must restate the stored request, so sign what Biatec
      // OIDC holds now.
      // The card keeps what the user saw; a deny of a changed request is
      // still a deny.
      final fresh = await client.fetch(token, authReqId);
      final receipt = await signReceipt(
        keys: keys,
        device: device,
        request: fresh,
        code: _lastCode,
        decision: Decision.deny,
        now: now,
        singleKey: !twoKeys,
      );
      final outcome = await client.postReceipt(token, authReqId, receipt);
      switch (outcome) {
        case ReceiptAccepted(status: 'denied'):
          await _remember(fresh, Decision.deny, receipt);
          _set(ApprovalPhase.denied);
        case CodeFailed():
          // Already ended by wrong codes; record it if a code was typed here.
          _attemptsRemaining = 0;
          if (_lastCode.isEmpty) {
            _set(ApprovalPhase.codeFailed);
          } else {
            await _finishCodeFailed(token, device, fresh);
          }
        default:
          _refused(outcome);
      }
    } on ApprovalException catch (e) {
      _set(ApprovalPhase.ready, error: e.message, offline: e.offline);
    } on Object catch (e) {
      _set(ApprovalPhase.ready, error: _t('couldNotDeny', {'why': '$e'}));
    }
  }

  /// The approval key is invalidated or gone: this device drops its keys and its enrollment, so the app leads to the enrollment screen. Biatec OIDC keeps the old device record until it is cancelled from the account (or its own rules end it).
  Future<void> startReenrollment() async {
    if (_phase == ApprovalPhase.working) return;
    EnrollmentRecord? record;
    try {
      record = await enrollment.read();
    } on Object {
      record = null;
    }
    // Also the key under the old alias of an install from before the two keys.
    await deleteDeviceKeys(keys, record);
    await enrollment.clear();
  }

  void _refused(ReceiptOutcome outcome) {
    if (outcome is ReceiptRefused && outcome.status == 410) {
      _set(ApprovalPhase.expired);
    } else if (outcome is ReceiptRefused && outcome.status == 409) {
      _set(ApprovalPhase.unavailable, error: _t('alreadyDecided'));
    } else if (outcome is ReceiptRefused) {
      _set(ApprovalPhase.ready,
          error: _t('refused',
              {'status': outcome.status, 'why': outcome.description}));
    } else {
      _set(ApprovalPhase.ready, error: _t('unexpectedAnswer'));
    }
  }

  Future<void> _remember(ConsentRequest request, Decision decision,
      Map<String, Object?> receipt) async {
    try {
      await history.add(HistoryEntry(
        authReqId: request.authReqId,
        decision: decision.wire,
        summary: summarize(request.detail),
        clientLabel: clientLabel(request.client),
        decidedAt: now,
        receipt: receipt,
      ));
    } on Object {
      // History is a convenience; the server holds the receipt.
    }
  }
}

/// One line for lists and history, in the language of [locale] (the device's when null).
String summarize(AuthorizationDetail d, [Locale? locale]) {
  final t = ApprovalTexts.of(locale);
  return switch (d) {
    TransactionDetail() => t.text('summaryTransaction', {
        'headline': d.headlineIn(locale),
        'value': d.valueTextIn(locale),
        'network': d.network,
      }),
    LimitsChangeDetail() => t.text('summaryLimits', {
        'perTransaction': d.requested.perTransactionEur,
        'perDay': d.requested.perDayEur,
      }),
    // Never free text a service wrote: that is drawn only in the area that says who wrote it. The amount and the currency passed the schema.
    PaymentDetail() =>
      t.text('summaryPayment', {'amount': d.amount, 'currency': d.currency}),
    LoginDetail() => t.text('signIn'),
    ActionDetail() => t.text('actionRequested'),
    UnsupportedDetail() => t.text('summaryCannotShow'),
  };
}

/// The line under the title of the biometric prompt of the operating system: the app's own words, and for a payment the amount and the currency. Never text a service wrote.
String promptSummary(AuthorizationDetail d) => switch (d) {
      PaymentDetail() =>
        _t('promptPayment', {'amount': d.amount, 'currency': d.currency}),
      LoginDetail() => _t('signIn'),
      ActionDetail() => _t('promptAction'),
      _ => summarize(d),
    };

/// The client as rule 3 allows it to be named: its proven domain, or "Unproven application" and the fingerprint.
String clientLabel(ConsentClient c, [Locale? locale]) {
  final host = provenDomainOf(c.domainProven, c.domain);
  final unproven = TrustStrings.of(locale).t('unprovenApplication');
  return host ?? '$unproven ${formatFingerprint(c.fingerprint)}'.trim();
}
