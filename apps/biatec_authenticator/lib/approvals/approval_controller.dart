import 'package:flutter/foundation.dart';

import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../keys/key_security_level.dart';
import 'approval_client.dart';
import 'consent_request.dart';
import 'history_store.dart';
import 'jcs.dart';
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
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final String authReqId;
  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final HistoryStore history;
  final DateTime Function() _clock;

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
      throw const ApprovalException('This device is not enrolled.');
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
                ? 'This request has expired.'
                : 'This request is no longer pending. It was already decided.');
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
      throw const ApprovalException('This device is not enrolled.');
    }
    if (record.activeAt.isAfter(now)) {
      throw const ApprovalException(
          'This device is not active yet, so it cannot decide.');
    }
    final presenceKid = record.presenceKid;
    final presenceAttestation = record.presenceAttestation;
    if (presenceKid == null || presenceAttestation == null) {
      // An install from before the two keys: its one key is not bound to the biometrics. No silent migration.
      throw const ApprovalException(
          'This device uses an old device key. Enroll it again.');
    }
    final presence = await keys.loadPresence();
    if (presence == null || presence.kid != presenceKid) {
      throw const ApprovalException(
          'The device key is missing. Enroll this device again.');
    }
    if (presence.securityLevel.blocksSigning) {
      throw const ApprovalException(KeySecurityLevel.softwareKeyMessage);
    }
    final approval = await keys.loadApproval();
    return (
      record.deviceToken,
      DeviceIdentity(
        approvalKey:
            approval != null && approval.kid == record.kid ? approval : null,
        approvalAttestation: record.attestation,
        presenceKey: presence,
        presenceAttestation: presenceAttestation,
      )
    );
  }

  /// Why this device cannot approve, if it cannot: an approval needs the approval key, in hardware and bound to the biometrics.
  String? _cannotApprove(DeviceIdentity device) {
    final key = device.approvalKey;
    if (_keysState == KeysState.approvalKeyInvalidated || key == null) {
      return ApprovalKeyInvalidated.message;
    }
    if (device.approvalAttestation == null) {
      return 'This enrollment has no attestation. Enroll the device again.';
    }
    if (key.securityLevel.blocksSigning || !key.authBound) {
      return 'This phone cannot protect approvals. Enroll this device on '
          'a phone that can.';
    }
    return null;
  }

  /// Approves with the binding [code] the requesting surface shows.
  Future<void> approve(String code) async {
    final shown = _request;
    if (_phase != ApprovalPhase.ready || shown == null) return;
    // A request this version cannot show is never approved, whatever surface asks (the screen also turns the button off).
    if (!shown.isShowable) return;
    code = code.trim();
    if (!_codeFormat.hasMatch(code)) {
      _set(ApprovalPhase.ready, error: 'Enter the six-digit code.');
      return;
    }
    if (shown.isExpired(now)) {
      _set(ApprovalPhase.expired);
      return;
    }
    _set(ApprovalPhase.working);
    try {
      final (token, device) = await _device();
      await _checkKeys();
      final blocked = _cannotApprove(device);
      if (blocked != null) {
        _set(ApprovalPhase.ready, error: blocked);
        return;
      }
      // Never approve from a cached card: fetch again, and stop if anything
      // differs from what the user looked at.
      final fresh = await client.fetch(token, authReqId);
      if (canonicalize(fresh.json) != canonicalize(shown.json)) {
        _request = fresh;
        _set(ApprovalPhase.ready,
            error: 'The request changed. Review it again before approving.');
        return;
      }
      if (fresh.isExpired(now)) {
        _set(ApprovalPhase.expired);
        return;
      }
      _lastCode = code;
      // The operating system asks for a strong biometric for this one signature: the prompt is the gate, nothing else asks. The subtitle names the client and the total, from the request that was just fetched.
      final prompt = ApprovalPrompt(
        title: ApprovalPrompt.fixedTitle,
        subtitle: '${clientLabel(fresh.client)}: ${summarize(fresh.detail)}',
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
          prompt: prompt,
        );
      } on KeyPromptCancelled {
        // Nothing was signed or sent: the request stays pending and no code attempt is used.
        _set(ApprovalPhase.ready,
            error: 'Approval cancelled. Nothing was sent.');
        return;
      } on ApprovalKeyInvalidated {
        _keysState = KeysState.approvalKeyInvalidated;
        _set(ApprovalPhase.ready, error: ApprovalKeyInvalidated.message);
        return;
      } on KeyPromptFailed catch (e) {
        _set(ApprovalPhase.ready, error: 'Could not approve: ${e.message}');
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
                null => 'Wrong code.',
                1 => 'Wrong code. 1 attempt left.',
                final n => 'Wrong code. $n attempts left.',
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
      _set(ApprovalPhase.ready, error: 'Could not approve: $e');
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
      );
      final outcome = await client.postReceipt(token, authReqId, receipt);
      if (outcome is ReceiptAccepted) {
        await _remember(request, Decision.codeFailed, receipt);
      } else {
        warning = 'The code_failed receipt was not accepted.';
      }
    } on Object catch (e) {
      warning = e is ApprovalException ? e.message : 'Could not sign: $e';
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
      _set(ApprovalPhase.ready, error: 'Could not deny: $e');
    }
  }

  /// The approval key is invalidated or gone: this device drops its keys and its enrollment, so the app leads to the enrollment screen. Biatec OIDC keeps the old device record until it is cancelled from the account (or its own rules end it).
  Future<void> startReenrollment() async {
    if (_phase == ApprovalPhase.working) return;
    try {
      await keys.deleteKeys();
    } on Object {
      // The keys are useless once the enrollment is gone.
    }
    await enrollment.clear();
  }

  void _refused(ReceiptOutcome outcome) {
    if (outcome is ReceiptRefused && outcome.status == 410) {
      _set(ApprovalPhase.expired);
    } else if (outcome is ReceiptRefused && outcome.status == 409) {
      _set(ApprovalPhase.unavailable,
          error: 'This request was already decided.');
    } else if (outcome is ReceiptRefused) {
      _set(ApprovalPhase.ready,
          error: 'Refused (${outcome.status}): ${outcome.description}');
    } else {
      _set(ApprovalPhase.ready, error: 'Unexpected answer from Biatec OIDC.');
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

/// One line for lists and history.
String summarize(AuthorizationDetail d) => switch (d) {
      TransactionDetail() => '${d.headline} (${d.valueText}) on ${d.network}',
      LimitsChangeDetail() => 'Limits to ${d.requested.perTransactionEur} EUR '
          'per transaction, ${d.requested.perDayEur} EUR per day',
    };

/// The client as rule 3 allows it to be named.
String clientLabel(ConsentClient c) => c.verified
    ? (c.legacy ? (c.displayName ?? c.domain ?? c.entityId ?? '') : c.domain!)
    : 'Unproven application ${c.fingerprintShown ?? ''}'.trim();
