import 'package:flutter/foundation.dart';

import 'enrollment_state.dart';
import 'enrollment_store.dart';

/// An [EnrollmentStore] that tells listeners when the stored enrollment changed, so the app notices at once when this device signs itself out (cancelling its own enrollment in Devices) instead of at the next tab change.
///
/// It is also the one place that copes with storage that fails: an enrollment whose write failed is kept in memory and saved again later, and a sign-out whose clear failed still holds for the rest of the session. Reads, writes and clears of the enrollment run one at a time, in the order they were asked for, so a slow write can never land after a sign-out that was asked for later.
class ObservableEnrollmentStore extends ChangeNotifier
    implements EnrollmentStore {
  ObservableEnrollmentStore(this._inner, {DateTime Function()? clock})
      : _clock = clock ?? DateTime.now;

  final EnrollmentStore _inner;
  final DateTime Function() _clock;

  /// How many times the enrollment was deliberately cleared. A record that is merely missing (an enrollment kept in memory after a failed write) is not a clear.
  int get clearCount => _clearCount;
  int _clearCount = 0;

  /// An enrollment whose write failed. The server has it, so the whole app (the pending list, settings, push) works from this copy until it is saved.
  EnrollmentRecord? _unsaved;
  DateTime? _lastSaveTry;

  /// How often a read tries again to save [_unsaved]; reads are frequent (the pending list polls).
  static const saveRetryEvery = Duration(seconds: 30);

  /// The last clear did not reach the storage: what is on disk is the enrollment that was signed out, so it is never handed out, and the clear is tried again.
  bool _clearFailed = false;

  Future<void> _tail = Future<void>.value();

  /// Steps asked for and not finished yet.
  int _queued = 0;

  /// Runs [step] after every step asked for before it; at once when none is left.
  Future<T> _serial<T>(Future<T> Function() step) {
    Future<T> run() async {
      try {
        return await step();
      } finally {
        _queued--;
      }
    }

    final result = _queued == 0 ? run() : _tail.then((_) => run());
    _queued++;
    _tail = result.then((_) {}, onError: (Object _) {});
    return result;
  }

  @override
  Future<EnrollmentRecord?> read() => _serial(() async {
        if (_clearFailed) {
          try {
            await _inner.clear();
            _clearFailed = false;
          } on Object {
            return _unsaved;
          }
        }
        final stored = await _inner.read();
        final unsaved = _unsaved;
        if (stored != null || unsaved == null) return stored;
        final last = _lastSaveTry;
        if (last == null || _clock().difference(last) >= saveRetryEvery) {
          _lastSaveTry = _clock();
          try {
            await _inner.write(unsaved);
            _unsaved = null;
          } on Object {
            // Still in memory only.
          }
        }
        return unsaved;
      });

  @override
  Future<void> write(EnrollmentRecord record) async {
    await _serial(() async {
      try {
        await _inner.write(record);
      } on Object {
        _unsaved = record;
        _lastSaveTry = _clock();
        rethrow;
      }
      _unsaved = null;
      // A new enrollment replaced what a failed clear left behind.
      _clearFailed = false;
    });
    notifyListeners();
  }

  /// Announced at once, before the storage is cleared and even when that fails: the device was signed out on purpose, and the app must stop using it now, not after a read that is still running (a slow keystore). Reads asked for from here on answer "not enrolled".
  @override
  Future<void> clear() {
    _clearCount++;
    _unsaved = null;
    final done = _serial(() async {
      _unsaved = null;
      _clearFailed = true;
      await _inner.clear();
      _clearFailed = false;
    });
    notifyListeners();
    return done;
  }

  @override
  Future<String?> readAlias() => _inner.readAlias();

  @override
  Future<void> writeAlias(String alias) => _inner.writeAlias(alias);

  @override
  Future<void> clearAlias() => _inner.clearAlias();
}
