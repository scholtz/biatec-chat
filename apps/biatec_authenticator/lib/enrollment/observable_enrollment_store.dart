import 'package:flutter/foundation.dart';

import 'enrollment_state.dart';
import 'enrollment_store.dart';

/// An [EnrollmentStore] that tells listeners when the stored enrollment changed, so the app notices at once when this device signs itself out (cancelling its own enrollment in Devices) instead of at the next tab change.
///
/// It is also the one place that copes with storage that fails: an enrollment whose write failed is kept in memory and saved again later, and a sign-out whose clear failed still holds for the rest of the session.
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

  /// The last clear did not reach the storage: reads answer "not enrolled" anyway and try the clear again.
  bool _clearFailed = false;

  @override
  Future<EnrollmentRecord?> read() async {
    if (_clearFailed) {
      await _clearInner();
      return null;
    }
    final clears = _clearCount;
    final stored = await _inner.read();
    if (clears != _clearCount) return null; // signed out meanwhile
    final unsaved = _unsaved;
    if (stored != null || unsaved == null) return stored;
    final last = _lastSaveTry;
    if (last == null || _clock().difference(last) >= saveRetryEvery) {
      _lastSaveTry = _clock();
      await _save(unsaved, clears);
    }
    return clears == _clearCount ? unsaved : null;
  }

  /// Writes [record] unless a clear has happened since [clears]; a write that lands after one is taken out again, as the sign-out wins.
  Future<bool> _save(EnrollmentRecord record, int clears) async {
    try {
      await _inner.write(record);
    } on Object {
      return false;
    }
    if (clears != _clearCount) {
      await _clearInner();
      return false;
    }
    if (identical(_unsaved, record)) _unsaved = null;
    return true;
  }

  @override
  Future<void> write(EnrollmentRecord record) async {
    final clears = _clearCount;
    try {
      await _inner.write(record);
    } on Object {
      if (clears == _clearCount) {
        _unsaved = record;
        _lastSaveTry = _clock();
      }
      rethrow;
    }
    if (clears != _clearCount) {
      // Signed out while this was being written.
      await _clearInner();
      return;
    }
    _unsaved = null;
    // A new enrollment replaced what a failed clear left behind.
    _clearFailed = false;
    notifyListeners();
  }

  /// Announced even when clearing the storage fails: the device was signed out on purpose, and the app must stop using it now.
  @override
  Future<void> clear() async {
    _clearCount++;
    _unsaved = null;
    _clearFailed = true;
    try {
      await _inner.clear();
      _clearFailed = false;
    } finally {
      notifyListeners();
    }
  }

  Future<void> _clearInner() async {
    try {
      await _inner.clear();
      _clearFailed = false;
    } on Object {
      _clearFailed = true;
    }
  }

  @override
  Future<String?> readAlias() => _inner.readAlias();

  @override
  Future<void> writeAlias(String alias) => _inner.writeAlias(alias);

  @override
  Future<void> clearAlias() => _inner.clearAlias();
}
