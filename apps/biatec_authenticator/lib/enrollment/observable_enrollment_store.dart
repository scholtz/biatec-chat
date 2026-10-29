import 'package:flutter/foundation.dart';

import 'enrollment_state.dart';
import 'enrollment_store.dart';

/// An [EnrollmentStore] that tells listeners when the stored enrollment changed, so the app notices at once when this device signs itself out (cancelling its own enrollment in Devices) instead of at the next tab change.
///
/// It is also the one place that copes with storage that fails: an enrollment whose write failed is kept in memory and saved again later, and a sign-out whose clear failed still holds for the rest of the session. Writes and clears reach the storage one at a time, in the order they were asked for, so a slow write can never land after a sign-out that was asked for later. Reads do not wait in that line: a keystore that hangs on one read must not hold up the next.
class ObservableEnrollmentStore extends ChangeNotifier
    implements EnrollmentStore {
  ObservableEnrollmentStore(this._inner, {DateTime Function()? clock})
      : _clock = clock ?? DateTime.now;

  final EnrollmentStore _inner;
  final DateTime Function() _clock;

  /// How many times the enrollment was deliberately cleared. A record that is merely missing is not a clear.
  int get clearCount => _clearCount;
  int _clearCount = 0;

  /// The last enrollment written, when its write failed. It is newer than anything on disk (the server has it), so the whole app works from it until it is saved.
  EnrollmentRecord? _unsaved;
  DateTime? _lastSaveTry;

  /// How often a read tries again to save [_unsaved]; reads are frequent (the pending list polls).
  static const saveRetryEvery = Duration(seconds: 30);

  /// The last clear did not reach the storage: what is on disk is the enrollment that was signed out, so it is never handed out, and the clear is tried again.
  bool _clearFailed = false;

  /// The last write or clear asked for, while one is unfinished.
  Future<void>? _tail;
  int _queued = 0;

  /// Runs [step] after every write or clear asked for before it. With none unfinished it starts at once rather than after an old finished future: such a future calls back in the zone it completed in, which need not be running any more.
  Future<T> _serial<T>(Future<T> Function() step) {
    Future<T> run() async {
      try {
        return await step();
      } finally {
        if (--_queued == 0) _tail = null;
      }
    }

    final previous = _tail;
    _queued++;
    final result = previous == null ? run() : previous.then((_) => run());
    _tail = result.then((_) {}, onError: (Object _) {});
    return result;
  }

  @override
  Future<EnrollmentRecord?> read() async {
    final unsaved = _unsaved;
    if (unsaved != null) {
      _saveLater(unsaved);
      return unsaved;
    }
    if (_clearFailed) {
      _clearLater();
      return null;
    }
    final clears = _clearCount;
    final stored = await _inner.read();
    // Signed out while this read ran: what it found is the old enrollment.
    if (clears != _clearCount) return null;
    return _unsaved ?? stored;
  }

  void _saveLater(EnrollmentRecord record) {
    final last = _lastSaveTry;
    if (last != null && _clock().difference(last) < saveRetryEvery) return;
    _lastSaveTry = _clock();
    _serial(() async {
      if (!identical(_unsaved, record)) return; // replaced or signed out meanwhile
      await _inner.write(record);
      if (identical(_unsaved, record)) _unsaved = null;
    }).ignore();
  }

  void _clearLater() {
    _serial(() async {
      if (!_clearFailed) return;
      await _inner.clear();
      _clearFailed = false;
    }).ignore();
  }

  @override
  Future<void> write(EnrollmentRecord record) async {
    // The newest enrollment from now on, until a later write or clear.
    _unsaved = record;
    _lastSaveTry = _clock();
    await _serial(() async {
      if (!identical(_unsaved, record)) return; // signed out meanwhile
      await _inner.write(record);
      if (identical(_unsaved, record)) {
        _unsaved = null;
        // A new enrollment replaced what a failed clear left behind.
        _clearFailed = false;
      }
    });
    notifyListeners();
  }

  /// Announced at once, before the storage is cleared and even when that fails: the device was signed out on purpose, and the app must stop using it now. Reads from here on answer "not enrolled".
  @override
  Future<void> clear() {
    _clearCount++;
    _unsaved = null;
    _clearFailed = true;
    final done = _serial(() async {
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
