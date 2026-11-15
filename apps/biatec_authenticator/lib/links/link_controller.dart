import 'package:flutter/foundation.dart';

import '../approvals/approval_client.dart';
import '../approvals/approval_controller.dart' show clientLabel;
import '../approvals/device_identity.dart';
import '../approvals/history_store.dart';
import '../approvals/receipt.dart';
import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import 'link_code.dart';
import 'link_receipt.dart';
import '../approvals/models/link_request.dart';
import '../text/rp_strings.dart';

enum LinkPhase {
  /// Fetching the link.
  loading,

  /// The screen is shown and waits for the user.
  ready,

  /// Biometric check, signing or posting.
  working,
  linked,
  denied,

  /// The third wrong code closed the link.
  closed,

  /// Not found, expired or already used (one neutral message).
  gone,

  /// The link could not be loaded or this device cannot decide.
  unavailable,
}

final _codeFormat = RegExp(r'^[0-9]{6}$');

/// Drives one link: fetch the link, the screen, the code the user types, the operating system's biometric prompt, the signed statement. Biatec OIDC is the authority on the code and its three
/// attempts; the controller mirrors what it answers. Nothing is ever sent before the user presses a button: the code is never submitted by itself.
class LinkController extends ChangeNotifier {
  LinkController({
    required this.code,
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.history,
    this.onLinked,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final LinkCode code;
  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final HistoryStore history;

  /// Called once after the service was linked (the settings reload their list).
  final VoidCallback? onLinked;
  final DateTime Function() _clock;

  DateTime get now => _clock();

  LinkPhase _phase = LinkPhase.loading;
  LinkPhase get phase => _phase;

  LinkRequest? _request;

  /// The link exactly as last fetched. The screen renders only this.
  LinkRequest? get request => _request;

  String? _error;

  /// Why the last step did not go through, in words, or null.
  String? get error => _error;

  String? _unavailable;

  /// Why the link could not be loaded (phase [LinkPhase.unavailable]).
  String? get unavailableReason => _unavailable;

  bool _offline = false;
  bool get offline => _offline;

  int? _attemptsRemaining;

  /// Attempts left as Biatec OIDC last reported them, or null before the first wrong code.
  int? get attemptsRemaining => _attemptsRemaining;

  bool _disposed = false;

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }

  void _set(LinkPhase phase, {String? error, String? unavailable, bool offline = false}) {
    if (_disposed) return;
    _phase = phase;
    _error = error;
    _unavailable = unavailable;
    _offline = offline;
    notifyListeners();
  }

  /// Loads the link. Call on open and to retry. Only the enrolled server is asked (the issuer was checked against it when the code was parsed).
  Future<void> load() async {
    if (_phase == LinkPhase.working) return;
    _set(LinkPhase.loading);
    try {
      final record = await enrollment.read();
      if (record == null) {
        throw const ApprovalException('This device is not enrolled.');
      }
      final request =
          await client.fetchLink(record.deviceToken, code.linkId, code.secret);
      _request = request;
      _set(request.isExpired(now) ? LinkPhase.gone : LinkPhase.ready);
    } on LinkException catch (e) {
      _request = null;
      final s = RpStrings.of();
      switch (e.problem) {
        case LinkProblem.gone:
          _set(LinkPhase.gone);
        case LinkProblem.notActive:
          _set(LinkPhase.unavailable, unavailable: s.t('notActive'));
        case LinkProblem.cannotShow:
          _set(LinkPhase.unavailable, unavailable: s.t('cannotShowLink'));
        case LinkProblem.other:
          _set(LinkPhase.unavailable, unavailable: e.message);
      }
    } on ApprovalException catch (e) {
      _set(LinkPhase.unavailable, unavailable: e.message, offline: e.offline);
    } on Object catch (e) {
      _set(LinkPhase.unavailable, unavailable: '$e');
    }
  }

  /// The enrolled, active device and its keys (two-key mode only: a link statement is signed by the approval key). Throws [ApprovalException].
  Future<(String, DeviceIdentity)> _device() async {
    final record = await enrollment.read();
    if (record == null) {
      throw const ApprovalException('This device is not enrolled.');
    }
    if (record.activeAt.isAfter(now)) {
      throw ApprovalException(RpStrings.of().t('notActive'));
    }
    return (record.deviceToken, await twoKeyIdentityOf(record, keys));
  }

  /// Links the service: the user typed [typed] (the code the service shows next to its QR code) and pressed Link. A service without a proven domain is linked only with [confirmedUnproven], which the screen
  /// sets after the extra confirmation. One biometric prompt, for the approval key.
  Future<void> approve(String typed, {bool confirmedUnproven = false}) async {
    final shown = _request;
    if (_phase != LinkPhase.ready || shown == null) return;
    final s = RpStrings.of();
    typed = typed.trim();
    if (!_codeFormat.hasMatch(typed)) {
      _set(LinkPhase.ready, error: s.t('enterCode'));
      return;
    }
    if (shown.domain == null && !confirmedUnproven) {
      // Never silent: the screen asks for the second confirmation, and anything else that calls this is told why nothing happened.
      _set(LinkPhase.ready, error: s.t('confirmUnproven'));
      return;
    }
    if (shown.isExpired(now)) {
      _set(LinkPhase.gone);
      return;
    }
    _set(LinkPhase.working);
    try {
      final (token, device) = await _device();
      KeysState? state;
      try {
        state = await keys.keysState();
      } on Object {
        // The keystore did not answer: the signature itself will say what is wrong.
      }
      final blocked = cannotApproveWith(device, state);
      if (blocked != null) {
        _set(LinkPhase.ready, error: blocked);
        return;
      }
      final Map<String, Object?> body;
      try {
        body = await signLink(
          keys: keys,
          device: device,
          request: shown,
          code: typed,
          decision: LinkDecision.approve,
          clock: () => now,
          prompt: LinkPrompt(
              title: LinkPrompt.fixedTitle, subtitle: clientLabel(shown.client)),
        );
      } on KeyPromptCancelled {
        // Nothing was signed or sent: the link stays open and no code attempt is used.
        _set(LinkPhase.ready, error: s.t('linkCancelled'));
        return;
      } on ApprovalKeyInvalidated {
        _set(LinkPhase.ready, error: ApprovalKeyInvalidated.message);
        return;
      } on KeyPromptFailed catch (e) {
        _set(LinkPhase.ready, error: 'Could not link: ${e.message}');
        return;
      }
      final outcome =
          await client.postLinkReceipt(token, code.linkId, code.secret, body);
      switch (outcome) {
        case LinkDone(status: 'linked'):
          await _remember(shown, LinkDecision.approve, body);
          _set(LinkPhase.linked);
          onLinked?.call();
        case LinkWrongCode(:final attemptsRemaining):
          _attemptsRemaining = attemptsRemaining;
          _set(LinkPhase.ready,
              error: switch (attemptsRemaining) {
                null => s.t('wrongCode'),
                1 => s.t('wrongCodeOne'),
                final n => s.t('wrongCodeN', {'n': '$n'}),
              });
        case LinkClosed():
          _attemptsRemaining = 0;
          _set(LinkPhase.closed);
        case LinkDone():
        case LinkRefused():
          _refused(outcome);
      }
    } on ApprovalException catch (e) {
      _set(LinkPhase.ready, error: e.message, offline: e.offline);
    } on Object catch (e) {
      _set(LinkPhase.ready, error: 'Could not link: $e');
    }
  }

  /// Does not link: signs a deny with the presence key (no prompt: refusing is always easy). A deny needs no code (the schema allows an empty one and the server never compares it), and the
  /// code the user typed, which may be a wrong one, is never put in a statement that says "no": it carries the empty code.
  Future<void> deny() async {
    final shown = _request;
    if (_phase != LinkPhase.ready || shown == null) return;
    _set(LinkPhase.working);
    try {
      final (token, device) = await _device();
      final body = await signLink(
        keys: keys,
        device: device,
        request: shown,
        code: '',
        decision: LinkDecision.deny,
        clock: () => now,
      );
      final outcome =
          await client.postLinkReceipt(token, code.linkId, code.secret, body);
      switch (outcome) {
        case LinkDone(status: 'denied'):
          await _remember(shown, LinkDecision.deny, body);
          _set(LinkPhase.denied);
        case LinkClosed():
          _set(LinkPhase.closed);
        case LinkWrongCode():
          // A server that compares the code of a deny: said, not guessed at, and nothing was recorded.
          _set(LinkPhase.ready, error: RpStrings.of().t('wrongCode'));
        case LinkDone():
        case LinkRefused():
          _refused(outcome);
      }
    } on ApprovalException catch (e) {
      _set(LinkPhase.ready, error: e.message, offline: e.offline);
    } on Object catch (e) {
      _set(LinkPhase.ready, error: 'Could not decline: $e');
    }
  }

  void _refused(LinkOutcome outcome) {
    if (outcome is LinkRefused &&
        (outcome.status == 410 ||
            outcome.status == 404 ||
            outcome.status == 409)) {
      _set(LinkPhase.gone);
    } else if (outcome is LinkRefused) {
      _set(LinkPhase.ready,
          error: 'Refused (${outcome.status}): ${outcome.description}');
    } else {
      _set(LinkPhase.ready, error: 'Unexpected answer from Biatec OIDC.');
    }
  }

  Future<void> _remember(
      LinkRequest request, LinkDecision decision, Map<String, Object?> body) async {
    try {
      final signed = (body['signed'] as Map).cast<String, Object?>();
      await history.add(HistoryEntry(
        authReqId: request.linkId,
        decision: decision.wire,
        summary: 'Link a service',
        clientLabel: clientLabel(request.client),
        decidedAt: now,
        receipt: {
          'version': 'link',
          'signed': signed,
          'alg': 'ES256',
          'signature': body['signature'],
          'device': {'kid': signed['kid']},
        },
      ));
    } on Object {
      // History is a convenience; the server holds the statement.
    }
  }
}
