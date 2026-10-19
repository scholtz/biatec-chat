import 'package:flutter/foundation.dart';

import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import 'approval_client.dart';
import 'biometric_gate.dart';
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

/// Drives one approval: fetch, card, code entry, biometric gate, signed
/// receipt. Biatec OIDC is the authority on the code and its three attempts;
/// the controller mirrors what it answers.
class ApprovalController extends ChangeNotifier {
  ApprovalController({
    required this.authReqId,
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.gate,
    required this.history,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final String authReqId;
  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final BiometricGate gate;
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
      _request = await client.fetch(await _deviceToken(), authReqId);
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

  /// The enrolled, active device that signs. Throws [ApprovalException] when
  /// this device cannot decide (rule 7: an inactive device approves nothing).
  Future<(String, DeviceIdentity)> _device() async {
    final record = await enrollment.read();
    if (record == null) {
      throw const ApprovalException('This device is not enrolled.');
    }
    if (record.activeAt.isAfter(now)) {
      throw const ApprovalException(
          'This device is not active yet, so it cannot decide.');
    }
    final attestation = record.attestation;
    if (attestation == null) {
      throw const ApprovalException(
          'This enrollment has no attestation. Enroll the device again.');
    }
    final key = await keys.load(record.kid);
    if (key == null) {
      throw const ApprovalException('The device key is missing.');
    }
    return (
      record.deviceToken,
      DeviceIdentity(key: key, attestation: attestation)
    );
  }

  /// Approves with the binding [code] the requesting surface shows.
  Future<void> approve(String code) async {
    final shown = _request;
    if (_phase != ApprovalPhase.ready || shown == null) return;
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
      if (!await gate.confirm('Approve this action')) {
        _set(ApprovalPhase.ready, error: 'Biometric check did not pass.');
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
      final receipt = await signReceipt(
        keys: keys,
        device: device,
        request: fresh,
        code: code,
        decision: Decision.approve,
        now: now,
      );
      final outcome = await client.postReceipt(token, authReqId, receipt);
      switch (outcome) {
        case ReceiptAccepted(status: 'approved'):
          await _remember(fresh, Decision.approve, receipt);
          _set(ApprovalPhase.approved);
        case WrongCode(:final attemptsRemaining):
          _attemptsRemaining = attemptsRemaining;
          _set(ApprovalPhase.ready,
              error: 'Wrong code. $attemptsRemaining '
                  '${attemptsRemaining == 1 ? 'attempt' : 'attempts'} left.');
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
    final receipt = await signReceipt(
      keys: keys,
      device: device,
      request: request,
      code: _lastCode,
      decision: Decision.codeFailed,
      now: now,
    );
    String? warning;
    try {
      final outcome = await client.postReceipt(token, authReqId, receipt);
      if (outcome is ReceiptAccepted) {
        await _remember(request, Decision.codeFailed, receipt);
      } else {
        warning = 'The code_failed receipt was not accepted.';
      }
    } on ApprovalException catch (e) {
      warning = e.message;
    }
    // The request is over either way; Biatec OIDC already refused it.
    _set(ApprovalPhase.codeFailed, error: warning);
  }

  /// Denies. Deny is signed like approve (rule 10) but needs no code and no
  /// biometric check: refusing must always be easy.
  Future<void> deny() async {
    final shown = _request;
    if (_phase != ApprovalPhase.ready || shown == null) return;
    _set(ApprovalPhase.working);
    try {
      final (token, device) = await _device();
      // The receipt must restate the stored request, so sign what Biatec
      // OIDC holds now.
      final fresh = await client.fetch(token, authReqId);
      _request = fresh;
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
      TransactionDetail() => '${d.amount} ${d.assetSymbol} '
          '(${d.fiatAmount} ${d.fiatCurrency}) on ${d.network}',
      LimitsChangeDetail() => 'Limits to ${d.requested.perTransactionEur} EUR '
          'per transaction, ${d.requested.perDayEur} EUR per day',
    };

/// The client as rule 3 allows it to be named.
String clientLabel(ConsentClient c) => c.verified
    ? (c.displayName ?? c.domain ?? c.entityId)
    : 'Unverified application ${c.fingerprint ?? ''}'.trim();
